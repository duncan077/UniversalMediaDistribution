use super::{authenticated, bad, bearer, friends, shared_lists};
use crate::{ApiError, ApiResult, ServerState};
use axum::{
    extract::{Query, State},
    http::{HeaderMap, StatusCode},
    response::{IntoResponse, Response},
    routing::{get, patch},
    Json, Router,
};
use rusqlite::{params, Connection, OptionalExtension, TransactionBehavior};
use serde::Deserialize;
use serde_json::{json, Value};
use umd_core::BackupRevisionIndex;

pub(super) fn initialize(db: &Connection) -> rusqlite::Result<()> {
    db.execute_batch("CREATE TABLE IF NOT EXISTS sync_indices(
        account_id TEXT PRIMARY KEY REFERENCES accounts(id) ON DELETE CASCADE, stamps TEXT NOT NULL, header TEXT)")
}

pub(super) fn current(db: &Connection, owner: &str) -> Result<(i64, Option<Value>), ApiError> {
    let row: Option<(i64, Option<String>)> = db
        .query_row(
            "SELECT revision,document FROM snapshots WHERE account_id=?1",
            [owner],
            |r| Ok((r.get(0)?, r.get(1)?)),
        )
        .optional()?;
    let (revision, doc) = row.unwrap_or_default();
    Ok((
        revision,
        doc.map(|s| serde_json::from_str(&s))
            .transpose()
            .map_err(umd_core::Error::from)?,
    ))
}

fn index(db: &Connection, owner: &str) -> Result<Option<BackupRevisionIndex>, ApiError> {
    let raw: Option<String> = db
        .query_row(
            "SELECT stamps FROM sync_indices WHERE account_id=?1",
            [owner],
            |r| r.get(0),
        )
        .optional()?;
    Ok(raw
        .map(|s| serde_json::from_str(&s))
        .transpose()
        .map_err(umd_core::Error::from)?)
}

pub(super) fn conflict(revision: i64) -> Response {
    (StatusCode::CONFLICT,Json(json!({"code":"revision_conflict","message":"El respaldo cambió en otro dispositivo.","currentRevision":revision}))).into_response()
}

pub(super) fn save(
    db: &Connection,
    owner: &str,
    previous_revision: i64,
    before: Option<&Value>,
    after: Option<&Value>,
) -> Result<Value, ApiError> {
    let revision = previous_revision
        .checked_add(1)
        .ok_or_else(|| bad("Revision limit reached"))?;
    let updated = umd_core::now();
    let device = after.and_then(|doc| doc["deviceId"].as_str());
    if let Some(doc) = after {
        let mut stamps = index(db, owner)?.unwrap_or_default();
        stamps.advance(before, doc, revision);
        let encoded = serde_json::to_string(&stamps).map_err(umd_core::Error::from)?;
        let header = stamps.empty_changes(doc).map(|v| v.to_string());
        db.execute(
            "INSERT INTO sync_indices(account_id,stamps,header) VALUES(?1,?2,?3)
            ON CONFLICT(account_id) DO UPDATE SET stamps=excluded.stamps,header=excluded.header",
            params![owner, encoded, header],
        )?;
    } else {
        db.execute("DELETE FROM sync_indices WHERE account_id=?1", [owner])?;
    }
    shared_lists::refresh(db, owner, after, &updated)?;
    friends::refresh(db, owner, after)?;
    let payload = after
        .map(serde_json::to_string)
        .transpose()
        .map_err(umd_core::Error::from)?;
    db.execute("INSERT INTO snapshots VALUES(?1,?2,?3,?4,?5) ON CONFLICT(account_id) DO UPDATE SET revision=excluded.revision,updated_at=excluded.updated_at,device_id=excluded.device_id,document=excluded.document",
        params![owner,revision,updated,device,payload])?;
    Ok(json!({"revision":revision,"updatedAt":updated,"updatedByDeviceId":device}))
}

#[derive(Deserialize, Default)]
struct Since {
    #[serde(default)]
    since: i64,
}
async fn changes(
    State(state): State<ServerState>,
    headers: HeaderMap,
    Query(query): Query<Since>,
) -> ApiResult {
    let token = bearer(&headers)?;
    state
        .accounts
        .work(move |store| {
            let db = store.lock()?;
            let tx = db.unchecked_transaction()?;
            let owner = authenticated(&tx, &token)?;
            changes_since(&tx, &owner, query.since.max(0))
        })
        .await
}

fn changes_since(db: &Connection, owner: &str, since: i64) -> ApiResult {
    let metadata: Option<(i64,String,Option<String>)>=db.query_row(
        "SELECT s.revision,s.updated_at,i.header FROM snapshots s LEFT JOIN sync_indices i ON i.account_id=s.account_id
         WHERE s.account_id=?1 AND s.document IS NOT NULL",[owner],|r|Ok((r.get(0)?,r.get(1)?,r.get(2)?))).optional()?;
    let (revision, updated, header) = metadata
        .ok_or_else(|| ApiError(StatusCode::NOT_FOUND, "La cuenta no tiene respaldo.".into()))?;
    let (full, document) = match header {
        Some(raw) if since == revision => (
            false,
            serde_json::from_str(&raw).map_err(umd_core::Error::from)?,
        ),
        _ => {
            let (_, doc) = current(db, owner)?;
            let doc = doc.ok_or_else(|| bad("Falta el documento de respaldo."))?;
            let delta = if since <= revision {
                index(db, owner)?.and_then(|index| index.changes(&doc, since))
            } else {
                None
            };
            (delta.is_none(), delta.unwrap_or(doc))
        }
    };
    Ok(Json(json!({"fromRevision":since,"toRevision":revision,"full":full,"updatedAt":updated,"document":document})).into_response())
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Patch {
    patch: Value,
    expected_revision: Option<i64>,
}
async fn apply(
    State(state): State<ServerState>,
    headers: HeaderMap,
    Json(input): Json<Patch>,
) -> ApiResult {
    let token = bearer(&headers)?;
    let max_bytes = state.accounts.limits.json;
    state
        .accounts
        .work_document(move |store| {
            let mut db = store.lock()?;
            let tx = db.transaction_with_behavior(TransactionBehavior::Immediate)?;
            let owner = authenticated(&tx, &token)?;
            let receipt = apply_patch(&tx, &owner, input, max_bytes)?;
            tx.commit()?;
            Ok(Json(receipt).into_response())
        })
        .await
}

fn apply_patch(
    db: &Connection,
    owner: &str,
    input: Patch,
    max_bytes: usize,
) -> Result<Value, ApiError> {
    let (revision, base) = current(db, owner)?;
    let Some(base) = base else {
        return Err(ApiError(
            StatusCode::CONFLICT,
            "La cuenta todavía no tiene respaldo.".into(),
        ));
    };
    if input
        .expected_revision
        .is_some_and(|expected| expected != revision)
    {
        return Err(ApiError(
            StatusCode::CONFLICT,
            "Cambió la revisión del respaldo.".into(),
        ));
    }
    let Some(merged) = umd_core::merge_backup_patch_with_limit(&base, &input.patch, max_bytes)?
    else {
        return Err(ApiError(
            StatusCode::CONFLICT,
            "Estos cambios requieren combinar el respaldo completo.".into(),
        ));
    };
    save(db, owner, revision, Some(&base), Some(&merged))
}

pub(super) fn routes(max_bytes: usize) -> Router<ServerState> {
    Router::new()
        .route(
            "/api/v1/sync/changes",
            get(changes).layer::<_, std::convert::Infallible>(
                tower_http::compression::CompressionLayer::new().gzip(true),
            ),
        )
        .route(
            "/api/v1/sync/snapshot/progress",
            patch(apply)
                .layer::<_, std::convert::Infallible>(
                    tower_http::decompression::RequestDecompressionLayer::new().gzip(true),
                )
                .layer(tower_http::limit::RequestBodyLimitLayer::new(max_bytes)),
        )
}
