use axum::{
    extract::{DefaultBodyLimit, Path, Query, Request, State},
    http::{HeaderMap, StatusCode},
    response::{IntoResponse, Response},
    routing::get,
    Extension, Json, Router,
};
use serde::Deserialize;
use serde_json::{json, Value};
use std::{collections::BTreeMap, sync::Arc};
use subtle::ConstantTimeEq;
use tokio::sync::Semaphore;
use umd_core::{CatalogDocument, CatalogQuery, CatalogStore};
use uuid::Uuid;
mod admin;
pub use admin::Admin;
mod accounts;
mod limits;
use limits::Limits;
mod releases;
mod thumbnails;
pub use accounts::AccountStore;
pub use releases::{ReleaseStore, DEFAULT_MAX_PACKAGE_BYTES};
pub use thumbnails::ThumbnailCache;

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
    router_with_thumbnails(state, None)
}

pub fn router_with_thumbnails(state: ServerState, thumbnails: Option<ThumbnailCache>) -> Router {
    let limits = state.accounts.limits;
    let work = CatalogWork {
        thumbnails,
        readers: state
            .catalog
            .readers(4)
            .map(Arc::new)
            .map_err(|error| tracing::warn!(%error, "Catalog readers unavailable"))
            .ok(),
        ..CatalogWork::default()
    };
    // A burst of missing posters must not exhaust the search admission queue.
    let thumbnail_work = CatalogWork {
        readers: work.readers.clone(),
        thumbnails: work.thumbnails.clone(),
        admitted: Arc::new(Semaphore::new(thumbnails::MAX_REQUESTS)),
        ..CatalogWork::default()
    };
    Router::new()
        .route("/api/v1/catalog/current", get(current).put(publish))
        .route("/api/v1/catalog/metadata", get(metadata))
        .route("/api/v1/catalog/items", get(items))
        .route("/api/v1/catalog/items/{id}", get(item))
        .route("/api/v1/catalog/search", get(search))
        .route("/api/v1/catalog/recent", get(recent))
        // Compress catalog JSON. Images and release packages are already
        // compressed and keep their own headers and admission limits.
        .layer(
            tower_http::compression::CompressionLayer::new()
                .gzip(true)
                .quality(tower_http::CompressionLevel::Fastest),
        )
        .layer(axum::middleware::from_fn_with_state(
            limits::Rate::new(limits.requests),
            limits::catalog_rate,
        ))
        .layer(Extension(work))
        .merge(
            Router::new()
                .route("/api/v1/catalog/thumbnails/{id}/{file}", get(thumbnails::serve))
                .layer(Extension(thumbnail_work)),
        )
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
        "deltaSync": true, "watchParties": true, "devicePlayback": true, "gzipSyncRequests": true,
        "releaseNotifications": true, "releaseUploads": state.admin_token.is_some(),
        "syncProtocolVersion": 4, "maximumSyncBytes": state.accounts.limits.json
    }))
}

async fn current(
    State(state): State<ServerState>,
    Extension(work): Extension<CatalogWork>,
    headers: HeaderMap,
) -> ApiResult {
    let catalog = work.reader(&state.catalog);
    let known = headers
        .get(axum::http::header::IF_NONE_MATCH)
        .and_then(|tag| tag.to_str().ok())
        .map(str::to_owned);
    work.run(move || {
        let (tag, document) = catalog.encode_current(known.as_deref(), 30 << 20)?;
        let response = document.map_or_else(
            || StatusCode::NOT_MODIFIED.into_response(),
            limits::json_bytes,
        );
        Ok((
            [("etag", tag), ("cache-control", "no-cache".into())],
            response,
        )
            .into_response())
    })
    .await
}

async fn metadata(
    State(state): State<ServerState>,
    Extension(work): Extension<CatalogWork>,
) -> ApiResult {
    let catalog = work.reader(&state.catalog);
    let metadata = work.run(move || Ok(catalog.metadata()?)).await?;
    Ok(Json(metadata).into_response())
}

// Admit only one authenticated upload before reading an administrative catalog body.
async fn publish(
    State(state): State<ServerState>,
    Extension(work): Extension<CatalogWork>,
    request: Request,
) -> ApiResult {
    authorize_publication(&state, request.headers())?;
    let upload = work
        .upload
        .clone()
        .try_acquire_owned()
        .map_err(|_| CatalogWork::busy())?;
    let limit = state.accounts.limits.json;
    let body = axum::body::to_bytes(request.into_body(), limit)
        .await
        .map_err(|_| {
            ApiError(
                StatusCode::PAYLOAD_TOO_LARGE,
                "El catálogo no se pudo leer dentro del límite permitido.".into(),
            )
        })?;
    let metadata = work
        .run(move || {
            let _upload = upload;
            let document = CatalogDocument::parse_with_limit(&body, limit)?;
            drop(body);
            Ok(state.catalog.publish(&document)?)
        })
        .await?;
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
    #[serde(default)]
    hide_adult: bool,
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
                list_id: None,
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

async fn items(
    State(state): State<ServerState>,
    Extension(work): Extension<CatalogWork>,
    Query(query): Query<Search>,
) -> ApiResult {
    let catalog = work.reader(&state.catalog);
    let thumbnails = work.thumbnails.clone();
    let hide_adult = query.hide_adult;
    let query = query.query()?;
    work.run(move || {
        let page = catalog.encode_public_page_with_thumbnails(
            &query, false, 8 << 20, hide_adult, thumbnails.as_ref().map(ThumbnailCache::urls),
        )?;
        Ok(limits::json_bytes(page.body))
    })
    .await
}

async fn recent(
    State(state): State<ServerState>,
    Extension(work): Extension<CatalogWork>,
    Query(query): Query<Search>,
) -> ApiResult {
    let catalog = work.reader(&state.catalog);
    let thumbnails = work.thumbnails.clone();
    work.run(move || {
        let body = catalog.encode_recent_with_thumbnails(
            8 << 20, query.hide_adult, thumbnails.as_ref().map(ThumbnailCache::urls),
        )?;
        Ok(limits::json_bytes(body))
    })
    .await
}

async fn search(
    State(state): State<ServerState>,
    Extension(work): Extension<CatalogWork>,
    Query(query): Query<Search>,
) -> ApiResult {
    let catalog = work.reader(&state.catalog);
    let thumbnails = work.thumbnails.clone();
    let hide_adult = query.hide_adult;
    let query = query.query()?;
    let summary = query.filters.summary;
    let (cursor, mut response) = work
        .run(move || {
            let page = catalog.encode_public_page_with_thumbnails(
                &query, true, 8 << 20, hide_adult, thumbnails.as_ref().map(ThumbnailCache::urls),
            )?;
            Ok((page.continuation_token, limits::json_bytes(page.body)))
        })
        .await?;
    if summary {
        response
            .headers_mut()
            .insert("x-umd-summary", "true".parse().unwrap());
    }
    if let Some(cursor) = cursor {
        response
            .headers_mut()
            .insert("x-umd-next-cursor", cursor.parse().unwrap());
    }
    Ok(response)
}

async fn item(
    State(state): State<ServerState>,
    Extension(work): Extension<CatalogWork>,
    Path(id): Path<Uuid>,
) -> ApiResult {
    let catalog = work.reader(&state.catalog);
    let thumbnails = work.thumbnails.clone();
    work.run(move || {
        let mut document = catalog
            .encode_public_item_with_thumbnails(id, 8 << 20, thumbnails.as_ref().map(ThumbnailCache::urls))?
            .ok_or_else(|| ApiError(StatusCode::NOT_FOUND, "Title not found".into()))?;
        let links: BTreeMap<_, _> = catalog.item_bindings(id)?.into_iter()
            .filter(|(site, external)| (site == "capibaratraductor" || umd_providers::Site::parse(site).is_some())
                && umd_core::provider_reference(external))
            .collect();
        if !links.is_empty() {
            let mut item = CatalogDocument::parse(&document)?;
            if let Some(title) = item.items.first_mut() {
                title.metadata.insert("onlineProviders".into(), json!(links));
            }
            document = serde_json::to_vec(&item).map_err(umd_core::Error::from)?;
            if document.len() > 8 << 20 {
                return Err(umd_core::Error::DocumentTooLarge(8 << 20).into());
            }
        }
        Ok(limits::json_bytes(document))
    })
    .await
}

#[derive(Clone)]
struct CatalogWork {
    thumbnails: Option<ThumbnailCache>,
    readers: Option<Arc<umd_core::CatalogReaders>>,
    admitted: Arc<Semaphore>,
    workers: Arc<Semaphore>,
    upload: Arc<Semaphore>,
}

impl Default for CatalogWork {
    fn default() -> Self {
        Self {
            thumbnails: None,
            readers: None,
            admitted: Arc::new(Semaphore::new(16)),
            workers: Arc::new(Semaphore::new(2)),
            upload: Arc::new(Semaphore::new(1)),
        }
    }
}

impl CatalogWork {
    fn reader(&self, writer: &CatalogStore) -> CatalogStore {
        self.readers
            .as_ref()
            .map_or_else(|| writer.clone(), |pool| pool.get())
    }

    fn busy() -> ApiError {
        ApiError(
            StatusCode::SERVICE_UNAVAILABLE,
            "El catálogo está ocupado. Reintentá en unos segundos.".into(),
        )
    }

    async fn run<T: Send + 'static>(
        &self,
        task: impl FnOnce() -> Result<T, ApiError> + Send + 'static,
    ) -> Result<T, ApiError> {
        let admitted = self
            .admitted
            .clone()
            .try_acquire_owned()
            .map_err(|_| Self::busy())?;
        let worker = self
            .workers
            .clone()
            .acquire_owned()
            .await
            .map_err(|_| Self::busy())?;
        tokio::task::spawn_blocking(move || {
            // The permits also cover jobs whose HTTP caller disconnected.
            let _permits = (admitted, worker);
            task()
        })
        .await
        .map_err(|error| {
            tracing::error!(%error, "Catalog worker failed");
            ApiError(
                StatusCode::INTERNAL_SERVER_ERROR,
                "Catalog worker failed".into(),
            )
        })?
    }
}

#[cfg(test)]
#[tokio::test]
async fn disconnected_catalog_request_keeps_its_blocking_slot_until_the_work_finishes() {
    let work = CatalogWork {
        admitted: Arc::new(Semaphore::new(1)),
        ..CatalogWork::default()
    };
    let (started, running) = tokio::sync::oneshot::channel();
    let (finish, waiting) = std::sync::mpsc::channel();
    let other = work.clone();
    let request = tokio::spawn(async move {
        other
            .run(move || {
                let _ = started.send(());
                let _ = waiting.recv();
                Ok(())
            })
            .await
    });
    running.await.unwrap();
    request.abort();
    let _ = request.await;
    assert!(matches!(
        work.run(|| Ok(())).await,
        Err(ApiError(StatusCode::SERVICE_UNAVAILABLE, _))
    ));
    finish.send(()).unwrap();
    let permit = tokio::time::timeout(std::time::Duration::from_secs(2), work.admitted.acquire())
        .await
        .unwrap()
        .unwrap();
    drop(permit);
    assert!(work.run(|| Ok(())).await.is_ok());
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
