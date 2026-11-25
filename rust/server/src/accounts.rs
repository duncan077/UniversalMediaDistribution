use crate::{ApiError, ApiResult, ServerState};
use axum::{
    extract::{Request, State},
    http::{HeaderMap, StatusCode},
    response::IntoResponse,
    routing::{get, post},
    Extension, Json, Router,
};
use chrono::{Duration, Utc};
use pbkdf2::pbkdf2_hmac;
use rusqlite::{params, Connection, OptionalExtension};
use serde::{Deserialize, Serialize};
use serde_json::value::RawValue;
use serde_json::{json, Value};
use sha2::{Digest, Sha256};
use std::{
    path::Path,
    sync::{Arc, Mutex},
};
use subtle::ConstantTimeEq;
use uuid::Uuid;
mod friends;
mod incremental;
mod parties;
mod shared_lists;

#[derive(Clone)]
pub struct AccountStore {
    db: Arc<Mutex<Connection>>,
    readers: Arc<Readers>,
    pub(crate) limits: crate::Limits,
    workers: Arc<tokio::sync::Semaphore>,
    admitted: Arc<tokio::sync::Semaphore>,
    hashing: Arc<tokio::sync::Semaphore>,
    document_bodies: Arc<tokio::sync::Semaphore>,
}

struct Readers {
    connections: Vec<Arc<Mutex<Connection>>>,
    next: std::sync::atomic::AtomicUsize,
}

struct DocumentBody {
    bytes: axum::body::Bytes,
    _permit: tokio::sync::OwnedSemaphorePermit,
}

impl DocumentBody {
    fn parse<T: serde::de::DeserializeOwned>(&self) -> Result<T, ApiError> {
        Json::<T>::from_bytes(&self.bytes)
            .map(|json| json.0)
            .map_err(|error| {
                let status = error.into_response().status();
                ApiError(status, "El cuerpo JSON del respaldo no es válido.".into())
            })
    }
}

impl AccountStore {
    pub fn open(path: impl AsRef<Path>) -> umd_core::Result<Self> {
        Self::initialize(Connection::open(path)?)
    }
    pub fn in_memory() -> umd_core::Result<Self> {
        Self::initialize(Connection::open_in_memory()?)
    }
    fn initialize(db: Connection) -> umd_core::Result<Self> {
        db.busy_timeout(std::time::Duration::from_secs(10))?;
        db.execute_batch("PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;
          CREATE TABLE IF NOT EXISTS accounts(id TEXT PRIMARY KEY,username TEXT UNIQUE NOT NULL,display_name TEXT NOT NULL,salt BLOB NOT NULL,hash BLOB NOT NULL,created_at TEXT NOT NULL);
          CREATE TABLE IF NOT EXISTS sessions(token_hash TEXT PRIMARY KEY,account_id TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,expires_at INTEGER NOT NULL);
          CREATE TABLE IF NOT EXISTS login_failures(key TEXT PRIMARY KEY,window INTEGER NOT NULL,count INTEGER NOT NULL);
          CREATE TABLE IF NOT EXISTS snapshots(account_id TEXT PRIMARY KEY REFERENCES accounts(id) ON DELETE CASCADE,revision INTEGER NOT NULL,updated_at TEXT NOT NULL,device_id TEXT,document TEXT);
          CREATE INDEX IF NOT EXISTS account_sessions_expires ON sessions(expires_at);
          CREATE INDEX IF NOT EXISTS account_sessions_owner ON sessions(account_id);
          CREATE INDEX IF NOT EXISTS account_login_failure_window ON login_failures(window);")?;
        if !db
            .prepare("SELECT 1 FROM pragma_table_info('accounts') WHERE name='iterations'")?
            .exists([])?
        {
            db.execute_batch(
                "ALTER TABLE accounts ADD COLUMN iterations INTEGER NOT NULL DEFAULT 600000",
            )?;
        }
        shared_lists::initialize(&db)?;
        friends::initialize(&db)?;
        friends::rebuild_legacy_books(&db).map_err(|e| umd_core::Error::Invalid(e.1))?;
        incremental::initialize(&db)?;
        let mut connections = Vec::new();
        if let Some(path) = db.path().filter(|path| !path.is_empty()) {
            for _ in 0..4 {
                let reader =
                    Connection::open_with_flags(path, rusqlite::OpenFlags::SQLITE_OPEN_READ_ONLY)?;
                reader.busy_timeout(std::time::Duration::from_secs(10))?;
                connections.push(Arc::new(Mutex::new(reader)));
            }
        }
        Ok(Self {
            db: Arc::new(Mutex::new(db)),
            readers: Arc::new(Readers {
                connections,
                next: 0.into(),
            }),
            limits: crate::Limits::from_env()?,
            workers: Arc::new(tokio::sync::Semaphore::new(4)),
            admitted: Arc::new(tokio::sync::Semaphore::new(64)),
            hashing: Arc::new(tokio::sync::Semaphore::new(4)),
            document_bodies: Arc::new(tokio::sync::Semaphore::new(2)),
        })
    }

    async fn document_body(&self, request: Request) -> Result<DocumentBody, ApiError> {
        let content_type = request
            .headers()
            .get(axum::http::header::CONTENT_TYPE)
            .and_then(|value| value.to_str().ok())
            .map(|value| {
                value
                    .split(';')
                    .next()
                    .unwrap_or("")
                    .trim()
                    .to_ascii_lowercase()
            });
        if !content_type.as_deref().is_some_and(|value| {
            value == "application/json"
                || (value.starts_with("application/") && value.ends_with("+json"))
        }) {
            return Err(ApiError(
                StatusCode::UNSUPPORTED_MEDIA_TYPE,
                "Se esperaba un cuerpo JSON.".into(),
            ));
        }
        let permit = self
            .document_bodies
            .clone()
            .try_acquire_owned()
            .map_err(|_| busy())?;
        let bytes = axum::body::to_bytes(request.into_body(), self.limits.json)
            .await
            .map_err(|error| {
                use std::error::Error as _;
                if error
                    .source()
                    .is_some_and(|source| source.is::<http_body_util::LengthLimitError>())
                {
                    ApiError(
                        StatusCode::PAYLOAD_TOO_LARGE,
                        "El respaldo supera el límite permitido.".into(),
                    )
                } else {
                    bad("No se pudo leer el cuerpo del respaldo.")
                }
            })?;
        Ok(DocumentBody {
            bytes,
            _permit: permit,
        })
    }
    async fn work<T: Send + 'static>(
        &self,
        f: impl FnOnce(ConnectionGuard) -> Result<T, ApiError> + Send + 'static,
    ) -> Result<T, ApiError> {
        self.admit(1, self.db.clone(), f).await
    }
    async fn read<T: Send + 'static>(
        &self,
        f: impl FnOnce(ConnectionGuard) -> Result<T, ApiError> + Send + 'static,
    ) -> Result<T, ApiError> {
        let readers = &self.readers.connections;
        let connection = if readers.is_empty() {
            self.db.clone()
        } else {
            let index = self
                .readers
                .next
                .fetch_add(1, std::sync::atomic::Ordering::Relaxed)
                % readers.len();
            readers[index].clone()
        };
        self.admit(1, connection, f).await
    }
    /// Work that arrives holding a whole backup document. Each one counts as
    /// sixteen ordinary requests, so at most four can be running or waiting
    /// and the memory held by the queue stays bounded.
    async fn work_document<T: Send + 'static>(
        &self,
        f: impl FnOnce(ConnectionGuard) -> Result<T, ApiError> + Send + 'static,
    ) -> Result<T, ApiError> {
        self.admit(16, self.db.clone(), f).await
    }
    async fn admit<T: Send + 'static>(
        &self,
        weight: u32,
        db: Arc<Mutex<Connection>>,
        f: impl FnOnce(ConnectionGuard) -> Result<T, ApiError> + Send + 'static,
    ) -> Result<T, ApiError> {
        // Ordinary account work waits its turn instead of failing: four run at
        // once and a bounded number queue behind them, so a burst of logins can
        // no longer turn every other request into a 429.
        let admitted = self
            .admitted
            .clone()
            .try_acquire_many_owned(weight)
            .map_err(|_| busy())?;
        let permit = self
            .workers
            .clone()
            .acquire_owned()
            .await
            .map_err(|_| busy())?;
        tokio::task::spawn_blocking(move || {
            let _permits = (admitted, permit);
            f(ConnectionGuard(db))
        })
        .await
        .map_err(|_| {
            ApiError(
                StatusCode::INTERNAL_SERVER_ERROR,
                "Account worker unavailable".into(),
            )
        })?
    }

    /// Password hashing costs a CPU core for a noticeable time, so it has its
    /// own gate and still sheds load when every slot is taken.
    async fn hash<T: Send + 'static>(
        &self,
        f: impl FnOnce(ConnectionGuard) -> Result<T, ApiError> + Send + 'static,
    ) -> Result<T, ApiError> {
        let permit = self
            .hashing
            .clone()
            .try_acquire_owned()
            .map_err(|_| busy())?;
        let db = self.db.clone();
        tokio::task::spawn_blocking(move || {
            let _permit = permit;
            f(ConnectionGuard(db))
        })
        .await
        .map_err(|_| {
            ApiError(
                StatusCode::INTERNAL_SERVER_ERROR,
                "Account worker unavailable".into(),
            )
        })?
    }
}
fn busy() -> ApiError {
    ApiError(
        StatusCode::TOO_MANY_REQUESTS,
        "Servidor ocupado. Reintentá en unos segundos.".into(),
    )
}

struct ConnectionGuard(Arc<Mutex<Connection>>);
impl ConnectionGuard {
    fn lock(&self) -> Result<std::sync::MutexGuard<'_, Connection>, ApiError> {
        self.0.lock().map_err(|_| {
            ApiError(
                StatusCode::INTERNAL_SERVER_ERROR,
                "Account storage unavailable".into(),
            )
        })
    }
}
impl From<rusqlite::Error> for ApiError {
    fn from(error: rusqlite::Error) -> Self {
        tracing::error!(%error,"Account database operation failed");
        Self(
            StatusCode::INTERNAL_SERVER_ERROR,
            "Account storage unavailable".into(),
        )
    }
}
fn bad(message: &str) -> ApiError {
    ApiError(StatusCode::BAD_REQUEST, message.into())
}
fn unauthorized() -> ApiError {
    ApiError(
        StatusCode::UNAUTHORIZED,
        "Usuario, contraseña o sesión inválidos.".into(),
    )
}
fn hash_token(token: &str) -> String {
    format!("{:x}", Sha256::digest(token.as_bytes()))
}
fn bearer(headers: &HeaderMap) -> Result<String, ApiError> {
    headers
        .get("authorization")
        .and_then(|v| v.to_str().ok())
        .and_then(|v| v.strip_prefix("Bearer "))
        .filter(|v| v.len() <= 256)
        .map(str::to_owned)
        .ok_or_else(unauthorized)
}
/// Rejects a sync request without a valid session before its body is read:
/// a backup can be 32 MiB, and reading, inflating and parsing it used to
/// happen before anyone looked at the token.
async fn require_session(
    State(state): State<ServerState>,
    mut request: axum::extract::Request,
    next: axum::middleware::Next,
) -> axum::response::Response {
    let checked = match bearer(request.headers()) {
        Ok(token) => {
            state
                .accounts
                .read(move |db| {
                    let db = db.lock()?;
                    authenticated(&db, &token)
                })
                .await
        }
        Err(error) => Err(error),
    };
    match checked {
        Ok(owner) => {
            request.extensions_mut().insert(SyncAccount(owner));
            next.run(request).await
        }
        Err(error) => error.into_response(),
    }
}
#[derive(Clone)]
struct SyncAccount(String);

/// The address a request came from. A reverse proxy on this machine may speak
/// for its client through `X-Forwarded-For`; nobody else is believed, so a
/// remote caller cannot pick the address its attempts are counted under.
pub(crate) struct ClientAddress(pub(crate) String);
impl<S: Send + Sync> axum::extract::FromRequestParts<S> for ClientAddress {
    type Rejection = std::convert::Infallible;
    async fn from_request_parts(
        parts: &mut axum::http::request::Parts,
        _: &S,
    ) -> Result<Self, Self::Rejection> {
        let Some(peer) = parts
            .extensions
            .get::<axum::extract::ConnectInfo<std::net::SocketAddr>>()
            .map(|info| info.0.ip())
        else {
            return Ok(Self("unknown".into()));
        };
        let forwarded = parts
            .headers
            .get("x-forwarded-for")
            .and_then(|value| value.to_str().ok())
            .and_then(|value| value.rsplit(',').next())
            .and_then(|last| last.trim().parse::<std::net::IpAddr>().ok());
        let address = match forwarded {
            Some(client) if peer.is_loopback() => client,
            _ => peer,
        };
        Ok(Self(address.to_canonical().to_string()))
    }
}

const LOGIN_WINDOW: i64 = 900;
const ACCOUNT_FAILURES: i64 = 8;
const ADDRESS_FAILURES: i64 = 40;
/// Failures are counted per address, and per address and username together.
/// A stranger who guesses at someone's password exhausts only their own
/// address: the owner, coming from elsewhere, can still sign in.
fn login_keys(address: &str, name: &str) -> [(String, i64); 2] {
    [
        (format!("address\n{address}"), ADDRESS_FAILURES),
        (format!("account\n{address}\n{name}"), ACCOUNT_FAILURES),
    ]
}
/// Seconds until this address may try again, when it has failed too often.
fn login_blocked(
    db: &Connection,
    address: &str,
    name: &str,
    now: i64,
) -> Result<Option<i64>, ApiError> {
    db.execute(
        "DELETE FROM login_failures WHERE window<=?1",
        [now - LOGIN_WINDOW],
    )?;
    let mut wait = None;
    for (key, limit) in login_keys(address, name) {
        let window: Option<i64> = db
            .query_row(
                "SELECT window FROM login_failures WHERE key=?1 AND count>=?2",
                params![key, limit],
                |r| r.get(0),
            )
            .optional()?;
        wait = wait.max(window.map(|start| start + LOGIN_WINDOW - now));
    }
    Ok(wait.filter(|seconds| *seconds > 0))
}
fn login_failed(db: &Connection, address: &str, name: &str, now: i64) -> Result<(), ApiError> {
    for (key, _) in login_keys(address, name) {
        db.execute(
            "INSERT INTO login_failures VALUES(?1,?2,1)
             ON CONFLICT(key) DO UPDATE SET count=count+1",
            params![key, now],
        )?;
    }
    Ok(())
}

fn authenticated(db: &Connection, token: &str) -> Result<String, ApiError> {
    db.query_row(
        "SELECT account_id FROM sessions WHERE token_hash=?1 AND expires_at>?2",
        params![hash_token(token), Utc::now().timestamp()],
        |r| r.get(0),
    )
    .optional()?
    .ok_or_else(unauthorized)
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Credentials {
    username: String,
    password: String,
    display_name: Option<String>,
    #[serde(default)]
    device_name: String,
}
fn username(value: &str) -> Result<String, ApiError> {
    let v = value.trim().to_ascii_lowercase();
    if !(3..=64).contains(&v.len())
        || !v.as_bytes()[0].is_ascii_alphanumeric()
        || !v
            .bytes()
            .all(|c| c.is_ascii_alphanumeric() || b"._-".contains(&c))
    {
        return Err(bad(
            "El usuario debe tener entre 3 y 64 letras, números, puntos, guiones o guiones bajos.",
        ));
    }
    Ok(v)
}
fn password(value: &str) -> Result<(), ApiError> {
    if !(10..=256).contains(&value.chars().count()) || value.len() > 1024 {
        return Err(bad("La contraseña debe tener entre 10 y 256 caracteres."));
    }
    Ok(())
}
fn issue(db: &Connection, id: &str, days: i64) -> Result<Value, ApiError> {
    use base64::{engine::general_purpose::URL_SAFE_NO_PAD, Engine};
    let mut bytes = [0; 32];
    getrandom::fill(&mut bytes).map_err(|_| {
        ApiError(
            StatusCode::INTERNAL_SERVER_ERROR,
            "No se pudo crear la sesión.".into(),
        )
    })?;
    let token = URL_SAFE_NO_PAD.encode(bytes);
    let expires = Utc::now() + Duration::days(days);
    db.execute(
        "DELETE FROM sessions WHERE expires_at<=?1",
        [Utc::now().timestamp()],
    )?;
    db.execute(
        "INSERT INTO sessions VALUES(?1,?2,?3)",
        params![hash_token(&token), id, expires.timestamp()],
    )?;
    let (username, display_name): (String, String) = db.query_row(
        "SELECT username,display_name FROM accounts WHERE id=?1",
        [id],
        |r| Ok((r.get(0)?, r.get(1)?)),
    )?;
    Ok(
        json!({"accountId":id,"username":username,"displayName":display_name,"accessToken":token,"expiresAt":expires.to_rfc3339()}),
    )
}
fn admin(headers: &HeaderMap, state: &ServerState) -> Result<(), ApiError> {
    let provided = bearer(headers)?;
    if !state
        .admin_token
        .as_ref()
        .is_some_and(|t| bool::from(t.as_bytes().ct_eq(provided.as_bytes())))
    {
        return Err(unauthorized());
    }
    Ok(())
}
async fn register(
    State(state): State<ServerState>,
    headers: HeaderMap,
    Json(request): Json<Credentials>,
) -> ApiResult {
    if !state.allow_registration {
        return Err(ApiError(
            StatusCode::FORBIDDEN,
            "El registro público está deshabilitado. Pedí una cuenta al administrador.".into(),
        ));
    }
    create(state, headers, request, false).await
}
async fn admin_register(
    State(state): State<ServerState>,
    headers: HeaderMap,
    Json(request): Json<Credentials>,
) -> ApiResult {
    admin(&headers, &state)?;
    create(state, headers, request, true).await
}
async fn create(
    state: ServerState,
    _headers: HeaderMap,
    request: Credentials,
    admin: bool,
) -> ApiResult {
    let name = username(&request.username)?;
    password(&request.password)?;
    let display = request
        .display_name
        .unwrap_or_else(|| name.clone())
        .split_whitespace()
        .collect::<Vec<_>>()
        .join(" ");
    if display.is_empty() || display.chars().count() > 100 || display.chars().any(char::is_control)
    {
        return Err(bad("Nombre visible inválido."));
    }
    let limits = state.accounts.limits;
    let result=state.accounts.hash(move |db| {
        let mut salt=Vec::from(*Uuid::new_v4().as_bytes()); salt.extend_from_slice(Uuid::new_v4().as_bytes());
        let mut hash=[0u8;32]; pbkdf2_hmac::<Sha256>(request.password.as_bytes(),&salt,limits.iterations,&mut hash);
        let db=db.lock()?; let id=Uuid::new_v4().to_string(); let created=umd_core::now();
        if db.query_row("SELECT 1 FROM accounts WHERE username=?1",[&name],|r|r.get::<_,i32>(0)).optional()?.is_some() { return Err(ApiError(StatusCode::CONFLICT,"El usuario ya existe.".into())); }
        db.execute("INSERT INTO accounts(id,username,display_name,salt,hash,created_at,iterations) VALUES(?1,?2,?3,?4,?5,?6,?7)",params![id,name,display,salt,hash.as_slice(),created,limits.iterations])?;
        if admin { Ok(json!({"accountId":id,"username":name,"displayName":display,"createdAt":created})) } else { issue(&db,&id,limits.days) }
    }).await?;
    Ok((StatusCode::CREATED, Json(result)).into_response())
}
async fn login(
    State(state): State<ServerState>,
    ClientAddress(address): ClientAddress,
    Json(request): Json<Credentials>,
) -> ApiResult {
    let name = username(&request.username)?;
    // Historical .NET accounts retain their original password and PBKDF2 parameters.
    if request.password.chars().count() > 256 {
        return Err(unauthorized());
    }
    if request.device_name.chars().count() > 100 {
        return Err(bad("Nombre de dispositivo demasiado largo."));
    }
    let limits = state.accounts.limits;
    let outcome = state
        .accounts
        .hash(move |db| {
            let row: Option<(String, Vec<u8>, Vec<u8>, u32)> = {
                let db = db.lock()?;
                // A blocked address is turned away before any password is hashed.
                if let Some(seconds) = login_blocked(&db, &address, &name, Utc::now().timestamp())?
                {
                    return Ok(Err(seconds));
                }
                db.query_row(
                    "SELECT id,salt,hash,iterations FROM accounts WHERE username=?1",
                    [&name],
                    |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?, r.get(3)?)),
                )
                .optional()?
            };
            // Missing users follow the same expensive password verification path.
            let salt = row.as_ref().map(|r| r.1.as_slice()).unwrap_or(&[0u8; 32]);
            let rounds = row.as_ref().map_or(limits.iterations, |r| r.3);
            let mut actual = [0u8; 32];
            pbkdf2_hmac::<Sha256>(request.password.as_bytes(), salt, rounds, &mut actual);
            let account = row
                .filter(|(_, _, expected, _)| bool::from(actual.as_slice().ct_eq(expected)))
                .map(|(id, ..)| id);
            let db = db.lock()?;
            let Some(id) = account else {
                // Only failures count, and never against the account as a whole.
                login_failed(&db, &address, &name, Utc::now().timestamp())?;
                return Err(unauthorized());
            };
            let [_, (own, _)] = login_keys(&address, &name);
            db.execute("DELETE FROM login_failures WHERE key=?1", [own])?;
            issue(&db, &id, limits.days).map(Ok)
        })
        .await?;
    match outcome {
        Ok(session) => Ok(Json(session).into_response()),
        Err(seconds) => Ok((
            StatusCode::TOO_MANY_REQUESTS,
            [("retry-after", seconds.to_string())],
            Json(
                json!({"code":"429","message":"Demasiados intentos. Volvé a intentar más tarde."}),
            ),
        )
            .into_response()),
    }
}
async fn me(State(state): State<ServerState>, headers: HeaderMap) -> ApiResult {
    let token = bearer(&headers)?;
    let result=state.accounts.work(move |db| { let db=db.lock()?; let id=authenticated(&db,&token)?;
        let (name,display,created):(String,String,String)=db.query_row("SELECT username,display_name,created_at FROM accounts WHERE id=?1",[&id],|r|Ok((r.get(0)?,r.get(1)?,r.get(2)?)))?;
        let expires:i64=db.query_row("SELECT expires_at FROM sessions WHERE token_hash=?1",[hash_token(&token)],|r|r.get(0))?;
        Ok(json!({"accountId":id,"username":name,"displayName":display,"createdAt":created,"sessionExpiresAt":chrono::DateTime::from_timestamp(expires,0).unwrap().to_rfc3339()}))
    }).await?;
    Ok(Json(result).into_response())
}
async fn logout(State(state): State<ServerState>, headers: HeaderMap) -> ApiResult {
    let token = bearer(&headers)?;
    state
        .accounts
        .work(move |db| {
            let db = db.lock()?;
            authenticated(&db, &token)?;
            db.execute(
                "DELETE FROM sessions WHERE token_hash=?1",
                [hash_token(&token)],
            )?;
            Ok(())
        })
        .await?;
    Ok(StatusCode::NO_CONTENT.into_response())
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Upload {
    expected_revision: i64,
    document: Value,
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Delete {
    expected_revision: i64,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Snapshot {
    revision: i64,
    updated_at: String,
    updated_by_device_id: Option<String>,
    document: Option<Box<RawValue>>,
}

async fn snapshot(
    State(state): State<ServerState>,
    Extension(SyncAccount(id)): Extension<SyncAccount>,
    headers: HeaderMap,
) -> ApiResult {
    let requested = headers
        .get(axum::http::header::IF_NONE_MATCH)
        .and_then(|v| v.to_str().ok())
        .map(str::to_owned);
    let (tag, result) = state
        .accounts
        .read(move |db| {
            let db = db.lock()?;
            let tx = db.unchecked_transaction()?;
            let revision: i64 = tx
                .query_row(
                    "SELECT revision FROM snapshots WHERE account_id=?1",
                    [&id],
                    |row| row.get(0),
                )
                .optional()?
                .unwrap_or(0);
            let tag = format!("\"{id}-{revision}\"");
            if requested.as_deref() == Some(tag.as_str()) {
                return Ok((tag, None));
            }
            let row: Option<(String, Option<String>, Option<String>)> = tx
                .query_row(
                    "SELECT updated_at,device_id,document FROM snapshots WHERE account_id=?1",
                    [id],
                    |row| Ok((row.get(0)?, row.get(1)?, row.get(2)?)),
                )
                .optional()?;
            let (updated, device, document) =
                row.unwrap_or(("1970-01-01T00:00:00Z".into(), None, None));
            let document = document
                .map(RawValue::from_string)
                .transpose()
                .map_err(umd_core::Error::from)?;
            drop(tx);
            drop(db);
            Ok((
                tag,
                Some(
                    Json(Snapshot {
                        revision,
                        updated_at: updated,
                        updated_by_device_id: device,
                        document,
                    })
                    .into_response(),
                ),
            ))
        })
        .await?;
    let response = result.unwrap_or_else(|| StatusCode::NOT_MODIFIED.into_response());
    Ok((
        [
            ("etag", tag.as_str()),
            ("cache-control", "private, no-cache"),
        ],
        response,
    )
        .into_response())
}
async fn upload(State(state): State<ServerState>, request: Request) -> ApiResult {
    let token = bearer(request.headers())?;
    let body = state.accounts.document_body(request).await?;
    let max_bytes = state.accounts.limits.json;
    state
        .accounts
        .work_document(move |db| {
            let input: Upload = body.parse()?;
            let DocumentBody { bytes, _permit } = body;
            drop(bytes);
            write_snapshot(
                db,
                token,
                input.expected_revision,
                Some(input.document),
                max_bytes,
            )
        })
        .await
}
async fn delete_snapshot(
    State(state): State<ServerState>,
    headers: HeaderMap,
    Json(request): Json<Delete>,
) -> ApiResult {
    let token = bearer(&headers)?;
    let max_bytes = state.accounts.limits.json;
    state
        .accounts
        .work(move |db| write_snapshot(db, token, request.expected_revision, None, max_bytes))
        .await
}
fn write_snapshot(
    store: ConnectionGuard,
    token: String,
    expected: i64,
    document: Option<Value>,
    max_bytes: usize,
) -> ApiResult {
    if expected < 0 {
        return Err(bad("Invalid expectedRevision"));
    }
    let Some(document) = document else {
        let mut db = store.lock()?;
        let tx = db.transaction_with_behavior(rusqlite::TransactionBehavior::Immediate)?;
        let owner = authenticated(&tx, &token)?;
        let revision = incremental::revision(&tx, &owner)?;
        if revision != expected {
            return Ok(incremental::conflict(revision));
        }
        let receipt = incremental::persist(
            &tx,
            &owner,
            incremental::next_revision(revision)?,
            None,
            None,
        )?;
        tx.commit()?;
        return Ok(Json(receipt).into_response());
    };
    umd_core::validate_backup_with_limit(&document, max_bytes)?;
    for _ in 0..5 {
        let (owner, revision, raw, mut stamps) = {
            let db = store.lock()?;
            let tx = db.unchecked_transaction()?;
            let owner = authenticated(&tx, &token)?;
            let (revision, raw) = incremental::current_raw(&tx, &owner)?;
            let stamps = incremental::index(&tx, &owner)?.unwrap_or_default();
            (owner, revision, raw, stamps)
        };
        let previous: Option<Value> = raw
            .map(|payload| serde_json::from_str(&payload))
            .transpose()
            .map_err(umd_core::Error::from)?;
        let combined = if revision == expected {
            None
        } else {
            let Some(before) = &previous else {
                return Ok(incremental::conflict(revision));
            };
            if document["version"].as_u64() != Some(4) {
                return Ok(incremental::conflict(revision));
            }
            let merged = umd_core::CatalogStore::in_memory()?;
            merged.restore_backup_with_limit(before, max_bytes)?;
            let (_, output) = merged.reconcile_profile_with_limit(Some(&document), max_bytes)?;
            if let Some(mut output) = output {
                for key in ["deviceId", "deviceName"] {
                    output[key] = document[key].clone();
                }
                umd_core::validate_backup_with_limit(&output, max_bytes)?;
                Some(output)
            } else {
                None
            }
        };
        let after = combined.as_ref().unwrap_or(&document);
        let next = incremental::next_revision(revision)?;
        stamps.advance(previous.as_ref(), after, next);
        drop(previous);
        let prepared = incremental::prepare_snapshot(after, &stamps)?;
        let mut db = store.lock()?;
        let tx = db.transaction_with_behavior(rusqlite::TransactionBehavior::Immediate)?;
        authenticated(&tx, &token)?;
        if incremental::revision(&tx, &owner)? != revision {
            continue;
        }
        let receipt = incremental::persist(&tx, &owner, next, Some(after), Some(prepared))?;
        tx.commit()?;
        return Ok(Json(receipt).into_response());
    }
    let db = store.lock()?;
    let owner = authenticated(&db, &token)?;
    Ok(incremental::conflict(incremental::revision(&db, &owner)?))
}
pub(crate) fn routes(state: &ServerState) -> Router<ServerState> {
    // Everything under /sync requires a session, checked before the body.
    let sync = Router::new()
        .merge(incremental::routes(state.accounts.limits.json))
        .route(
            "/api/v1/sync/snapshot",
            get(snapshot)
                .put(upload)
                .delete(delete_snapshot)
                .layer::<_, std::convert::Infallible>(
                    tower_http::decompression::RequestDecompressionLayer::new().gzip(true),
                )
                .layer::<_, std::convert::Infallible>(
                    tower_http::limit::RequestBodyLimitLayer::new(state.accounts.limits.json),
                )
                // A snapshot download is the largest response the server sends.
                .layer::<_, std::convert::Infallible>(
                    tower_http::compression::CompressionLayer::new()
                        .gzip(true)
                        .quality(tower_http::CompressionLevel::Fastest),
                ),
        )
        .route_layer(axum::middleware::from_fn_with_state(
            state.clone(),
            require_session,
        ));
    Router::new()
        .merge(shared_lists::routes())
        .route("/api/v1/accounts/register", post(register))
        .route("/api/v1/admin/accounts", post(admin_register))
        .route("/api/v1/accounts/login", post(login))
        .route("/api/v1/accounts/me", get(me))
        .route("/api/v1/accounts/session", axum::routing::delete(logout))
        .layer(axum::extract::DefaultBodyLimit::max(8192))
        .merge(friends::routes().layer(axum::extract::DefaultBodyLimit::max(256 << 10)))
        .merge(parties::routes())
        .merge(sync)
}

#[cfg(test)]
mod tests {
    use super::*;
    #[tokio::test]
    async fn snapshot_admission_is_held_until_its_buffer_is_released() {
        let mut store = AccountStore::in_memory().unwrap();
        store.limits.json = 64;
        let request = |body: String| {
            axum::http::Request::builder()
                .header(axum::http::header::CONTENT_TYPE, "application/json")
                .body(axum::body::Body::from(body))
                .unwrap()
        };
        let first = store.document_body(request("{}".into())).await.unwrap();
        let second = store.document_body(request("{}".into())).await.unwrap();
        assert!(matches!(
            store.document_body(request("{}".into())).await,
            Err(ApiError(StatusCode::TOO_MANY_REQUESTS, _))
        ));
        drop(first);
        assert!(matches!(
            store.document_body(request("too large".repeat(16))).await,
            Err(ApiError(StatusCode::PAYLOAD_TOO_LARGE, _))
        ));
        drop(second);
        assert!(store.document_body(request("{}".into())).await.is_ok());
    }
    #[test]
    fn expired_sessions_are_rejected_and_tokens_are_hashed() {
        let store = AccountStore::in_memory().unwrap();
        let db = store.db.lock().unwrap();
        db.execute(
            "INSERT INTO accounts(id,username,display_name,salt,hash,created_at) VALUES('id','tester','Tester',?1,?2,'2026-01-01T00:00:00Z')",
            params![[0u8; 32].as_slice(), [0u8; 32].as_slice()],
        )
        .unwrap();
        db.execute(
            "INSERT INTO sessions VALUES(?1,'id',?2)",
            params![hash_token("private-token"), Utc::now().timestamp() - 1],
        )
        .unwrap();
        assert!(authenticated(&db, "private-token").is_err());
        db.execute(
            "UPDATE sessions SET expires_at=?1",
            [Utc::now().timestamp() + 60],
        )
        .unwrap();
        assert_eq!(authenticated(&db, "private-token").unwrap(), "id");
        let stored: String = db
            .query_row("SELECT token_hash FROM sessions", [], |r| r.get(0))
            .unwrap();
        assert_ne!(stored, "private-token");
        assert_eq!(stored.len(), 64);
    }
    #[tokio::test]
    async fn login_attempt_limit_runs_before_password_verification() {
        let accounts = AccountStore::in_memory().unwrap();
        accounts
            .db
            .lock()
            .unwrap()
            .execute(
                "INSERT INTO login_failures VALUES('account\ntest\ntester',?1,8)",
                [Utc::now().timestamp()],
            )
            .unwrap();
        let directory = tempfile::tempdir().unwrap();
        let state = ServerState {
            admin: crate::Admin::open(directory.path()).unwrap(),
            catalog: umd_core::CatalogStore::in_memory().unwrap(),
            accounts,
            releases: crate::ReleaseStore::new(
                std::env::temp_dir().join(uuid::Uuid::new_v4().to_string()),
                crate::DEFAULT_MAX_PACKAGE_BYTES,
            ),
            admin_token: None,
            allow_registration: false,
        };
        let result = login(
            State(state),
            ClientAddress("test".into()),
            Json(Credentials {
                username: "tester".into(),
                password: "a-valid-length-password".into(),
                display_name: None,
                device_name: "test".into(),
            }),
        )
        .await;
        let response = result.unwrap();
        assert_eq!(response.status(), StatusCode::TOO_MANY_REQUESTS);
        assert!(response.headers().contains_key("retry-after"));
    }

    fn state() -> (tempfile::TempDir, ServerState) {
        let directory = tempfile::tempdir().unwrap();
        let state = ServerState {
            admin: crate::Admin::open(directory.path()).unwrap(),
            catalog: umd_core::CatalogStore::in_memory().unwrap(),
            accounts: AccountStore::in_memory().unwrap(),
            releases: crate::ReleaseStore::new(
                std::env::temp_dir().join(uuid::Uuid::new_v4().to_string()),
                crate::DEFAULT_MAX_PACKAGE_BYTES,
            ),
            admin_token: None,
            allow_registration: true,
        };
        (directory, state)
    }
    fn credentials(password: &str) -> Json<Credentials> {
        Json(Credentials {
            username: "tester".into(),
            password: password.into(),
            display_name: None,
            device_name: "test".into(),
        })
    }

    #[tokio::test]
    async fn failed_logins_block_their_own_address_and_not_the_account() {
        let (_directory, state) = state();
        let password = "the-right-password";
        create(
            state.clone(),
            HeaderMap::new(),
            credentials(password).0,
            true,
        )
        .await
        .unwrap();
        let attempt = |address: &str, password: &str| {
            login(
                State(state.clone()),
                ClientAddress(address.into()),
                credentials(password),
            )
        };
        for _ in 0..ACCOUNT_FAILURES {
            assert!(matches!(
                attempt("203.0.113.9", "a-wrong-password").await,
                Err(ApiError(StatusCode::UNAUTHORIZED, _))
            ));
        }
        // The guessing address is now turned away, even with the right password.
        let blocked = attempt("203.0.113.9", password).await.unwrap();
        assert_eq!(blocked.status(), StatusCode::TOO_MANY_REQUESTS);
        // The owner, signing in from somewhere else, is not affected.
        let owner = attempt("198.51.100.7", password).await.unwrap();
        assert_eq!(owner.status(), StatusCode::OK);
        // A success clears that address's count for the account, not the stranger's.
        assert!(matches!(
            attempt("198.51.100.7", "a-wrong-password").await,
            Err(ApiError(StatusCode::UNAUTHORIZED, _))
        ));
        assert_eq!(
            attempt("198.51.100.7", password).await.unwrap().status(),
            StatusCode::OK
        );
        assert_eq!(
            attempt("203.0.113.9", password).await.unwrap().status(),
            StatusCode::TOO_MANY_REQUESTS
        );
    }

    #[tokio::test]
    async fn only_a_local_proxy_may_speak_for_another_address() {
        use axum::extract::{ConnectInfo, FromRequestParts};
        let address = |peer: &str, forwarded: Option<&str>| {
            let mut request = axum::http::Request::builder().uri("/");
            if let Some(value) = forwarded {
                request = request.header("x-forwarded-for", value);
            }
            let mut request = request.body(()).unwrap();
            let peer: std::net::SocketAddr = peer.parse().unwrap();
            request.extensions_mut().insert(ConnectInfo(peer));
            let (mut parts, ()) = request.into_parts();
            async move {
                ClientAddress::from_request_parts(&mut parts, &())
                    .await
                    .unwrap()
                    .0
            }
        };
        // Behind the local reverse proxy, the hop it appended is the client.
        assert_eq!(
            address("127.0.0.1:4000", Some("10.9.9.9, 203.0.113.9")).await,
            "203.0.113.9"
        );
        assert_eq!(address("127.0.0.1:4000", None).await, "127.0.0.1");
        // A remote caller cannot choose the address its attempts count under.
        assert_eq!(
            address("198.51.100.7:4000", Some("203.0.113.9")).await,
            "198.51.100.7"
        );
        assert_eq!(
            address("[::ffff:198.51.100.7]:4000", None).await,
            "198.51.100.7"
        );
    }
}
