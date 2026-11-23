use super::{fetch, json_response, summary, Session};
use crate::CommandResult;
use reqwest::{Client, StatusCode, Url};
use serde_json::{json, Value};

/// What the server said it supports: incremental sync, and gzip request bodies.
pub(super) async fn available(client: &Client, base: &Url) -> (bool, bool) {
    let Ok(url) = base.join("api/v1/capabilities") else {
        return (false, false);
    };
    let Ok(response) = client.get(url).send().await else {
        return (false, false);
    };
    json_response(response).await.map_or((false, false), |v| {
        (
            v["deltaSync"] == true && v["progressPatch"] == true,
            v["gzipSyncRequests"] == true,
        )
    })
}

pub(super) async fn fetch_latest(session: &mut Session) -> CommandResult<Value> {
    let Some(since) = session.delta_base.filter(|_| session.incremental) else {
        return fetch(session).await;
    };
    let Some(base) = session
        .pending
        .as_ref()
        .filter(|v| v["revision"] == since && v["document"].is_object())
    else {
        return fetch(session).await;
    };
    let response = session
        .client
        .get(session.base.join("api/v1/sync/changes").unwrap())
        .query(&[("since", since)])
        .bearer_auth(&session.token)
        .send()
        .await
        .map_err(|e| e.to_string())?;
    if matches!(
        response.status(),
        StatusCode::NOT_FOUND | StatusCode::METHOD_NOT_ALLOWED | StatusCode::NOT_IMPLEMENTED
    ) {
        return fetch(session).await;
    }
    let changes = json_response(response).await?;
    let Some(revision) = changes["toRevision"].as_i64().filter(|r| *r >= since) else {
        return fetch(session).await;
    };
    let delta = &changes["document"];
    if changes["fromRevision"] != since || !delta.is_object() {
        return fetch(session).await;
    }
    let document = match changes["full"].as_bool() {
        Some(true) => {
            umd_core::validate_backup(delta).map_err(|e| e.to_string())?;
            delta.clone()
        }
        Some(false) if revision == since => {
            if !umd_core::backup_patch_empty(delta)
                || base["document"]["exportedAt"] != delta["exportedAt"]
                || base["document"]["deviceId"] != delta["deviceId"]
            {
                return fetch(session).await;
            }
            return Ok(summary(base));
        }
        Some(false) => {
            let Ok(Some(merged)) = umd_core::apply_received_changes(&base["document"], delta)
            else {
                return fetch(session).await;
            };
            merged
        }
        None => return fetch(session).await,
    };
    let stamp = changes.get("updatedAt").unwrap_or(&document["exportedAt"]);
    let snapshot = json!({"document":document,"revision":revision,"updatedAt":stamp,"updatedByDeviceId":document["deviceId"]});
    let info = summary(&snapshot);
    session.delta_base = Some(revision);
    session.pending = Some(snapshot);
    session.etag = None;
    Ok(info)
}

pub(super) enum Write {
    Saved(i64),
    Full,
}
pub(super) async fn send_patch(
    session: &Session,
    expected: i64,
    patch: Value,
) -> CommandResult<Write> {
    let request = session
        .client
        .patch(session.base.join("api/v1/sync/snapshot/progress").unwrap())
        .bearer_auth(&session.token);
    let response = super::sync_body(
        request,
        session.gzip,
        json!({"expectedRevision":expected,"patch":patch}),
    )
    .await?
    .send()
    .await
    .map_err(|e| e.to_string())?;
    match response.status() {
        StatusCode::CONFLICT
        | StatusCode::BAD_REQUEST
        | StatusCode::PAYLOAD_TOO_LARGE
        | StatusCode::UNPROCESSABLE_ENTITY
        | StatusCode::NOT_FOUND
        | StatusCode::METHOD_NOT_ALLOWED
        | StatusCode::NOT_IMPLEMENTED => Ok(Write::Full),
        _ => {
            let receipt = json_response(response).await?;
            let revision = receipt["revision"]
                .as_i64()
                .filter(|r| *r > expected)
                .ok_or("El servidor devolvió una revisión inválida.")?;
            Ok(Write::Saved(revision))
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use axum::{
        extract::{Query, State},
        routing::{get, patch},
        Json, Router,
    };
    use std::{
        collections::HashMap,
        sync::{Arc, Mutex},
    };
    use umd_core::{CatalogStore, ListEdit};

    #[tokio::test]
    async fn rereads_a_concurrent_devices_edit_before_acknowledging_a_patch() {
        let local = CatalogStore::in_memory().unwrap();
        let baseline = local.backup().unwrap();
        let peer = CatalogStore::in_memory().unwrap();
        peer.restore_backup(&baseline).unwrap();
        peer.edit_list(ListEdit::Create {
            name: "Otro equipo".into(),
        })
        .unwrap();
        let competing = peer.backup().unwrap();
        local
            .edit_list(ListEdit::Create {
                name: "Este equipo".into(),
            })
            .unwrap();
        type Remote = Arc<Mutex<(i64, Value, usize)>>;
        let state: Remote = Arc::new(Mutex::new((1, baseline.clone(), 0)));
        let initial = baseline.clone();
        let service = Router::new()
            .route(
                "/api/v1/sync/changes",
                get(
                    move |State(s): State<Remote>, Query(q): Query<HashMap<String, i64>>| {
                        let initial = initial.clone();
                        async move {
                            let remote = s.lock().unwrap();
                            let since = q["since"];
                            let base = if since == remote.0 {
                                &remote.1
                            } else {
                                &initial
                            };
                            Json(
                                json!({"fromRevision":since,"toRevision":remote.0,"full":false,
                        "document":umd_core::backup_patch(base,&remote.1).unwrap()}),
                            )
                        }
                    },
                ),
            )
            .route(
                "/api/v1/sync/snapshot/progress",
                patch(move |State(s): State<Remote>, Json(body): Json<Value>| {
                    let competing = competing.clone();
                    async move {
                        let mut remote = s.lock().unwrap();
                        assert_eq!(body["expectedRevision"], 1);
                        assert_eq!(body["patch"]["lists"].as_array().unwrap().len(), 1);
                        remote.1 = umd_core::merge_backup_patch(&competing, &body["patch"])
                            .unwrap()
                            .unwrap();
                        remote.0 = 3;
                        remote.2 += 1;
                        Json(json!({"revision":3}))
                    }
                }),
            )
            .with_state(state.clone());
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let base = super::super::private_url(&format!("http://{}", listener.local_addr().unwrap()))
            .unwrap();
        let server = tokio::spawn(async move { axum::serve(listener, service).await.unwrap() });
        let mut session = Session {
            user: Value::Null,
            account: uuid::Uuid::new_v4(),
            base,
            client: Client::new(),
            token: "test".into(),
            revision: Some(1),
            pending: Some(json!({"document":baseline,"revision":1})),
            automatic: false,
            incremental: true,
            gzip: false,
            delta_base: Some(1),
            party_socket: None,
            etag: None,
            last_sync: None,
        };
        let result = super::super::synchronize(&mut session, &local)
            .await
            .unwrap();
        assert_eq!(result["backup"]["revision"], 3);
        assert_eq!(result["changed"], true);
        assert_eq!(local.personal_lists().unwrap().len(), 2);
        super::super::synchronize(&mut session, &local)
            .await
            .unwrap();
        assert_eq!(state.lock().unwrap().2, 1);
        server.abort();
    }
}
