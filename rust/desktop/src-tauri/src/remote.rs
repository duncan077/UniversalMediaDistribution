use crate::{private_files::PrivateFiles, work, CommandResult};
use serde_json::{json, Value};
use tauri::State;
use umd_core::{CatalogStore, MAX_DOCUMENT_BYTES};
pub(crate) mod friends;
mod incremental;
pub(crate) mod party;
pub(crate) mod shared_lists;

#[derive(Default)]
pub struct RemoteState(tokio::sync::Mutex<std::collections::HashMap<uuid::Uuid, Session>>);
struct Session {
    user: Value,
    account: uuid::Uuid,
    base: reqwest::Url,
    client: reqwest::Client,
    token: String,
    revision: Option<i64>,
    pending: Option<Value>,
    automatic: bool,
    incremental: bool,
    gzip: bool,
    delta_base: Option<i64>,
    party_socket: Option<party::Connection>,
    etag: Option<(i64, reqwest::header::HeaderValue)>,
    last_sync: Option<(i64, (u64, i64))>,
}
/// Serialises a sync body off the async runtime and gzips it for servers that
/// advertise `gzipSyncRequests`. A snapshot shrinks roughly tenfold.
pub(crate) async fn sync_body(
    request: reqwest::RequestBuilder,
    gzip: bool,
    body: Value,
) -> CommandResult<reqwest::RequestBuilder> {
    let bytes = work(move || {
        let plain = serde_json::to_vec(&body).map_err(|e| e.to_string())?;
        if !gzip {
            return Ok(plain);
        }
        use std::io::Write;
        let mut encoder = flate2::write::GzEncoder::new(
            Vec::with_capacity(plain.len() / 6),
            flate2::Compression::fast(),
        );
        encoder.write_all(&plain).map_err(|e| e.to_string())?;
        encoder.finish().map_err(|e| e.to_string())
    })
    .await?;
    let request = request.header(reqwest::header::CONTENT_TYPE, "application/json");
    Ok(if gzip {
        request.header(reqwest::header::CONTENT_ENCODING, "gzip")
    } else {
        request
    }
    .body(bytes))
}
pub(crate) async fn json_response(mut response: reqwest::Response) -> CommandResult<Value> {
    let status = response.status();
    let mut bytes = Vec::new();
    while let Some(chunk) = response.chunk().await.map_err(|e| e.to_string())? {
        if bytes.len() + chunk.len() > MAX_DOCUMENT_BYTES {
            return Err("El respaldo del servidor supera 32 MiB.".into());
        }
        bytes.extend_from_slice(&chunk);
    }
    if !status.is_success() {
        let body = serde_json::from_slice::<Value>(&bytes).unwrap_or(Value::Null);
        let reason = body["message"]
            .as_str()
            .unwrap_or(status.canonical_reason().unwrap_or("Error del servidor"));
        return Err(format!("HTTP {status}: {reason}"));
    }
    serde_json::from_slice(&bytes)
        .map_err(|_| format!("Respuesta inválida del servidor ({status})."))
}
pub(crate) fn private_url(value: &str) -> CommandResult<reqwest::Url> {
    let mut base = reqwest::Url::parse(value.trim()).map_err(|e| e.to_string())?;
    if !base.username().is_empty()
        || base.password().is_some()
        || base.host_str().is_none()
        || !matches!(base.scheme(), "http" | "https")
    {
        return Err("Dirección de servidor inválida.".into());
    }
    if base.scheme() == "http"
        && !matches!(
            base.host_str(),
            Some("localhost" | "127.0.0.1" | "[::1]" | "::1")
        )
    {
        return Err("Para iniciar sesión fuera de esta computadora, usá HTTPS.".into());
    }
    base.set_query(None);
    base.set_fragment(None);
    if !base.path().ends_with('/') {
        base.set_path(&format!("{}/", base.path()));
    }
    Ok(base)
}
#[derive(serde::Serialize, serde::Deserialize)]
struct SavedSession {
    base: String,
    account: uuid::Uuid,
    user: Value,
    token: String,
    expires: String,
    incremental: bool,
    gzip: bool,
    automatic: bool,
}
fn session_client() -> CommandResult<reqwest::Client> {
    reqwest::Client::builder()
        .timeout(std::time::Duration::from_secs(45))
        .redirect(reqwest::redirect::Policy::none())
        .build()
        .map_err(|e| e.to_string())
}
#[tauri::command]
#[allow(clippy::too_many_arguments)]
pub async fn account_login(
    profile_id: uuid::Uuid,
    base_url: String,
    username: String,
    password: String,
    register: bool,
    display_name: Option<String>,
    device_name: Option<String>,
    files: State<'_, PrivateFiles>,
    store: State<'_, CatalogStore>,
    remote: State<'_, RemoteState>,
) -> CommandResult<Value> {
    let mut state = remote.0.lock().await;
    let store = store.for_profile(profile_id).map_err(|e| e.to_string())?;
    let base = private_url(&base_url)?;
    let client = session_client()?;
    let path = if register {
        "api/v1/accounts/register"
    } else {
        "api/v1/accounts/login"
    };
    let result=json_response(client.post(base.join(path).map_err(|e|e.to_string())?).json(&json!({"username":username,"password":password,"displayName":display_name.unwrap_or_else(|| username.clone()),"deviceName":device_name.as_deref().unwrap_or("UMD Desktop")})).send().await.map_err(|e|e.to_string())?).await?;
    let token = result["accessToken"]
        .as_str()
        .ok_or("El servidor no devolvió una sesión.")?
        .to_owned();
    let account = result["accountId"]
        .as_str()
        .and_then(|raw| uuid::Uuid::parse_str(raw).ok())
        .filter(|id| !id.is_nil())
        .ok_or("El servidor devolvió una cuenta inválida.")?;
    let revision = store
        .backup_revision(base.as_str(), account)
        .map_err(|e| e.to_string())?;
    let (incremental, gzip) = incremental::available(&client, &base).await;
    let mut session = Session {
        user: json!({"username":result["username"],"displayName":result["displayName"]}),
        account,
        base,
        client,
        token,
        revision,
        pending: None,
        automatic: false,
        incremental,
        gzip,
        delta_base: None,
        party_socket: None,
        etag: None,
        last_sync: None,
    };
    let backup = fetch(&mut session).await;
    if backup.as_ref().is_ok_and(|info| info["hasBackup"] == true) {
        store
            .remember_sync_origin(session.base.as_str(), account)
            .map_err(|e| e.to_string())?;
    }
    let backup_error = backup.as_ref().err().cloned();
    let expires = result["expiresAt"].as_str().unwrap_or("");
    if chrono::DateTime::parse_from_rfc3339(expires).is_ok() {
        files.save(
            profile_id,
            "session",
            &SavedSession {
                base: session.base.to_string(),
                account,
                user: session.user.clone(),
                token: session.token.clone(),
                expires: expires.into(),
                incremental,
                gzip,
                automatic: false,
            },
        )?;
    } else {
        files.remove(profile_id, "session")?;
    }
    // Revoke a previous session when replacing the in-memory account.
    if let Some(old) = state.remove(&profile_id) {
        let _ = old
            .client
            .delete(old.base.join("api/v1/accounts/session").unwrap())
            .bearer_auth(old.token)
            .send()
            .await;
    }
    state.insert(profile_id, session);
    Ok(
        json!({"username":result["username"],"displayName":result["displayName"],"backup":backup.ok(),"backupError":backup_error,"automatic":false}),
    )
}
async fn fetch(session: &mut Session) -> CommandResult<Value> {
    let mut request = session
        .client
        .get(session.base.join("api/v1/sync/snapshot").unwrap())
        .bearer_auth(&session.token);
    if let (Some((revision, tag)), Some(snapshot)) = (&session.etag, &session.pending) {
        if snapshot["revision"].as_i64() == Some(*revision) {
            request = request.header(reqwest::header::IF_NONE_MATCH, tag);
        }
    }
    let response = request.send().await.map_err(|e| e.to_string())?;
    if response.status() == reqwest::StatusCode::NOT_MODIFIED {
        return session
            .pending
            .as_ref()
            .map(summary)
            .ok_or("Se recibió una revisión sin respaldo local.".into());
    }
    let tag = response.headers().get(reqwest::header::ETAG).cloned();
    let snapshot = if response.status() == reqwest::StatusCode::NOT_FOUND {
        json!({"revision":0,"document":null})
    } else {
        json_response(response).await?
    };
    if snapshot.get("document").is_none() {
        return Err("El servidor devolvió un respaldo incompleto.".into());
    }
    let revision = snapshot["revision"]
        .as_i64()
        .filter(|v| *v >= 0)
        .ok_or("Revisión inválida.")?;
    if !snapshot["document"].is_null() {
        umd_core::validate_backup(&snapshot["document"]).map_err(|e| e.to_string())?;
    }
    session.delta_base = (!snapshot["document"].is_null()).then_some(revision);
    session.etag = tag.map(|tag| (revision, tag));
    // A nonempty remote backup must be restored before it may be replaced.
    if snapshot["document"].is_null() {
        session.revision = Some(revision);
    }
    if session.pending.as_ref().is_some_and(|old| old != &snapshot) {
        session.last_sync = None;
    }
    let info = summary(&snapshot);
    session.pending = Some(snapshot);
    Ok(info)
}
fn summary(snapshot: &Value) -> Value {
    let doc = &snapshot["document"];
    let item_count = doc["catalog"]["items"]
        .as_array()
        .map(|items| items.iter().filter(|item| item["deleted"] != true).count());
    let favorite_count = doc["favoriteItemStates"]
        .as_array()
        .map(|rows| rows.iter().filter(|row| row["isFavorite"] == true).count());
    let list_count = doc["lists"]
        .as_array()
        .map(|rows| rows.iter().filter(|row| row["isDeleted"] != true).count());
    json!({
        "revision": snapshot["revision"],
        "updatedAt": snapshot["updatedAt"],
        "itemCount": item_count,
        "favoriteCount": favorite_count,
        "listCount": list_count,
        "hasBackup": !doc.is_null()
    })
}
#[tauri::command]
pub async fn account_backup_info(
    profile_id: uuid::Uuid,
    remote: State<'_, RemoteState>,
) -> CommandResult<Value> {
    let mut guard = remote.0.lock().await;
    fetch(guard.get_mut(&profile_id).ok_or("Iniciá sesión primero.")?).await
}
#[tauri::command]
pub async fn account_upload(
    profile_id: uuid::Uuid,
    store: State<'_, CatalogStore>,
    remote: State<'_, RemoteState>,
) -> CommandResult<()> {
    let mut guard = remote.0.lock().await;
    let session = guard.get_mut(&profile_id).ok_or("Iniciá sesión primero.")?;
    if session
        .pending
        .as_ref()
        .is_some_and(|v| v["document"].is_object() && v["revision"].as_i64() != session.revision)
    {
        return Err(
            "El respaldo de la cuenta tiene cambios. Elegí Sincronizar ahora para combinarlos o Restaurar para usar el respaldo.".into(),
        );
    }
    let expected = session
        .revision
        .ok_or("Revisá el respaldo antes de guardar.")?;
    let store = store.for_profile(profile_id).map_err(|e| e.to_string())?;
    let library = store.clone();
    let document = work(move || library.backup().map_err(|e| e.to_string())).await?;
    let request = session
        .client
        .put(session.base.join("api/v1/sync/snapshot").unwrap())
        .bearer_auth(&session.token);
    let result = json_response(
        sync_body(
            request,
            session.gzip,
            json!({"expectedRevision":expected,"document":document}),
        )
        .await?
        .send()
        .await
        .map_err(|e| e.to_string())?,
    )
    .await?;
    let confirmed = result["revision"]
        .as_i64()
        .filter(|n| *n > expected)
        .ok_or("Revisión inválida.")?;
    store
        .remember_upload(session.base.as_str(), session.account, confirmed)
        .map_err(|e| {
            format!(
                "El servidor guardó el respaldo pero no se pudo registrar la revisión local: {e}"
            )
        })?;
    session.revision = Some(confirmed);
    session.pending = None;
    Ok(())
}
#[tauri::command]
pub async fn account_restore(
    profile_id: uuid::Uuid,
    reviewed_revision: i64,
    store: State<'_, CatalogStore>,
    remote: State<'_, RemoteState>,
) -> CommandResult<usize> {
    let mut guard = remote.0.lock().await;
    let session = guard.get_mut(&profile_id).ok_or("Iniciá sesión primero.")?;
    let snapshot = session
        .pending
        .as_ref()
        .ok_or("Revisá el respaldo antes de restaurarlo.")?;
    if snapshot["document"].is_null() {
        return Err("La cuenta todavía no tiene un respaldo.".into());
    }
    let document = snapshot["document"].clone();
    let revision = snapshot["revision"].as_i64().ok_or("Revisión inválida.")?;
    if revision != reviewed_revision {
        return Err("El respaldo cambió. Revisalo antes de restaurar.".into());
    }
    let store = store.for_profile(profile_id).map_err(|e| e.to_string())?;
    let base = session.base.to_string();
    let account = session.account;
    let count = work(move || {
        store
            .restore_account_backup(&document, &base, account, revision)
            .map_err(|e| e.to_string())
    })
    .await?;
    session.revision = Some(revision);
    session.pending = None;
    Ok(count)
}
#[tauri::command]
pub async fn account_logout(
    purge_downloads: Option<bool>,
    downloads: State<'_, crate::downloads::Downloads>,
    store: State<'_, CatalogStore>,
    files: State<'_, PrivateFiles>,
    profile_id: uuid::Uuid,
    remote: State<'_, RemoteState>,
) -> CommandResult<()> {
    let mut guard = remote.0.lock().await;
    if let Some(session) = guard.get(&profile_id) {
        let response = session
            .client
            .delete(session.base.join("api/v1/accounts/session").unwrap())
            .bearer_auth(&session.token)
            .send()
            .await
            .map_err(|e| e.to_string())?;
        if !response.status().is_success() && response.status() != reqwest::StatusCode::UNAUTHORIZED
        {
            return Err("No se pudo cerrar la sesión en el servidor. Reintentá.".into());
        }
    }
    if purge_downloads == Some(true) {
        crate::downloads::clear_profile(&downloads, &store, profile_id).await?;
    }
    files.remove(profile_id, "session")?;
    guard.remove(&profile_id);
    Ok(())
}

#[tauri::command]
pub async fn account_current(
    store: State<'_, CatalogStore>,
    files: State<'_, PrivateFiles>,
    profile_id: uuid::Uuid,
    remote: State<'_, RemoteState>,
) -> CommandResult<Option<Value>> {
    let mut guard = remote.0.lock().await;
    if let std::collections::hash_map::Entry::Vacant(slot) = guard.entry(profile_id) {
        let store = store.for_profile(profile_id).map_err(|e| e.to_string())?;
        let Some(saved) = files.read::<SavedSession>(profile_id, "session")? else {
            return Ok(None);
        };
        if !chrono::DateTime::parse_from_rfc3339(&saved.expires)
            .is_ok_and(|v| v > chrono::Utc::now())
        {
            files.remove(profile_id, "session")?;
            return Ok(None);
        }
        let base = private_url(&saved.base)?;
        let revision = store
            .backup_revision(base.as_str(), saved.account)
            .map_err(|e| e.to_string())?;
        let mut restored = Session {
            client: session_client()?,
            base,
            revision,
            user: saved.user,
            account: saved.account,
            token: saved.token,
            incremental: saved.incremental,
            gzip: saved.gzip,
            automatic: saved.automatic,
            delta_base: None,
            etag: None,
            party_socket: None,
            pending: None,
            last_sync: None,
        };
        if let Err(e) = fetch(&mut restored).await {
            if e.starts_with("HTTP 401") {
                files.remove(profile_id, "session")?;
                return Ok(None);
            }
            restored.user["backupError"] = json!(e);
        }
        slot.insert(restored);
    }
    Ok(guard.get(&profile_id).map(|session| {
        let mut user = session.user.clone();
        user["automatic"] = json!(session.automatic);
        user["backup"] = session.pending.as_ref().map(summary).unwrap_or(Value::Null);
        user
    }))
}

#[tauri::command]
pub async fn account_set_automatic(
    files: State<'_, PrivateFiles>,
    profile_id: uuid::Uuid,
    enabled: bool,
    remote: State<'_, RemoteState>,
) -> CommandResult<()> {
    let mut sessions = remote.0.lock().await;
    if let Some(mut saved) = files.read::<SavedSession>(profile_id, "session")? {
        saved.automatic = enabled;
        files.save(profile_id, "session", &saved)?;
    }
    sessions
        .get_mut(&profile_id)
        .ok_or("Iniciá sesión primero.")?
        .automatic = enabled;
    Ok(())
}

#[tauri::command]
pub async fn account_sync(
    profile_id: uuid::Uuid,
    background: bool,
    store: State<'_, CatalogStore>,
    remote: State<'_, RemoteState>,
) -> CommandResult<Value> {
    let mut sessions = remote.0.lock().await;
    let session = sessions
        .get_mut(&profile_id)
        .ok_or("Iniciá sesión primero.")?;
    if background && !session.automatic {
        return Ok(json!({"changed":false}));
    }
    let store = store.for_profile(profile_id).map_err(|e| e.to_string())?;
    synchronize(session, &store).await
}

async fn synchronize(session: &mut Session, store: &CatalogStore) -> CommandResult<Value> {
    let mut changed = false;
    let mut needs_full_upload = false;
    for _ in 0..3 {
        let info = incremental::fetch_latest(session).await?;
        let expected = info["revision"].as_i64().ok_or("Revisión inválida.")?;
        if info["hasBackup"] == false
            && (expected > 0
                || store
                    .has_synced_account(session.base.as_str(), session.account)
                    .map_err(|e| e.to_string())?)
        {
            session.automatic = false;
            return Err("Se eliminó el respaldo de la cuenta. Para volver a crearlo, elegí Guardar respaldo.".into());
        }
        let marker = store.sync_marker().map_err(|e| e.to_string())?;
        if (session.etag.is_some() || session.delta_base == Some(expected))
            && session.last_sync == Some((expected, marker))
            && info["hasBackup"] == true
        {
            return Ok(json!({"changed":changed,"backup":info}));
        }
        // Lend the remote snapshot to the worker instead of copying it: it can
        // be as large as the whole library. If the worker is lost, the next
        // round fetches the snapshot again.
        let snapshot = session.pending.take();
        let library = store.clone();
        let (snapshot, merged) = work(move || {
            let document = snapshot
                .as_ref()
                .and_then(|s| s.get("document"))
                .filter(|v| !v.is_null());
            let merged = library
                .reconcile_profile(document)
                .map_err(|e| e.to_string());
            Ok((snapshot, merged))
        })
        .await?;
        session.pending = snapshot;
        let (updated, upload) = merged?;
        changed |= updated;
        let Some(document) = upload else {
            store
                .remember_sync_origin(session.base.as_str(), session.account)
                .map_err(|e| e.to_string())?;
            session.last_sync = Some((expected, marker));
            return Ok(json!({"changed":changed,"backup":info}));
        };
        if !needs_full_upload && session.incremental && session.delta_base == Some(expected) {
            if let Some(patch) = session
                .pending
                .as_ref()
                .and_then(|base| umd_core::backup_patch(&base["document"], &document))
            {
                if umd_core::backup_patch_empty(&patch) {
                    session.last_sync = Some((expected, marker));
                    return Ok(json!({"changed":changed,"backup":info}));
                }
                match incremental::send_patch(session, expected, patch).await? {
                    incremental::Write::Saved(written) => {
                        // Read back the merged rows even when another device wrote
                        // between our download and this PATCH.
                        let info = incremental::fetch_latest(session).await?;
                        let revision = info["revision"].as_i64().ok_or("Revisión inválida.")?;
                        if revision < written {
                            session.delta_base = None;
                            session.last_sync = None;
                            continue;
                        }
                        let confirmed = session
                            .pending
                            .as_ref()
                            .map(|v| v["document"].clone())
                            .filter(|v| !v.is_null());
                        let Some(confirmed) = confirmed else {
                            continue;
                        };
                        let library = store.clone();
                        let (received, remaining) = work(move || {
                            library
                                .reconcile_profile(Some(&confirmed))
                                .map_err(|e| e.to_string())
                        })
                        .await?;
                        store
                            .remember_sync_origin(session.base.as_str(), session.account)
                            .map_err(|e| e.to_string())?;
                        // This marker predates our write. A concurrent local edit
                        // must still be considered on the next sync.
                        session.last_sync = remaining.is_none().then_some((revision, marker));
                        return Ok(json!({"changed":changed||received,"backup":info}));
                    }
                    incremental::Write::Full => {
                        needs_full_upload = true;
                        session.delta_base = None;
                        session.last_sync = None;
                        continue;
                    }
                }
            }
        }
        let request = session
            .client
            .put(session.base.join("api/v1/sync/snapshot").unwrap())
            .bearer_auth(&session.token);
        let response = sync_body(
            request,
            session.gzip,
            json!({"expectedRevision":expected,"document":document}),
        )
        .await?
        .send()
        .await
        .map_err(|e| e.to_string())?;
        if response.status() == reqwest::StatusCode::CONFLICT {
            continue;
        }
        let receipt = json_response(response).await?;
        let revision = receipt["revision"]
            .as_i64()
            .filter(|v| *v > expected)
            .ok_or("El servidor devolvió una revisión inválida.")?;
        // A .NET server can reconcile simultaneous uploads itself. Never use
        // the outbound document as a stand-in for the merged server result.
        if revision != expected.saturating_add(1) {
            continue;
        }
        let snapshot =
            json!({"document":document,"revision":revision,"updatedAt":receipt["updatedAt"]});
        let info = summary(&snapshot);
        session.pending = Some(snapshot);
        session.delta_base = None;
        store
            .remember_sync_origin(session.base.as_str(), session.account)
            .map_err(|e| e.to_string())?;
        session.last_sync = Some((revision, marker));
        return Ok(json!({"changed":changed,"backup":info}));
    }
    Err("Otro dispositivo sigue guardando cambios. Reintentá la sincronización.".into())
}

#[tauri::command]
pub async fn edit_profile(
    files: State<'_, PrivateFiles>,
    edit: umd_core::ProfileEdit,
    store: State<'_, CatalogStore>,
    remote: State<'_, RemoteState>,
) -> CommandResult<()> {
    let removed = match &edit {
        umd_core::ProfileEdit::Delete { id } => Some(*id),
        _ => None,
    };
    let mut guard = remote.0.lock().await;
    let library = store.inner().clone();
    work(move || library.edit_profile(edit).map_err(|e| e.to_string())).await?;
    if let Some(id) = removed {
        files.remove(id, "session")?;
        files.remove(id, "metadata")?;
    }
    let session = removed.and_then(|id| guard.remove(&id));
    drop(guard);
    if let Some(session) = session {
        let _ = session
            .client
            .delete(session.base.join("api/v1/accounts/session").unwrap())
            .bearer_auth(session.token)
            .send()
            .await;
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    #[tokio::test]
    async fn conflict_retries_merge_both_devices_and_a_deleted_backup_stays_deleted() {
        use axum::{extract::State, http::StatusCode, routing::get, Json, Router};
        use std::sync::{Arc, Mutex};
        use umd_core::ListEdit;
        let local = CatalogStore::in_memory().unwrap();
        let other = CatalogStore::in_memory().unwrap();
        local
            .edit_list(ListEdit::Create {
                name: "Teléfono".into(),
            })
            .unwrap();
        other
            .edit_list(ListEdit::Create {
                name: "Tele".into(),
            })
            .unwrap();
        let concurrent = other.backup().unwrap();
        let snapshot = Arc::new(Mutex::new(json!({"revision":0,"document":null})));
        type Snapshot = Arc<Mutex<Value>>;
        let service = Router::new()
            .route(
                "/api/v1/sync/snapshot",
                get(|State(s): State<Snapshot>| async move { Json(s.lock().unwrap().clone()) })
                    .put(
                        move |State(s): State<Snapshot>, Json(body): Json<Value>| async move {
                            let mut s = s.lock().unwrap();
                            if s["revision"] == 0 {
                                *s = json!({"revision":1,"document":concurrent});
                                return (StatusCode::CONFLICT, Json(json!({"currentRevision":1})));
                            }
                            assert_eq!(body["expectedRevision"], s["revision"]);
                            let revision = s["revision"].as_i64().unwrap() + 1;
                            *s = json!({"revision":revision,"document":body["document"]});
                            (
                                StatusCode::OK,
                                Json(json!({"revision":revision,"updatedAt":umd_core::now()})),
                            )
                        },
                    ),
            )
            .with_state(snapshot.clone());
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let base = private_url(&format!("http://{}", listener.local_addr().unwrap())).unwrap();
        let server = tokio::spawn(async move { axum::serve(listener, service).await.unwrap() });
        let mut s = Session {
            user: Value::Null,
            account: uuid::Uuid::new_v4(),
            base,
            client: reqwest::Client::new(),
            token: "test".into(),
            revision: None,
            pending: None,
            automatic: true,
            incremental: false,
            gzip: false,
            delta_base: None,
            party_socket: None,
            etag: None,
            last_sync: None,
        };
        let result = synchronize(&mut s, &local).await.unwrap();
        assert_eq!(result["backup"]["revision"], 2);
        assert_eq!(local.personal_lists().unwrap().len(), 2);
        assert_eq!(
            snapshot.lock().unwrap()["document"]["lists"]
                .as_array()
                .unwrap()
                .len(),
            2
        );
        assert_eq!(synchronize(&mut s, &local).await.unwrap()["changed"], false);
        assert_eq!(snapshot.lock().unwrap()["revision"], 2);
        *snapshot.lock().unwrap() = json!({"revision":0,"document":null});
        s.last_sync = None;
        s.pending = None;
        assert!(synchronize(&mut s, &local)
            .await
            .unwrap_err()
            .contains("eliminó"));
        assert!(!s.automatic);
        assert_eq!(snapshot.lock().unwrap()["revision"], 0);
        server.abort();
    }
    #[tokio::test]
    async fn empty_dotnet_backup_starts_at_revision_zero_but_unauthorized_is_an_error() {
        use axum::{http::StatusCode, routing::get, Router};
        for status in [StatusCode::NOT_FOUND, StatusCode::UNAUTHORIZED] {
            let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
            let base = private_url(&format!("http://{}", listener.local_addr().unwrap())).unwrap();
            let app =
                Router::new().route("/api/v1/sync/snapshot", get(move || async move { status }));
            let server = tokio::spawn(async move { axum::serve(listener, app).await.unwrap() });
            let mut session = Session {
                user: Value::Null,
                account: uuid::Uuid::new_v4(),
                base,
                client: reqwest::Client::new(),
                token: "test".into(),
                revision: None,
                pending: None,
                automatic: false,
                incremental: false,
                gzip: false,
                delta_base: None,
                party_socket: None,
                etag: None,
                last_sync: None,
            };
            let result = fetch(&mut session).await;
            if status == StatusCode::NOT_FOUND {
                assert_eq!(result.unwrap()["hasBackup"], false);
                assert_eq!(session.revision, Some(0));
            } else {
                assert!(result.unwrap_err().contains("401"));
                assert!(session.revision.is_none());
            }
            server.abort();
        }
    }
    #[test]
    fn account_urls_require_tls_except_loopback_and_disallow_embedded_credentials() {
        for url in [
            "http://example.org",
            "file:///etc/passwd",
            "https://user:secret@example.org",
            "javascript:alert(1)",
        ] {
            assert!(private_url(url).is_err(), "{url}");
        }
        for url in [
            "https://example.org/umd",
            "http://127.0.0.1:9005",
            "http://[::1]:9005",
        ] {
            assert!(private_url(url).is_ok(), "{url}");
        }
        assert_eq!(
            private_url("https://example.org/umd?q=x#fragment")
                .unwrap()
                .as_str(),
            "https://example.org/umd/"
        );
    }
}
