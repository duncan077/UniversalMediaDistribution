mod crawl;
mod source;
use crate::{authorize_publication, ApiError, ApiResult, ServerState};
use axum::{
    extract::{Query, Request, State},
    http::{header, StatusCode},
    middleware::{self, Next},
    response::{IntoResponse, Response},
    routing::{get, post, put},
    Json, Router,
};
pub use crawl::Admin;
use crawl::{date, now, ErrBox};
use rusqlite::params;
use serde::Deserialize;
use serde_json::{json, Value};
use source::PROVIDERS;
use std::sync::atomic::Ordering;
use sysinfo::{ProcessRefreshKind, ProcessesToUpdate, System};
fn internal(error: impl std::fmt::Display) -> ApiError {
    tracing::error!(%error,"Crawler operation failed");
    ApiError(
        StatusCode::INTERNAL_SERVER_ERROR,
        "No se pudo leer o guardar el estado del rastreo.".into(),
    )
}
fn bad(message: &str) -> ApiError {
    ApiError(StatusCode::BAD_REQUEST, message.into())
}
async fn authorize(State(s): State<ServerState>, request: Request, next: Next) -> Response {
    if let Err(e) = authorize_publication(&s, request.headers()) {
        return e.into_response();
    }
    next.run(request).await
}
pub(crate) fn routes(state: &ServerState) -> Router<ServerState> {
    let api = Router::new()
        .route("/", get(diagnostics))
        .route("/logs", get(logs))
        .route("/crawl", post(start))
        .route("/provider-interval", put(interval))
        .route_layer(middleware::from_fn_with_state(state.clone(), authorize));
    Router::new()
        .nest("/api/v1/admin/diagnostics", api)
        .route("/admin", get(index))
        .route("/admin/", get(index))
        .route("/admin/app.js", get(script))
        .route("/admin/style.css", get(styles))
        .layer(middleware::from_fn(admin_headers))
}
async fn script() -> impl IntoResponse {
    (
        [(header::CONTENT_TYPE, "text/javascript; charset=utf-8")],
        include_str!("../../../src/UniversalMediaDistribution.Server/Admin/app.js"),
    )
}
async fn styles() -> impl IntoResponse {
    (
        [(header::CONTENT_TYPE, "text/css; charset=utf-8")],
        include_str!("../../../src/UniversalMediaDistribution.Server/Admin/style.css"),
    )
}
async fn admin_headers(request: Request, next: Next) -> Response {
    let mut response = next.run(request).await;
    for (key, value) in [
        ("cache-control", "no-store"), ("x-content-type-options", "nosniff"),
        ("referrer-policy", "no-referrer"),
        ("content-security-policy", "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'"),
    ] {
        response.headers_mut().insert(key, value.parse().unwrap());
    }
    response
}
async fn index() -> impl IntoResponse {
    axum::response::Html(include_str!(
        "../../../src/UniversalMediaDistribution.Server/Admin/index.html"
    ))
}
pub(crate) async fn count(State(admin): State<Admin>, request: Request, next: Next) -> Response {
    admin.0.requests.fetch_add(1, Ordering::Relaxed);
    let response = next.run(request).await;
    if response.status().is_server_error() {
        admin.0.failed.fetch_add(1, Ordering::Relaxed);
        let code = response.status().as_u16();
        let result = tokio::task::spawn_blocking(move || {
            admin.server_event(
                "request",
                "error",
                "http_error",
                "Falló una solicitud al servidor.",
                Some(code),
            )
        })
        .await;
        if !matches!(result, Ok(Ok(()))) {
            tracing::warn!("Could not store HTTP error event");
        }
    }
    response
}
async fn diagnostics(State(s): State<ServerState>) -> ApiResult {
    let val = tokio::task::spawn_blocking(move || snapshot(&s))
        .await
        .map_err(internal)?
        .map_err(internal)?;
    Ok(Json(val).into_response())
}
fn snapshot(s: &ServerState) -> Result<Value, ErrBox> {
    let a = &s.admin;
    let (titles, revision, updated) = s.catalog.crawl_summary()?;
    let (providers, runs, running) = {
        let lock = a.lock()?;
        if lock.is_some() {
            a.recover()?;
        }
        let running = lock.is_none();
        drop(lock);
        let db = a.db()?;
        let mut providers = Vec::new();
        let mut stmt=db.prepare("SELECT p.interval,p.complete,p.discovery,p.updates,p.attempt,p.success,p.failures,p.kind,p.retry,(SELECT COUNT(*) FROM titles WHERE provider=p.id),(SELECT COUNT(*) FROM titles WHERE provider=p.id AND success IS NOT NULL),(SELECT COUNT(*) FROM titles WHERE provider=p.id AND due<=?2),(SELECT COUNT(*) FROM titles WHERE provider=p.id AND failures>0) FROM providers p WHERE id=?1")?;
        for &(id, name) in PROVIDERS {
            let provider = stmt.query_row(params![id, now()], |row| {
                let complete = row.get::<_, bool>(1)?;
                let attempt = row.get::<_, Option<i64>>(4)?;
                let failures = row.get::<_, i64>(6)?;
                let retry_at = row.get::<_, Option<i64>>(8)?;
                let failed_titles = row.get::<_, i64>(12)?;
                let status = a.0.config.availability(id).unwrap_or_else(|| {
                    if retry_at.is_some_and(|t| t > now()) { "backoff" }
                    else if failures > 0 || failed_titles > 0 { "attention" }
                    else if attempt.is_none() { "not_run" }
                    else if complete { "ok" } else { "pending" }
                });
                Ok(json!({
                    "id":id, "name":name, "status":status, "intervalSeconds":row.get::<_,u32>(0)?,
                    "backfillComplete":complete, "lastDiscovery":date(row.get(2)?), "lastUpdates":date(row.get(3)?),
                    "lastAttempt":date(attempt), "lastSuccess":date(row.get(5)?), "consecutiveFailures":failures,
                    "failureKind":row.get::<_,Option<String>>(7)?, "retryAt":date(retry_at),
                    "discovered":row.get::<_,i64>(9)?, "loaded":row.get::<_,i64>(10)?, "due":row.get::<_,i64>(11)?,
                    "failedTitles":failed_titles
                }))
            })?;
            providers.push(provider);
        }
        let mut query=db.prepare("SELECT id,started,finished,status,updated,failures FROM runs ORDER BY started DESC,rowid DESC LIMIT 50")?;
        let runs=query.query_map([],|r|Ok(json!({"id":r.get::<_,String>(0)?,"startedAt":date(r.get(1)?),"finishedAt":date(r.get(2)?),"status":r.get::<_,String>(3)?,"titlesUpdated":r.get::<_,i64>(4)?,"failures":r.get::<_,i64>(5)?})))?.collect::<Result<Vec<_>,_>>()?;
        (providers, runs, running)
    };
    let pid = sysinfo::get_current_pid()?;
    let mut sys = System::new();
    sys.refresh_processes_specifics(
        ProcessesToUpdate::Some(&[pid]),
        true,
        ProcessRefreshKind::nothing().with_memory(),
    );
    let db_bytes = std::fs::read_dir(&a.0.directory)?
        .filter_map(Result::ok)
        .filter(|f| {
            let name = f.file_name();
            let name = name.to_string_lossy();
            name.ends_with(".db") || name.ends_with(".db-wal") || name.ends_with(".db-shm")
        })
        .filter_map(|f| f.metadata().ok())
        .map(|m| m.len())
        .sum::<u64>();
    Ok(
        json!({"generatedAt":date(Some(now())),"crawlerRunning":running,"catalog":{"titles":titles,"revision":revision,"updatedAt":updated},"providers":providers,"runs":runs,"runtime":{"startedAt":date(Some(a.0.started)),"requests":a.0.requests.load(Ordering::Relaxed),"failedRequests":a.0.failed.load(Ordering::Relaxed),"workingSetBytes":sys.process(pid).map(|p|p.memory()),"databaseBytes":db_bytes}}),
    )
}
#[derive(Deserialize)]
struct LogFilter {
    provider: Option<String>,
    level: Option<String>,
    before: Option<i64>,
    limit: Option<usize>,
}
async fn logs(State(s): State<ServerState>, Query(f): Query<LogFilter>) -> ApiResult {
    let limit = f.limit.unwrap_or(100).clamp(1, 200);
    let records = tokio::task::spawn_blocking(move || -> Result<Vec<Value>,ErrBox> {
        let db=s.admin.db()?;
        db.execute("DELETE FROM events WHERE time<?", [now()-86400])?;
        let mut query=db.prepare("SELECT id,time,provider,run,stage,level,status,message,http_status
            FROM events WHERE (?1 IS NULL OR id<?1) AND (?2 IS NULL OR provider=?2)
            AND (?3 IS NULL OR level=?3) ORDER BY id DESC LIMIT ?4")?;
        let rows=query.query_map(params![f.before,f.provider,f.level,(limit+1) as i64], |row| {
            let run=row.get::<_,Option<String>>(3)?;
            Ok(json!({
                "id":row.get::<_,i64>(0)?, "occurredAt":date(row.get(1)?), "providerId":row.get::<_,Option<String>>(2)?,
                "source":if run.is_some() {"crawler"} else {"server"}, "runId":run, "stage":row.get::<_,String>(4)?,
                "level":row.get::<_,String>(5)?, "status":row.get::<_,String>(6)?, "message":row.get::<_,String>(7)?,
                "httpStatus":row.get::<_,Option<u16>>(8)?
            }))
        })?.collect::<Result<Vec<_>,_>>()?;
        Ok(rows)
    }).await.map_err(internal)?.map_err(internal)?;
    let before = (records.len() > limit).then(|| records[limit - 1]["id"].clone());
    Ok(Json(json!({"items":&records[..limit.min(records.len())],"before":before})).into_response())
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Interval {
    provider_id: String,
    interval_seconds: u32,
}
async fn interval(State(s): State<ServerState>, Json(input): Json<Interval>) -> ApiResult {
    if !(1..=60).contains(&input.interval_seconds) {
        return Err(bad("El intervalo debe ser de 1 a 60 segundos."));
    }
    if s.admin
        .db()
        .map_err(internal)?
        .execute(
            "UPDATE providers SET interval=? WHERE id=?",
            params![input.interval_seconds, input.provider_id],
        )
        .map_err(internal)?
        == 0
    {
        return Err(bad("Proveedor desconocido."));
    }
    Ok(Json(json!({"providerId":input.provider_id,"intervalSeconds":input.interval_seconds,"message":"Guardado para el próximo rastreo."})).into_response())
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Crawl {
    provider_id: Option<String>,
    #[serde(default)]
    retry_failures: bool,
}
async fn start(State(s): State<ServerState>, Json(input): Json<Crawl>) -> ApiResult {
    if let Some(id) = input.provider_id.as_deref() {
        if !PROVIDERS.iter().any(|(p, _)| p == &id) || s.admin.0.config.availability(id).is_some() {
            return Err(bad("Proveedor desconocido o sin configurar."));
        }
    }
    let Some((lock, run)) = s.admin.begin().map_err(internal)? else {
        return Ok((
            StatusCode::CONFLICT,
            Json(json!({"status":"already_running","message":"Ya hay un rastreo en curso."})),
        )
            .into_response());
    };
    let reply = json!({"status":"queued","runId":run,"providerId":input.provider_id,"message":"Se inició el rastreo."});
    let admin = s.admin.clone();
    let job = tokio::spawn(async move {
        if let Err(err) = s
            .admin
            .execute(
                s.catalog,
                &run,
                lock,
                input.provider_id,
                input.retry_failures,
            )
            .await
        {
            tracing::error!(%err,"Crawler failed")
        }
    });
    *admin.0.manual.lock().map_err(internal)? = Some(job);
    Ok((StatusCode::ACCEPTED, Json(reply)).into_response())
}

#[cfg(test)]
mod tests;
