use super::{authenticated, bad, bearer};
use crate::{ApiError, ApiResult, ServerState};
use axum::{
    extract::{Path, State},
    http::{HeaderMap, StatusCode},
    response::{IntoResponse, Response},
    routing::{delete, get, post, put},
    Json, Router,
};
use rusqlite::{params, Connection, OptionalExtension, TransactionBehavior};
use serde::Deserialize;
use serde_json::json;
use std::collections::BTreeSet;
use uuid::Uuid;
mod activity;
pub(super) use activity::refresh;

pub(super) fn initialize(db: &Connection) -> rusqlite::Result<()> {
    db.execute_batch("CREATE TABLE IF NOT EXISTS friend_pairs(
        a TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
        b TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
        sender TEXT NOT NULL,accepted INTEGER NOT NULL DEFAULT 0,since TEXT NOT NULL,
        share_a INTEGER NOT NULL DEFAULT 1,share_b INTEGER NOT NULL DEFAULT 1,
        PRIMARY KEY(a,b),CHECK(a<b),CHECK(sender=a OR sender=b));
        CREATE INDEX IF NOT EXISTS friends_by_b ON friend_pairs(b);
        CREATE TABLE IF NOT EXISTS friend_settings(owner TEXT PRIMARY KEY REFERENCES accounts(id) ON DELETE CASCADE,
            sharing INTEGER NOT NULL,discoverable INTEGER NOT NULL,hidden TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS friend_activity(owner TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            item_key TEXT NOT NULL,entry TEXT NOT NULL,PRIMARY KEY(owner,item_key));
        CREATE TABLE IF NOT EXISTS friend_lookup_attempts(owner TEXT PRIMARY KEY REFERENCES accounts(id) ON DELETE CASCADE,
            window INTEGER NOT NULL,failures INTEGER NOT NULL);")
}
type TaskResult = Result<Response, ApiError>;
async fn account(
    state: ServerState,
    headers: HeaderMap,
    write: bool,
    task: impl FnOnce(&Connection, &str) -> TaskResult + Send + 'static,
) -> ApiResult {
    let token = bearer(&headers)?;
    state
        .accounts
        .work(move |guard| {
            let mut db = guard.lock()?;
            let mode = if write {
                TransactionBehavior::Immediate
            } else {
                TransactionBehavior::Deferred
            };
            let tx = db.transaction_with_behavior(mode)?;
            let me = authenticated(&tx, &token)?;
            let result = task(&tx, &me)?;
            tx.commit()?;
            Ok(result)
        })
        .await
}
fn missing() -> ApiError {
    ApiError(
        StatusCode::NOT_FOUND,
        "No se encontró la amistad o la solicitud.".into(),
    )
}
fn conflict(message: &str) -> ApiError {
    ApiError(StatusCode::CONFLICT, message.into())
}
fn pair<'a>(a: &'a str, b: &'a str) -> (&'a str, &'a str) {
    if a < b {
        (a, b)
    } else {
        (b, a)
    }
}
fn ensure_capacity(db: &Connection, a: &str, b: &str) -> Result<(), ApiError> {
    for id in [a, b] {
        let count: i64 = db.query_row(
            "SELECT count(*) FROM friend_pairs WHERE accepted=1 AND (a=?1 OR b=?1)",
            [id],
            |r| r.get(0),
        )?;
        if count > 500 {
            return Err(conflict("Se alcanzó el límite de 500 amigos."));
        }
    }
    Ok(())
}
async fn friends(State(s): State<ServerState>, h: HeaderMap) -> ApiResult {
    account(s, h, false, list_friends).await
}
fn list_friends(db: &Connection, me: &str) -> TaskResult {
    let mut stmt=db.prepare("SELECT u.id,u.username,u.display_name,p.since,
            CASE WHEN p.a=?1 THEN p.share_a ELSE p.share_b END,
            CASE WHEN v.sharing AND (CASE WHEN p.a=?1 THEN p.share_b ELSE p.share_a END) AND snap.document IS NOT NULL THEN snap.updated_at END
            FROM friend_pairs p JOIN accounts u ON u.id=CASE WHEN p.a=?1 THEN p.b ELSE p.a END
            LEFT JOIN friend_settings v ON v.owner=u.id LEFT JOIN snapshots snap ON snap.account_id=u.id
            WHERE (p.a=?1 OR p.b=?1) AND p.accepted=1 ORDER BY u.display_name,u.id")?;
    let rows=stmt.query_map([me],|r|Ok(json!({"accountId":r.get::<_,String>(0)?,"username":r.get::<_,String>(1)?,
            "displayName":r.get::<_,String>(2)?,"since":r.get::<_,String>(3)?,"shareProgress":r.get::<_,bool>(4)?,
            "progressUpdatedAt":r.get::<_,Option<String>>(5)?})))?.collect::<rusqlite::Result<Vec<_>>>()?;
    Ok(Json(rows).into_response())
}
async fn requests(State(s): State<ServerState>, h: HeaderMap) -> ApiResult {
    account(s, h, false, list_requests).await
}
fn list_requests(db: &Connection, me: &str) -> TaskResult {
    let mut stmt = db.prepare(
        "SELECT u.id,u.username,u.display_name,p.sender,p.since FROM friend_pairs p
            JOIN accounts u ON u.id=CASE WHEN p.a=?1 THEN p.b ELSE p.a END
            WHERE (p.a=?1 OR p.b=?1) AND accepted=0 ORDER BY p.since DESC,u.id",
    )?;
    let rows=stmt.query_map([me],|r|Ok(json!({"accountId":r.get::<_,String>(0)?,"username":r.get::<_,String>(1)?,
            "displayName":r.get::<_,String>(2)?,"direction":if r.get::<_,String>(3)?==me{"outgoing"}else{"incoming"},"createdAt":r.get::<_,String>(4)?})))?.collect::<rusqlite::Result<Vec<_>>>()?;
    Ok(Json(rows).into_response())
}
#[derive(Deserialize)]
struct SendRequest {
    username: String,
}
async fn send(
    State(s): State<ServerState>,
    h: HeaderMap,
    Json(input): Json<SendRequest>,
) -> ApiResult {
    account(s, h, true, move |db, me| send_request(db, me, input)).await
}
fn send_request(db: &Connection, me: &str, input: SendRequest) -> TaskResult {
    let now = chrono::Utc::now().timestamp();
    let failures: i64 = db
        .query_row(
            "SELECT failures FROM friend_lookup_attempts WHERE owner=?1 AND window>?2",
            params![me, now - 900],
            |r| r.get(0),
        )
        .optional()?
        .unwrap_or(0);
    if failures >= 8 {
        return Err(ApiError(
            StatusCode::TOO_MANY_REQUESTS,
            "Demasiados intentos. Volvé a intentar en 15 minutos.".into(),
        ));
    }
    let target:Option<(String,String,String)>=db.query_row("SELECT u.id,u.username,u.display_name FROM accounts u
            LEFT JOIN friend_settings v ON v.owner=u.id WHERE u.username=?1 AND coalesce(v.discoverable,1)",
            [input.username.trim().to_ascii_lowercase()],|r|Ok((r.get(0)?,r.get(1)?,r.get(2)?))).optional()?;
    let Some((id, name, display)) = target else {
        db.execute("INSERT INTO friend_lookup_attempts VALUES(?1,?2,1) ON CONFLICT(owner) DO UPDATE SET
                failures=CASE WHEN window<=?2-900 THEN 1 ELSE failures+1 END, window=CASE WHEN window<=?2-900 THEN ?2 ELSE window END",params![me,now])?;
        return Ok(ApiError(
            StatusCode::NOT_FOUND,
            "Ninguna cuenta con ese nombre acepta solicitudes.".into(),
        )
        .into_response());
    };
    if id == me {
        return Err(conflict("No podés agregarte a vos mismo."));
    }
    let (a, b) = pair(me, &id);
    let prior: Option<(bool, String, String)> = db
        .query_row(
            "SELECT accepted,sender,since FROM friend_pairs WHERE a=?1 AND b=?2",
            [a, b],
            |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?)),
        )
        .optional()?;
    let mut stamp = umd_core::now();
    let mut direction = "outgoing";
    match prior {
        Some((true, _, _)) => return Err(conflict("Esta cuenta ya está entre tus amigos.")),
        Some((false, sender, original)) if sender == me => stamp = original,
        Some(_) => {
            db.execute(
                "UPDATE friend_pairs SET accepted=1,since=?3 WHERE a=?1 AND b=?2",
                params![a, b, stamp],
            )?;
            ensure_capacity(db, a, b)?;
            direction = "accepted";
        }
        None => {
            db.execute(
                "INSERT INTO friend_pairs(a,b,sender,since) VALUES(?1,?2,?3,?4)",
                params![a, b, me, stamp],
            )?;
        }
    }
    Ok(Json(json!({"accountId":id,"username":name,"displayName":display,"direction":direction,"createdAt":stamp})).into_response())
}
async fn accept(State(s): State<ServerState>, h: HeaderMap, Path(id): Path<Uuid>) -> ApiResult {
    account(s, h, true, move |db, me| accept_request(db, me, id)).await
}
fn accept_request(db: &Connection, me: &str, id: Uuid) -> TaskResult {
    let other = id.to_string();
    let (a, b) = pair(me, &other);
    let changed=db.execute("UPDATE friend_pairs SET accepted=1,since=?4 WHERE a=?1 AND b=?2 AND sender=?3 AND accepted=0",params![a,b,other,umd_core::now()])?;
    if changed == 0 {
        return Err(missing());
    }
    ensure_capacity(db, a, b)?;
    Ok(StatusCode::NO_CONTENT.into_response())
}
async fn remove(s: ServerState, h: HeaderMap, id: Uuid, accepted: bool) -> ApiResult {
    account(s, h, true, move |db, me| {
        let other = id.to_string();
        let (a, b) = pair(me, &other);
        if db.execute(
            "DELETE FROM friend_pairs WHERE a=?1 AND b=?2 AND accepted=?3",
            params![a, b, accepted],
        )? == 0
        {
            return Err(missing());
        }
        Ok(StatusCode::NO_CONTENT.into_response())
    })
    .await
}
async fn dismiss(State(s): State<ServerState>, h: HeaderMap, Path(id): Path<Uuid>) -> ApiResult {
    remove(s, h, id, false).await
}
async fn unfriend(State(s): State<ServerState>, h: HeaderMap, Path(id): Path<Uuid>) -> ApiResult {
    remove(s, h, id, true).await
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Sharing {
    share_progress: bool,
}
async fn sharing(
    State(s): State<ServerState>,
    h: HeaderMap,
    Path(id): Path<Uuid>,
    Json(input): Json<Sharing>,
) -> ApiResult {
    account(s, h, true, move |db, me| {
        let other = id.to_string();
        let (a, b) = pair(me, &other);
        if db.execute(
            "UPDATE friend_pairs SET share_a=CASE WHEN a=?1 THEN ?4 ELSE share_a END,
            share_b=CASE WHEN b=?1 THEN ?4 ELSE share_b END WHERE a=?2 AND b=?3 AND accepted=1",
            params![me, a, b, input.share_progress],
        )? == 0
        {
            return Err(missing());
        }
        Ok(StatusCode::NO_CONTENT.into_response())
    })
    .await
}
#[derive(Deserialize, serde::Serialize)]
#[serde(rename_all = "camelCase")]
struct Visibility {
    share_progress: bool,
    discoverable_by_username: bool,
    hidden_item_keys: Vec<String>,
}
fn visibility_of(db: &Connection, me: &str) -> Result<Visibility, ApiError> {
    let row: Option<(bool, bool, String)> = db
        .query_row(
            "SELECT sharing,discoverable,hidden FROM friend_settings WHERE owner=?1",
            [me],
            |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?)),
        )
        .optional()?;
    let (share_progress, discoverable_by_username, raw) = row.unwrap_or((false, true, "[]".into()));
    Ok(Visibility {
        share_progress,
        discoverable_by_username,
        hidden_item_keys: serde_json::from_str(&raw).map_err(umd_core::Error::from)?,
    })
}
async fn visibility(State(s): State<ServerState>, h: HeaderMap) -> ApiResult {
    account(s, h, false, |db, me| {
        Ok(Json(visibility_of(db, me)?).into_response())
    })
    .await
}
async fn set_visibility(
    State(s): State<ServerState>,
    h: HeaderMap,
    Json(input): Json<Visibility>,
) -> ApiResult {
    account(s, h, true, move |db, me| update_visibility(db, me, input)).await
}
fn update_visibility(db: &Connection, me: &str, mut input: Visibility) -> TaskResult {
    if input.hidden_item_keys.len() > 5000 {
        return Err(bad("Podés ocultar hasta 5000 títulos."));
    }
    input.hidden_item_keys = input
        .hidden_item_keys
        .iter()
        .map(|s| s.trim().to_owned())
        .filter(|s| !s.is_empty())
        .collect::<BTreeSet<_>>()
        .into_iter()
        .collect();
    db.execute("INSERT INTO friend_settings VALUES(?1,?2,?3,?4) ON CONFLICT(owner) DO UPDATE SET sharing=excluded.sharing,discoverable=excluded.discoverable,hidden=excluded.hidden",
            params![me,input.share_progress,input.discoverable_by_username,serde_json::to_string(&input.hidden_item_keys).map_err(umd_core::Error::from)?])?;
    let document = if input.share_progress {
        activity::snapshot(db, me)?
    } else {
        None
    };
    refresh(db, me, document.as_ref())?;
    Ok(Json(input).into_response())
}
pub(super) fn routes() -> Router<ServerState> {
    Router::new()
        .route("/api/v1/friends", get(friends))
        .route("/api/v1/friends/requests", get(requests).post(send))
        .route("/api/v1/friends/requests/{id}/accept", post(accept))
        .route("/api/v1/friends/requests/{id}", delete(dismiss))
        .route("/api/v1/friends/{id}", delete(unfriend))
        .route("/api/v1/friends/{id}/sharing", put(sharing))
        .route(
            "/api/v1/friends/visibility",
            get(visibility).put(set_visibility),
        )
        .route("/api/v1/friends/{id}/shared", get(activity::shared))
        .route("/api/v1/friends/{id}/comparison", get(activity::comparison))
        .route("/api/v1/friends/activity/lookup", post(activity::lookup))
}
