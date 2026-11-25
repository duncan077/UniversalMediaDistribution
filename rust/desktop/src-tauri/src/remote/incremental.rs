use super::{fetch, json_response, PendingSnapshot, Session};
use crate::{work, CommandResult};
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
        .filter(|v| v.revision == since && v.document.is_some())
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
    let mut changes = json_response(response).await?;
    let Some(revision) = changes["toRevision"].as_i64().filter(|r| *r >= since) else {
        return fetch(session).await;
    };
    let delta = changes["document"].take();
    if changes["fromRevision"] != since || !delta.is_object() {
        return fetch(session).await;
    }
    let Some(full) = changes["full"].as_bool() else {
        return fetch(session).await;
    };
    if !full && revision == since {
        if !umd_core::backup_patch_empty(&delta)
            || base.exported_at != delta["exportedAt"]
            || base.device_id != delta["deviceId"]
        {
            return fetch(session).await;
        }
        return Ok(base.summary.clone());
    }
    let compressed = base.document.clone();
    let snapshot = work(move || {
        let document = if full {
            umd_core::validate_account_backup(&delta, umd_core::MAX_DOCUMENT_BYTES)
                .map_err(|e| e.to_string())?;
            delta
        } else {
            let base =
                PendingSnapshot::decode(compressed.as_deref().ok_or("Falta el respaldo base.")?)?;
            let Ok(Some(merged)) = umd_core::apply_received_changes(&base, &delta) else {
                return Ok(None);
            };
            merged
        };
        let stamp = changes
            .get("updatedAt")
            .unwrap_or(&document["exportedAt"])
            .clone();
        let mut snapshot = json!({"revision":revision,"updatedAt":stamp});
        snapshot["document"] = document;
        PendingSnapshot::encode(&snapshot).map(Some)
    })
    .await?;
    let Some(snapshot) = snapshot else {
        return fetch(session).await;
    };
    let info = snapshot.summary.clone();
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
        let mut baseline = local.backup().unwrap();
        let unrelated = uuid::Uuid::new_v4();
        baseline["catalog"]["items"] = json!([{
            "syncId": unrelated, "name":"Solo en el catálogo", "mediaType":"book", "subtype":"manga"
        }]);
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
                        assert_eq!(body["patch"]["catalog"]["items"], json!([]));
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
            pending: Some(
                PendingSnapshot::encode(&json!({"document":baseline,"revision":1})).unwrap(),
            ),
            automatic: false,
            incremental: true,
            gzip: false,
            delta_base: Some(1),
            party_socket: None,
            etag: None,
            last_sync: None,
        };
        let result = super::super::synchronize(&mut session, &local, false)
            .await
            .unwrap();
        assert_eq!(result["backup"]["revision"], 3);
        assert_eq!(result["changed"], true);
        assert_eq!(session.revision, Some(3));
        assert_eq!(
            local
                .backup_revision(session.base.as_str(), session.account)
                .unwrap(),
            Some(3)
        );
        assert_eq!(local.personal_lists().unwrap().len(), 2);
        assert!(local.item(unrelated).unwrap().is_none());
        assert_eq!(
            state.lock().unwrap().1["catalog"]["items"][0]["syncId"],
            json!(unrelated)
        );
        super::super::synchronize(&mut session, &local, false)
            .await
            .unwrap();
        assert_eq!(state.lock().unwrap().2, 1);
        server.abort();
    }
}
