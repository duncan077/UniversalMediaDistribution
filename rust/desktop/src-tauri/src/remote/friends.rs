use super::{json_response, RemoteState, Session};
use crate::CommandResult;
use reqwest::Method;
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};
use tauri::State;
use uuid::Uuid;

#[derive(Deserialize)]
#[serde(
    tag = "kind",
    rename_all = "camelCase",
    rename_all_fields = "camelCase"
)]
pub enum FriendAction {
    State,
    Send {
        username: String,
    },
    Accept {
        id: Uuid,
    },
    Dismiss {
        id: Uuid,
    },
    Remove {
        id: Uuid,
    },
    Sharing {
        id: Uuid,
        enabled: bool,
    },
    SetGlobal {
        enabled: bool,
    },
    SetDiscovery {
        enabled: bool,
    },
    Hide {
        key: String,
        hidden: bool,
    },
    Compare {
        id: Uuid,
    },
    Activity {
        media_type: String,
        title: String,
        subtype: Option<String>,
    },
}

#[derive(Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
struct Visibility {
    share_progress: bool,
    discoverable_by_username: bool,
    hidden_item_keys: Vec<String>,
}

async fn request(
    s: &Session,
    method: Method,
    suffix: &str,
    body: Option<Value>,
) -> CommandResult<Value> {
    let url = s
        .base
        .join(&format!("api/v1/friends{suffix}"))
        .map_err(|e| e.to_string())?;
    let mut req = s.client.request(method, url).bearer_auth(&s.token);
    if let Some(body) = body {
        req = req.json(&body);
    }
    let response = req.send().await.map_err(|e| e.to_string())?;
    if response.status() == reqwest::StatusCode::NO_CONTENT {
        return Ok(Value::Null);
    }
    json_response(response).await
}

async fn visibility(s: &Session) -> CommandResult<Visibility> {
    serde_json::from_value(request(s, Method::GET, "/visibility", None).await?)
        .map_err(|_| "El servidor devolvió opciones de privacidad inválidas.".into())
}

#[tauri::command]
pub async fn friend_action(
    profile_id: Uuid,
    action: FriendAction,
    remote: State<'_, RemoteState>,
) -> CommandResult<Value> {
    let sessions = remote.0.lock().await;
    let s = sessions
        .get(&profile_id)
        .ok_or("Iniciá sesión en Cuenta y respaldo para usar Amigos.")?;
    match action {
        FriendAction::State => {
            let (friends, requests, settings) = tokio::try_join!(
                request(s, Method::GET, "", None),
                request(s, Method::GET, "/requests", None),
                visibility(s)
            )?;
            Ok(json!({"friends":friends,"requests":requests,"visibility":settings}))
        }
        FriendAction::Send { username } => {
            request(
                s,
                Method::POST,
                "/requests",
                Some(json!({"username":username.trim()})),
            )
            .await
        }
        FriendAction::Accept { id } => {
            request(s, Method::POST, &format!("/requests/{id}/accept"), None).await
        }
        FriendAction::Dismiss { id } => {
            request(s, Method::DELETE, &format!("/requests/{id}"), None).await
        }
        FriendAction::Remove { id } => request(s, Method::DELETE, &format!("/{id}"), None).await,
        FriendAction::Sharing { id, enabled } => {
            request(
                s,
                Method::PUT,
                &format!("/{id}/sharing"),
                Some(json!({"shareProgress":enabled})),
            )
            .await
        }
        FriendAction::Compare { id } => {
            request(s, Method::GET, &format!("/{id}/comparison"), None).await
        }
        FriendAction::Activity {
            media_type,
            title,
            subtype,
        } => {
            let key = umd_core::friend_item_key(&media_type, &title);
            let scoped = umd_core::friend_scoped_item_key(&media_type, &title, subtype.as_deref());
            let keys = if scoped == key {
                vec![key.clone()]
            } else {
                vec![key.clone(), scoped.clone()]
            };
            let (activity, settings) = tokio::try_join!(
                request(
                    s,
                    Method::POST,
                    "/activity/lookup",
                    Some(json!({"itemKeys":keys}))
                ),
                visibility(s)
            )?;
            let matches: Vec<_> = activity["matches"]
                .as_array()
                .into_iter()
                .flatten()
                .filter(|m| {
                    scoped == key
                        || m["itemKey"] == scoped
                        || (m["itemKey"] == key && m["subtype"].is_null())
                })
                .collect();
            Ok(
                json!({"key":key,"matches":matches,"hidden":settings.hidden_item_keys.contains(&key) || settings.hidden_item_keys.contains(&scoped)}),
            )
        }
        change => {
            let mut settings = visibility(s).await?;
            match change {
                FriendAction::SetGlobal { enabled } => settings.share_progress = enabled,
                FriendAction::SetDiscovery { enabled } => {
                    settings.discoverable_by_username = enabled
                }
                FriendAction::Hide { key, hidden } => {
                    settings.hidden_item_keys.retain(|k| k != &key);
                    if hidden {
                        settings.hidden_item_keys.push(key);
                    }
                }
                _ => unreachable!(),
            }
            request(s, Method::PUT, "/visibility", Some(json!(settings))).await
        }
    }
}
