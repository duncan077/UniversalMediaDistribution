use super::{authenticated, bad, bearer};
use crate::{ApiError, ApiResult, ServerState};
use axum::{
    extract::{Path, State},
    http::{HeaderMap, StatusCode},
    response::IntoResponse,
    routing::get,
    Json, Router,
};
use rusqlite::{params, Connection, OptionalExtension, Row, TransactionBehavior};
use serde::Deserialize;
use serde_json::{json, Value};
use std::collections::{HashMap, HashSet};
use umd_core::{normalize, CatalogDocument};
use uuid::Uuid;

pub(super) fn initialize(db: &Connection) -> rusqlite::Result<()> {
    db.execute_batch(
        "CREATE TABLE IF NOT EXISTS shared_lists(
        share_id TEXT PRIMARY KEY,owner TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
        list_id TEXT NOT NULL,name TEXT NOT NULL,item_count INTEGER NOT NULL,
        created_at TEXT NOT NULL,updated_at TEXT NOT NULL,UNIQUE(owner,list_id)); CREATE TABLE IF NOT EXISTS migrated_list_catalogs(share_id TEXT PRIMARY KEY REFERENCES shared_lists(share_id) ON DELETE CASCADE,document TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS shared_list_catalogs(share_id TEXT PRIMARY KEY REFERENCES shared_lists(share_id) ON DELETE CASCADE,revision INTEGER NOT NULL,document TEXT NOT NULL);",
    )
}
fn missing() -> ApiError {
    ApiError(
        StatusCode::NOT_FOUND,
        "No se encontró la lista compartida.".into(),
    )
}
fn entries(list: &Value) -> Result<&Vec<Value>, ApiError> {
    list["items"]
        .as_array()
        .ok_or_else(|| bad("La lista tiene títulos inválidos."))
}
fn list_id(list: &Value) -> Option<Uuid> {
    list["id"].as_str()?.parse().ok()
}
fn live_lists(document: &Value) -> impl Iterator<Item = &Value> {
    document["lists"]
        .as_array()
        .into_iter()
        .flatten()
        .filter(|v| v["isDeleted"] != true)
}
// A snapshot and its published memberships change in the same transaction.
// Revoked links cannot come back when an older backup restores a deleted list.
pub(super) fn refresh(
    db: &Connection,
    owner: &str,
    doc: Option<&Value>,
    updated: &str,
) -> Result<(), ApiError> {
    db.execute("DELETE FROM migrated_list_catalogs WHERE share_id IN(SELECT share_id FROM shared_lists WHERE owner=?1)",[owner])?;
    let mut stmt = db.prepare("SELECT list_id FROM shared_lists WHERE owner=?1")?;
    let published = stmt
        .query_map([owner], |r| r.get::<_, String>(0))?
        .collect::<rusqlite::Result<Vec<_>>>()?;
    if published.is_empty() {
        return Ok(());
    }
    let active: HashMap<_, _> = doc
        .into_iter()
        .flat_map(live_lists)
        .filter_map(|list| Some((list_id(list)?.to_string(), list)))
        .collect();
    for id in published {
        if let Some(list) = active.get(&id) {
            db.execute("UPDATE shared_lists SET name=?3,item_count=?4,updated_at=?5 WHERE owner=?1 AND list_id=?2",
                params![owner,id,list["name"].as_str(),entries(list)?.len() as i64,updated])?;
        } else {
            db.execute(
                "DELETE FROM shared_lists WHERE owner=?1 AND list_id=?2",
                params![owner, id],
            )?;
        }
    }
    Ok(())
}

fn catalog_for(
    doc: &Value,
    list: &Value,
    owner: &str,
    updated: &str,
) -> Result<CatalogDocument, ApiError> {
    let mut catalog =
        CatalogDocument::deserialize(&doc["catalog"]).map_err(umd_core::Error::from)?;
    let members = entries(list)?;
    let mut wanted = HashSet::new();
    let mut legacy = HashMap::new();
    if members.iter().any(|m| m["itemId"].is_null()) {
        for item in catalog.items.iter().filter(|i| !i.deleted) {
            let id = item.id()?;
            let key = (item.media_type.to_lowercase(), normalize(&item.name));
            for subtype in [None, Some(item.subtype.to_lowercase())] {
                legacy
                    .entry((key.clone(), subtype))
                    .and_modify(|v| *v = None)
                    .or_insert(Some(id));
            }
        }
    }
    for member in members {
        let id = if member["itemId"].is_null() {
            let key = (
                member["mediaType"].as_str().unwrap_or("").to_lowercase(),
                normalize(member["name"].as_str().unwrap_or("")),
            );
            legacy
                .get(&(key, member["subtype"].as_str().map(str::to_lowercase)))
                .copied()
                .flatten()
        } else {
            member["itemId"].as_str().and_then(|s| s.parse().ok())
        };
        let id = id.ok_or_else(|| bad("La lista contiene un título sin identidad inequívoca."))?;
        if !wanted.insert(id) {
            return Err(bad("La lista contiene un título repetido."));
        }
    }
    catalog
        .items
        .retain(|item| !item.deleted && item.id().is_ok_and(|id| wanted.contains(&id)));
    if catalog.items.len() != wanted.len() {
        return Err(bad("Falta un título de la lista en el respaldo."));
    }
    let mut result = CatalogDocument::new(catalog.items);
    result.source_label = Some(format!(
        "{} shared by {owner}",
        list["name"].as_str().unwrap_or("")
    ));
    result.exported_at = updated.to_owned();
    Ok(result)
}

const METADATA: &str =
    "SELECT s.share_id,s.list_id,s.name,a.display_name,s.item_count,s.created_at,s.updated_at
    FROM shared_lists s JOIN accounts a ON s.owner=a.id";
fn metadata(row: &Row<'_>) -> rusqlite::Result<Value> {
    let id: String = row.get(0)?;
    Ok(
        json!({"shareId":id,"listId":row.get::<_,String>(1)?,"name":row.get::<_,String>(2)?,
        "ownerDisplayName":row.get::<_,String>(3)?,"itemCount":row.get::<_,i64>(4)?,"createdAt":row.get::<_,String>(5)?,
        "updatedAt":row.get::<_,String>(6)?,"catalogPath":format!("/api/v1/shared-lists/{id}/catalog")}),
    )
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Publish {
    list_id: Uuid,
}
async fn publish(
    State(state): State<ServerState>,
    headers: HeaderMap,
    Json(req): Json<Publish>,
) -> ApiResult {
    let token = bearer(&headers)?;
    if req.list_id.is_nil() {
        return Err(bad("La lista requiere un identificador."));
    }
    let value = state
        .accounts
        .work(move |db| {
            let mut db = db.lock()?;
            let tx = db.transaction_with_behavior(TransactionBehavior::Immediate)?;
            let owner = authenticated(&tx, &token)?;
            let raw: Option<String> = tx
                .query_row(
                    "SELECT document FROM snapshots WHERE account_id=?1",
                    [&owner],
                    |r| r.get(0),
                )
                .optional()?
                .flatten();
            let raw = raw.ok_or_else(|| {
                ApiError(
                    StatusCode::CONFLICT,
                    "Sincronizá la lista antes de publicarla.".into(),
                )
            })?;
            let doc: Value = serde_json::from_str(&raw).map_err(umd_core::Error::from)?;
            let list = live_lists(&doc)
                .find(|l| list_id(l) == Some(req.list_id))
                .ok_or_else(missing)?;
            let now = umd_core::now();
            let catalog = catalog_for(&doc, list, "", &now)?;
            let share = Uuid::new_v4().simple().to_string();
            tx.execute(
                "INSERT INTO shared_lists VALUES(?1,?2,?3,?4,?5,?6,?6)
            ON CONFLICT(owner,list_id) DO NOTHING",
                params![
                    &share[..24],
                    owner,
                    req.list_id.to_string(),
                    list["name"].as_str(),
                    catalog.items.len() as i64,
                    now
                ],
            )?;
            let value = tx.query_row(
                &format!("{METADATA} WHERE s.owner=?1 AND s.list_id=?2"),
                params![owner, req.list_id.to_string()],
                metadata,
            )?;
            tx.commit()?;
            Ok(value)
        })
        .await?;
    Ok(Json(value).into_response())
}
async fn owned(State(state): State<ServerState>, headers: HeaderMap) -> ApiResult {
    let token = bearer(&headers)?;
    let values = state
        .accounts
        .work(move |db| {
            let db = db.lock()?;
            let owner = authenticated(&db, &token)?;
            let mut stmt = db.prepare(&format!(
                "{METADATA} WHERE s.owner=?1 ORDER BY s.updated_at DESC,s.share_id"
            ))?;
            let values = stmt
                .query_map([owner], metadata)?
                .collect::<rusqlite::Result<Vec<_>>>()?;
            Ok(values)
        })
        .await?;
    Ok(Json(values).into_response())
}
async fn info(State(state): State<ServerState>, Path(id): Path<String>) -> ApiResult {
    let data = state
        .accounts
        .work(move |db| {
            db.lock()?
                .query_row(&format!("{METADATA} WHERE s.share_id=?1"), [id], metadata)
                .optional()?
                .ok_or_else(missing)
        })
        .await?;
    Ok(Json(data).into_response())
}
/// The public catalog of a list as JSON text. Anyone holding the link can ask
/// for it, so it is built from the owner's backup once per backup revision and
/// served from `shared_list_catalogs` afterwards, instead of parsing the whole
/// backup on every request.
fn catalog_text(db: &mut Connection, id: &str) -> Result<String, ApiError> {
    if let Some(raw) = db
        .query_row(
            "SELECT document FROM migrated_list_catalogs WHERE share_id=?1",
            [id],
            |r| r.get::<_, String>(0),
        )
        .optional()?
    {
        let migrated: CatalogDocument =
            serde_json::from_str(&raw).map_err(|_| bad("Catálogo de lista inválido."))?;
        return Ok(serde_json::to_string(&migrated).map_err(umd_core::Error::from)?);
    }
    let ready: Option<Option<String>> = db
        .query_row(
            "SELECT c.document FROM shared_lists s JOIN snapshots p ON p.account_id=s.owner
             LEFT JOIN shared_list_catalogs c ON c.share_id=s.share_id AND c.revision=p.revision
             WHERE s.share_id=?1",
            [id],
            |r| r.get(0),
        )
        .optional()?;
    if let Some(text) = ready.ok_or_else(missing)? {
        return Ok(text);
    }
    let tx = db.transaction_with_behavior(TransactionBehavior::Immediate)?;
    let (raw, list, owner, updated, revision): (String, String, String, String, i64) = tx
        .query_row(
            "SELECT p.document,s.list_id,a.display_name,s.updated_at,p.revision FROM shared_lists s
             JOIN accounts a ON a.id=s.owner JOIN snapshots p ON p.account_id=s.owner WHERE s.share_id=?1",
            [id],
            |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?, r.get(3)?, r.get(4)?)),
        )
        .optional()?
        .ok_or_else(missing)?;
    let doc: Value = serde_json::from_str(&raw).map_err(umd_core::Error::from)?;
    let wanted = list.parse().map_err(|_| missing())?;
    let list = live_lists(&doc)
        .find(|l| list_id(l) == Some(wanted))
        .ok_or_else(missing)?;
    let text = serde_json::to_string(&catalog_for(&doc, list, &owner, &updated)?)
        .map_err(umd_core::Error::from)?;
    tx.execute(
        "INSERT INTO shared_list_catalogs VALUES(?1,?2,?3) ON CONFLICT(share_id) DO UPDATE SET
         revision=excluded.revision,document=excluded.document",
        params![id, revision, text],
    )?;
    tx.commit()?;
    Ok(text)
}
async fn catalog(State(state): State<ServerState>, Path(id): Path<String>) -> ApiResult {
    let text = state
        .accounts
        .work(move |db| catalog_text(&mut *db.lock()?, &id))
        .await?;
    Ok((
        [(axum::http::header::CONTENT_TYPE, "application/json")],
        text,
    )
        .into_response())
}
async fn revoke(
    State(state): State<ServerState>,
    headers: HeaderMap,
    Path(id): Path<String>,
) -> ApiResult {
    let token = bearer(&headers)?;
    state
        .accounts
        .work(move |db| {
            let mut db = db.lock()?;
            let tx = db.transaction_with_behavior(TransactionBehavior::Immediate)?;
            let owner = authenticated(&tx, &token)?;
            if tx.execute(
                "DELETE FROM shared_lists WHERE share_id=?1 AND owner=?2",
                params![id, owner],
            )? == 0
            {
                let exists: bool = tx.query_row(
                    "SELECT EXISTS(SELECT 1 FROM shared_lists WHERE share_id=?1)",
                    [id],
                    |r| r.get(0),
                )?;
                return Err(if exists {
                    ApiError(
                        StatusCode::FORBIDDEN,
                        "Solo el propietario puede retirar el enlace.".into(),
                    )
                } else {
                    missing()
                });
            }
            tx.commit()?;
            Ok(())
        })
        .await?;
    Ok(StatusCode::NO_CONTENT.into_response())
}
pub(super) fn routes() -> Router<ServerState> {
    Router::new()
        .route("/api/v1/shared-lists", get(owned).post(publish))
        .route("/api/v1/shared-lists/{id}", get(info).delete(revoke))
        .route("/api/v1/shared-lists/{id}/catalog", get(catalog))
}
