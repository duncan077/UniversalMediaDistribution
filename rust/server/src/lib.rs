use axum::{
    extract::{DefaultBodyLimit, Path, Query, State},
    http::{HeaderMap, StatusCode},
    response::{IntoResponse, Response},
    routing::get,
    Json, Router,
};
use serde::Deserialize;
use serde_json::{json, Value};
use std::collections::BTreeMap;
use subtle::ConstantTimeEq;
use umd_core::{CatalogDocument, CatalogQuery, CatalogStore};
use uuid::Uuid;
mod admin;
pub use admin::Admin;
mod accounts;
mod limits;
use limits::Limits;
mod releases;
pub use accounts::AccountStore;
pub use releases::{ReleaseStore, DEFAULT_MAX_PACKAGE_BYTES};

#[derive(Clone)]
pub struct ServerState {
    pub admin: Admin,
    pub catalog: CatalogStore,
    pub admin_token: Option<String>,
    pub accounts: AccountStore,
    pub releases: ReleaseStore,
    pub allow_registration: bool,
}

pub fn router(state: ServerState) -> Router {
    let limits = state.accounts.limits;
    Router::new()
        .route("/api/v1/catalog/current", get(current).put(publish))
        .route("/api/v1/catalog/metadata", get(metadata))
        .route("/api/v1/catalog/items", get(items))
        .route("/api/v1/catalog/items/{id}", get(item))
        .route("/api/v1/catalog/search", get(search))
        .route("/api/v1/catalog/recent", get(recent))
        // Catalog pages are JSON that shrinks about tenfold. Release packages
        // are merged after this layer: they are already compressed and must
        // keep their byte ranges and Content-Length.
        .layer(tower_http::compression::CompressionLayer::new().gzip(true))
        .layer(axum::middleware::from_fn_with_state(
            limits::Rate::new(limits.requests),
            limits::catalog_rate,
        ))
        .merge(admin::routes(&state))
        .merge(
            accounts::routes(&state)
                .merge(releases::routes())
                .layer(axum::middleware::map_response(limits::no_store)),
        )
        .route("/health", get(health))
        .route("/api/v1/capabilities", get(capabilities))
        .layer(DefaultBodyLimit::max(limits.json))
        .layer(tower_http::trace::TraceLayer::new_for_http())
        .layer(axum::middleware::from_fn_with_state(
            state.admin.clone(),
            admin::count,
        ))
        .with_state(state)
}

async fn health() -> Json<Value> {
    Json(json!({ "status": "ok", "serverTime": umd_core::now() }))
}

async fn capabilities(State(state): State<ServerState>) -> Json<Value> {
    Json(json!({
        "apiVersion": 1, "onlineCatalog": true,
        "accounts": true, "accountRegistration": state.allow_registration, "backupSync": true,
        "sharedLists": true, "friends": true, "progressPatch": true,
        "deltaSync": true, "watchParties": true, "gzipSyncRequests": true,
        "releaseNotifications": true, "releaseUploads": state.admin_token.is_some(),
        "syncProtocolVersion": 4, "maximumSyncBytes": state.accounts.limits.json
    }))
}

async fn current(State(state): State<ServerState>) -> ApiResult {
    let document = blocking(move || state.catalog.export_with_limit(30 << 20))
        .await?
        .ok_or_else(|| {
            ApiError(
                StatusCode::PAYLOAD_TOO_LARGE,
                "El catálogo supera 30 MiB. Consultá sus páginas.".into(),
            )
        })?;
    limits::catalog_json(&document, 30 << 20)
}

async fn metadata(State(state): State<ServerState>) -> ApiResult {
    let metadata = blocking(move || state.catalog.metadata()).await?;
    Ok(Json(metadata).into_response())
}

// Extract and authorize headers before parsing an administrative catalog body.
async fn publish(
    State(state): State<ServerState>,
    headers: HeaderMap,
    body: axum::body::Bytes,
) -> ApiResult {
    authorize_publication(&state, &headers)?;
    let document = CatalogDocument::parse_with_limit(&body, state.accounts.limits.json)?;
    let metadata = blocking(move || state.catalog.publish(&document)).await?;
    Ok(Json(metadata).into_response())
}

fn authorize_publication(state: &ServerState, headers: &HeaderMap) -> Result<(), ApiError> {
    let expected = state.admin_token.as_deref().ok_or_else(|| {
        ApiError(
            StatusCode::SERVICE_UNAVAILABLE,
            "Administrative publishing is disabled".into(),
        )
    })?;
    let received = headers
        .get("authorization")
        .and_then(|v| v.to_str().ok())
        .and_then(|v| v.strip_prefix("Bearer "))
        .unwrap_or("");
    if !bool::from(expected.as_bytes().ct_eq(received.as_bytes())) {
        return Err(ApiError(
            StatusCode::UNAUTHORIZED,
            "Invalid admin token".into(),
        ));
    }
    Ok(())
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Search {
    #[serde(default)]
    q: String,
    media_type: Option<String>,
    cursor: Option<String>,
    limit: Option<u32>,
    #[serde(default)]
    browse: bool,
    #[serde(default)]
    summary: bool,
    year: Option<i32>,
    genre: Option<String>,
    category: Option<String>,
    provider: Option<String>,
    status: Option<String>,
    subtype_group: Option<String>,
    #[serde(flatten)]
    unsupported: BTreeMap<String, Value>,
}

impl Search {
    fn query(self) -> Result<CatalogQuery, ApiError> {
        if !self.unsupported.is_empty() {
            return Err(ApiError(
                StatusCode::BAD_REQUEST,
                "Filtro de búsqueda desconocido".into(),
            ));
        }
        let _ = self.browse;
        Ok(CatalogQuery {
            q: self.q,
            media_type: self.media_type,
            cursor: self.cursor,
            limit: self.limit,
            favorites_only: false,
            filters: umd_core::CatalogFilters {
                year: self.year,
                genre: self.genre,
                category: self.category,
                provider: self.provider,
                status: self.status,
                subtype_group: self.subtype_group,
                summary: self.summary,
            },
        })
    }
}

async fn items(State(state): State<ServerState>, Query(query): Query<Search>) -> ApiResult {
    let mut query = query.query()?;
    loop {
        let store = state.catalog.clone();
        let q = query.clone();
        let page = blocking(move || store.page(&q)).await?.page;
        match limits::catalog_json(&page, 8 << 20) {
            Err(ApiError(StatusCode::PAYLOAD_TOO_LARGE, _)) if page.document.items.len() > 1 => {
                query.limit = Some((page.document.items.len() / 2) as u32)
            }
            result => return result,
        }
    }
}

async fn recent(State(state): State<ServerState>) -> ApiResult {
    limits::catalog_json(
        &blocking(move || state.catalog.recently_added()).await?,
        8 << 20,
    )
}

async fn search(State(state): State<ServerState>, Query(query): Query<Search>) -> ApiResult {
    let mut query = query.query()?;
    let summary = query.filters.summary;
    let (page, mut response) = loop {
        let store = state.catalog.clone();
        let q = query.clone();
        let page = blocking(move || store.search(&q)).await?;
        match limits::catalog_json(&page.page.document, 8 << 20) {
            Err(ApiError(StatusCode::PAYLOAD_TOO_LARGE, _))
                if page.page.document.items.len() > 1 =>
            {
                query.limit = Some((page.page.document.items.len() / 2) as u32)
            }
            result => break (page, result?),
        }
    };
    if summary {
        response
            .headers_mut()
            .insert("x-umd-summary", "true".parse().unwrap());
    }
    if let Some(cursor) = page.page.continuation_token {
        response
            .headers_mut()
            .insert("x-umd-next-cursor", cursor.parse().unwrap());
    }
    Ok(response)
}

async fn item(State(state): State<ServerState>, Path(id): Path<Uuid>) -> ApiResult {
    let document = blocking(move || state.catalog.item(id)).await?;
    limits::catalog_json(
        &document.ok_or_else(|| ApiError(StatusCode::NOT_FOUND, "Title not found".into()))?,
        8 << 20,
    )
}

async fn blocking<T: Send + 'static>(
    work: impl FnOnce() -> umd_core::Result<T> + Send + 'static,
) -> Result<T, ApiError> {
    tokio::task::spawn_blocking(work)
        .await
        .map_err(|error| {
            tracing::error!(%error, "Catalog worker failed");
            ApiError(
                StatusCode::INTERNAL_SERVER_ERROR,
                "Catalog worker failed".into(),
            )
        })?
        .map_err(Into::into)
}

type ApiResult = Result<Response, ApiError>;

#[derive(Debug)]
struct ApiError(StatusCode, String);

impl From<umd_core::Error> for ApiError {
    fn from(error: umd_core::Error) -> Self {
        match error {
            umd_core::Error::DocumentTooLarge(_) => {
                Self(StatusCode::PAYLOAD_TOO_LARGE, error.to_string())
            }
            umd_core::Error::Invalid(_) | umd_core::Error::Json(_) => {
                Self(StatusCode::BAD_REQUEST, error.to_string())
            }
            _ => {
                tracing::error!(%error, "Catalog operation failed");
                Self(
                    StatusCode::INTERNAL_SERVER_ERROR,
                    "Catalog storage unavailable".into(),
                )
            }
        }
    }
}

impl IntoResponse for ApiError {
    fn into_response(self) -> Response {
        (
            self.0,
            Json(json!({ "code": self.0.as_u16().to_string(), "message": self.1 })),
        )
            .into_response()
    }
}
