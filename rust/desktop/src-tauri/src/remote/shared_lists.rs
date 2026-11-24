use super::{json_response, synchronize, RemoteState, Session};
use crate::{work, CommandResult};
use reqwest::Url;
use serde_json::{json, Value};
use tauri::State;
use umd_core::{CatalogDocument, CatalogStore};
use uuid::Uuid;

fn valid_id(id: &str) -> bool {
    id.len() == 24
        && id
            .bytes()
            .all(|c| c.is_ascii_alphanumeric() || matches!(c, b'-' | b'_'))
}
fn public_url(session: &Session, row: &mut Value) -> CommandResult<()> {
    let id = row["shareId"]
        .as_str()
        .filter(|v| valid_id(v))
        .ok_or("Identificador de enlace inválido.")?;
    row["url"] = json!(session
        .base
        .join(&format!("api/v1/shared-lists/{id}/catalog"))
        .map_err(|e| e.to_string())?
        .as_str());
    Ok(())
}
#[tauri::command]
pub async fn published_lists(
    profile_id: Uuid,
    remote: State<'_, RemoteState>,
) -> CommandResult<Value> {
    let sessions = remote.0.lock().await;
    let session = sessions
        .get(&profile_id)
        .ok_or("Iniciá sesión en Cuenta y respaldo para ver tus enlaces.")?;
    let response = session
        .client
        .get(session.base.join("api/v1/shared-lists").unwrap())
        .bearer_auth(&session.token)
        .send()
        .await
        .map_err(|e| e.to_string())?;
    let mut value = json_response(response).await?;
    for row in value
        .as_array_mut()
        .ok_or("Respuesta de listas inválida.")?
    {
        public_url(session, row)?;
    }
    Ok(value)
}
#[tauri::command]
pub async fn publish_list(
    profile_id: Uuid,
    list_id: Uuid,
    store: State<'_, CatalogStore>,
    remote: State<'_, RemoteState>,
) -> CommandResult<Value> {
    let library = store.for_profile(profile_id).map_err(|e| e.to_string())?;
    let mut sessions = remote.0.lock().await;
    let session = sessions
        .get_mut(&profile_id)
        .ok_or("Iniciá sesión en Cuenta y respaldo para publicar una lista.")?;
    synchronize(session, &library, false).await?;
    let response = session
        .client
        .post(session.base.join("api/v1/shared-lists").unwrap())
        .bearer_auth(&session.token)
        .json(&json!({"listId":list_id}))
        .send()
        .await
        .map_err(|e| e.to_string())?;
    let mut row = json_response(response).await?;
    public_url(session, &mut row)?;
    Ok(row)
}
#[tauri::command]
pub async fn revoke_list(
    profile_id: Uuid,
    share_id: String,
    remote: State<'_, RemoteState>,
) -> CommandResult<()> {
    if !valid_id(&share_id) {
        return Err("Identificador de enlace inválido.".into());
    }
    let sessions = remote.0.lock().await;
    let session = sessions
        .get(&profile_id)
        .ok_or("Iniciá sesión para retirar el enlace.")?;
    let response = session
        .client
        .delete(
            session
                .base
                .join(&format!("api/v1/shared-lists/{share_id}"))
                .unwrap(),
        )
        .bearer_auth(&session.token)
        .send()
        .await
        .map_err(|e| e.to_string())?;
    if !response.status().is_success() && response.status() != reqwest::StatusCode::NOT_FOUND {
        json_response(response).await?;
    }
    Ok(())
}

fn shared_url(link: &str) -> CommandResult<Url> {
    let mut url =
        Url::parse(link.trim()).map_err(|_| "Ingresá un enlace a una lista compartida de UMD.")?;
    if !matches!(url.scheme(), "http" | "https")
        || url.host_str().is_none()
        || !url.username().is_empty()
        || url.password().is_some()
    {
        return Err("Dirección de lista inválida.".into());
    }
    let path = url.path().trim_end_matches('/');
    let path = path.strip_suffix("/catalog").unwrap_or(path);
    path.rsplit_once("/api/v1/shared-lists/")
        .filter(|(_, id)| valid_id(id))
        .ok_or("El enlace debe apuntar a una lista compartida de UMD.")?;
    let path = path.to_owned();
    url.set_path(&path);
    url.set_query(None);
    url.set_fragment(None);
    Ok(url)
}
#[tauri::command]
pub async fn open_shared_list(link: String) -> CommandResult<Value> {
    let url = shared_url(&link)?;
    let client = crate::direct_client()?;
    let timeout = std::time::Duration::from_secs(30);
    let info = json_response(
        client
            .get(url.clone())
            .timeout(timeout)
            .send()
            .await
            .map_err(|e| e.to_string())?,
    )
    .await?;
    let id = url
        .path_segments()
        .and_then(|mut parts| parts.next_back())
        .ok_or("Enlace inválido.")?;
    if info["shareId"] != id {
        return Err("La respuesta no corresponde a la lista solicitada.".into());
    }
    let name = info["name"]
        .as_str()
        .filter(|v| !v.trim().is_empty() && v.chars().count() <= 100)
        .ok_or("Nombre de lista inválido.")?;
    let catalog = json_response(
        client
            .get(format!("{url}/catalog"))
            .timeout(timeout)
            .send()
            .await
            .map_err(|e| e.to_string())?,
    )
    .await?;
    let document: CatalogDocument = serde_json::from_value(catalog).map_err(|e| e.to_string())?;
    document.validate().map_err(|e| e.to_string())?;
    Ok(
        json!({"link":url.as_str(),"name":name,"ownerDisplayName":info["ownerDisplayName"],"catalog":document}),
    )
}
#[tauri::command]
pub async fn save_shared_list(
    profile_id: Uuid,
    link: String,
    name: String,
    catalog: CatalogDocument,
    store: State<'_, CatalogStore>,
) -> CommandResult<bool> {
    let url = shared_url(&link)?;
    let store = store.for_profile(profile_id).map_err(|e| e.to_string())?;
    work(move || {
        store
            .save_shared_list(url.as_str(), &name, &catalog)
            .map_err(|e| e.to_string())
    })
    .await
}
