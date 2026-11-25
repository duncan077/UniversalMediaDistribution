use super::{authenticated, bad, bearer, friends, shared_lists};
use crate::{ApiError, ApiResult, ServerState};
use axum::{
    extract::{Query, State},
    http::StatusCode,
    response::{IntoResponse, Response},
    routing::{get, patch},
    Extension, Json, Router,
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
    let (revision, doc) = current_raw(db, owner)?;
    Ok((
        revision,
        doc.map(|s| serde_json::from_str(&s))
            .transpose()
            .map_err(umd_core::Error::from)?,
    ))
}

pub(super) fn current_raw(db: &Connection, owner: &str) -> Result<(i64, Option<String>), ApiError> {
    let row: Option<(i64, Option<String>)> = db
        .query_row(
            "SELECT revision,document FROM snapshots WHERE account_id=?1",
            [owner],
            |r| Ok((r.get(0)?, r.get(1)?)),
        )
        .optional()?;
    Ok(row.unwrap_or_default())
}

pub(super) fn revision(db: &Connection, owner: &str) -> Result<i64, ApiError> {
    Ok(db
        .query_row(
            "SELECT revision FROM snapshots WHERE account_id=?1",
            [owner],
            |row| row.get(0),
        )
        .optional()?
        .unwrap_or(0))
}

pub(super) fn index(db: &Connection, owner: &str) -> Result<Option<BackupRevisionIndex>, ApiError> {
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

pub(super) fn next_revision(revision: i64) -> Result<i64, ApiError> {
    revision
        .checked_add(1)
        .ok_or_else(|| bad("Revision limit reached"))
}

pub(super) struct PreparedSnapshot {
    stamps: String,
    header: Option<String>,
    payload: String,
}

pub(super) fn prepare_snapshot(
    doc: &Value,
    stamps: &BackupRevisionIndex,
) -> Result<PreparedSnapshot, ApiError> {
    Ok(PreparedSnapshot {
        stamps: serde_json::to_string(stamps).map_err(umd_core::Error::from)?,
        header: stamps.empty_changes(doc).map(|v| v.to_string()),
        payload: serde_json::to_string(doc).map_err(umd_core::Error::from)?,
    })
}

pub(super) fn persist(
    db: &Connection,
    owner: &str,
    revision: i64,
    after: Option<&Value>,
    prepared: Option<PreparedSnapshot>,
) -> Result<Value, ApiError> {
    let updated = umd_core::now();
    let device = after.and_then(|doc| doc["deviceId"].as_str());
    let payload = if let Some(prepared) = prepared {
        db.execute(
            "INSERT INTO sync_indices(account_id,stamps,header) VALUES(?1,?2,?3)
            ON CONFLICT(account_id) DO UPDATE SET stamps=excluded.stamps,header=excluded.header",
            params![owner, prepared.stamps, prepared.header],
        )?;
        Some(prepared.payload)
    } else {
        db.execute("DELETE FROM sync_indices WHERE account_id=?1", [owner])?;
        None
    };
    shared_lists::refresh(db, owner, after, &updated)?;
    friends::refresh(db, owner, after)?;
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
    Extension(super::SyncAccount(owner)): Extension<super::SyncAccount>,
    Query(query): Query<Since>,
) -> ApiResult {
    state
        .accounts
        .read(move |store| {
            let db = store.lock()?;
            let tx = db.unchecked_transaction()?;
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
async fn apply(State(state): State<ServerState>, request: axum::extract::Request) -> ApiResult {
    let token = bearer(request.headers())?;
    let body = state.accounts.document_body(request).await?;
    let max_bytes = state.accounts.limits.json;
    state
        .accounts
        .work_document(move |store| {
            let input: Patch = body.parse()?;
            let super::DocumentBody { bytes, _permit } = body;
            drop(bytes);
            for _ in 0..5 {
                let (owner, revision, raw, mut stamps) = {
                    let db = store.lock()?;
                    let tx = db.unchecked_transaction()?;
                    let owner = authenticated(&tx, &token)?;
                    let (revision, raw) = current_raw(&tx, &owner)?;
                    let stamps = index(&tx, &owner)?.unwrap_or_default();
                    (owner, revision, raw, stamps)
                };
                if input
                    .expected_revision
                    .is_some_and(|expected| expected != revision)
                {
                    return Ok(conflict(revision));
                }
                let raw = raw.ok_or_else(|| {
                    ApiError(
                        StatusCode::CONFLICT,
                        "La cuenta todavía no tiene respaldo.".into(),
                    )
                })?;
                let base = serde_json::from_str(&raw).map_err(umd_core::Error::from)?;
                drop(raw);
                let next = next_revision(revision)?;
                let merged = stamps
                    .apply_patch(base, &input.patch, next, max_bytes)?
                    .ok_or_else(|| {
                        ApiError(
                            StatusCode::CONFLICT,
                            "Estos cambios requieren combinar el respaldo completo.".into(),
                        )
                    })?;
                let prepared = prepare_snapshot(&merged, &stamps)?;
                let mut db = store.lock()?;
                let tx = db.transaction_with_behavior(TransactionBehavior::Immediate)?;
                authenticated(&tx, &token)?;
                let current: Option<i64> = tx
                    .query_row(
                        "SELECT revision FROM snapshots WHERE account_id=?1",
                        [&owner],
                        |row| row.get(0),
                    )
                    .optional()?;
                if current != Some(revision) {
                    if input.expected_revision.is_some() {
                        return Ok(conflict(current.unwrap_or(0)));
                    }
                    continue;
                }
                let receipt = persist(&tx, &owner, next, Some(&merged), Some(prepared))?;
                tx.commit()?;
                return Ok(Json(receipt).into_response());
            }
            Err(ApiError(
                StatusCode::CONFLICT,
                "Cambió la revisión del respaldo.".into(),
            ))
        })
        .await
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
