use axum::{
    extract::{Extension, Path, State},
    http::{header, HeaderMap, StatusCode, Uri},
    response::{IntoResponse, Response},
};
use image::{codecs::jpeg::JpegEncoder, ImageReader, Limits};
use reqwest::dns::{Addrs, Name, Resolve, Resolving};
use rusqlite::{params, Connection, OptionalExtension};
use sha2::{Digest, Sha256};
use std::{
    collections::HashMap,
    io::{self, Cursor},
    path::Path as FsPath,
    sync::{Arc, Mutex},
    time::{Duration, Instant},
};
use tokio::sync::{Mutex as AsyncMutex, Semaphore};
use umd_core::{public_url::public_ip, CatalogThumbnailUrls, CATALOG_THUMBNAIL_PREFIX};
use url::Url;
use uuid::Uuid;

const MAX_SOURCE_BYTES: usize = 6 << 20;
pub(crate) const MAX_REQUESTS: usize = 96;
const FRESH_SECONDS: i64 = 7 * 24 * 3600;
const RETRY_DELAY: Duration = Duration::from_secs(300);
const STRIPES: usize = 64;
type CacheResult<T> = Result<T, Box<dyn std::error::Error + Send + Sync>>;

#[derive(Clone)]
pub struct ThumbnailCache(Arc<Inner>);

struct Inner {
    urls: CatalogThumbnailUrls,
    db: Mutex<Connection>,
    budget: u64,
    client: reqwest::Client,
    admitted: Semaphore,
    downloads: Arc<Semaphore>,
    stripes: [AsyncMutex<()>; STRIPES],
    failures: Mutex<HashMap<String, Instant>>,
}

struct Thumbnail {
    bytes: Vec<u8>,
    stored_at: i64,
}

impl Thumbnail {
    fn remaining(&self) -> i64 {
        FRESH_SECONDS - (chrono::Utc::now().timestamp() - self.stored_at).max(0)
    }

    fn fresh(&self) -> bool {
        self.remaining() > 0
    }

    fn response(self, headers: &HeaderMap) -> Response {
        let remaining = self.remaining();
        let policy = if remaining > 0 {
            format!(
                "public, max-age={}, s-maxage={remaining}",
                remaining.min(86400)
            )
        } else {
            "public, max-age=120, s-maxage=300".into()
        };
        let tag = format!("\"{:x}\"", Sha256::digest(&self.bytes));
        let unchanged = headers
            .get(header::IF_NONE_MATCH)
            .and_then(|v| v.to_str().ok())
            .is_some_and(|list| {
                list.split(',')
                    .map(str::trim)
                    .any(|value| value == "*" || value.strip_prefix("W/").unwrap_or(value) == tag)
            });
        let mut response = if unchanged {
            StatusCode::NOT_MODIFIED.into_response()
        } else {
            ([(header::CONTENT_TYPE, "image/jpeg")], self.bytes).into_response()
        };
        let response_headers = response.headers_mut();
        response_headers.insert(header::ETAG, tag.parse().unwrap());
        response_headers.insert(header::CACHE_CONTROL, policy.parse().unwrap());
        response_headers.insert(header::X_CONTENT_TYPE_OPTIONS, "nosniff".parse().unwrap());
        response
    }
}

// Resolve every connection through this policy, including redirect targets.
// The checked addresses are the ones handed to the HTTP connector.
struct PublicDns;
impl Resolve for PublicDns {
    fn resolve(&self, name: Name) -> Resolving {
        Box::pin(async move {
            let addresses: Vec<_> = tokio::net::lookup_host((name.as_str(), 0)).await?.collect();
            if addresses.is_empty() || addresses.iter().any(|address| !public_ip(address.ip())) {
                return Err(io::Error::new(
                    io::ErrorKind::PermissionDenied,
                    "Nonpublic artwork origin",
                )
                .into());
            }
            Ok(Box::new(addresses.into_iter()) as Addrs)
        })
    }
}

impl ThumbnailCache {
    pub fn from_env(directory: &FsPath) -> CacheResult<Option<Self>> {
        let Some(origin) = std::env::var("UMD_SERVER_PUBLIC_URL")
            .ok()
            .filter(|v| !v.trim().is_empty())
        else {
            return Ok(None);
        };
        let mib = match std::env::var("UMD_SERVER_THUMBNAIL_CACHE_MIB") {
            Ok(value) if !value.trim().is_empty() => value
                .trim()
                .parse()
                .ok()
                .filter(|n| (16..=32768).contains(n))
                .ok_or("UMD_SERVER_THUMBNAIL_CACHE_MIB debe estar entre 16 y 32768.")?,
            _ => 2048,
        };
        Ok(Some(Self::open(directory, &origin, mib << 20)?))
    }

    pub fn open(directory: &FsPath, origin: &str, budget: u64) -> CacheResult<Self> {
        let urls = CatalogThumbnailUrls::new(origin)?;
        let mut db = Connection::open(directory.join("catalog-thumbnails.db"))?;
        db.execute_batch(
            "PRAGMA auto_vacuum=INCREMENTAL;
            CREATE TABLE IF NOT EXISTS thumbnails (
                key TEXT PRIMARY KEY, bytes BLOB NOT NULL, stored_at INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS thumbnails_age ON thumbnails(stored_at, key);",
        )?;
        prune(&mut db, budget, "")?;
        let client = umd_providers::browser_http::client_builder()
            .no_proxy()
            .https_only(true)
            .referer(false)
            .dns_resolver(Arc::new(PublicDns))
            .redirect(reqwest::redirect::Policy::custom(|attempt| {
                if attempt.previous().len() > 4
                    || CatalogThumbnailUrls::source(attempt.url().as_str()).is_none()
                {
                    attempt.error("Invalid thumbnail redirect")
                } else {
                    attempt.follow()
                }
            }))
            .connect_timeout(Duration::from_secs(5))
            .timeout(Duration::from_secs(10))
            .build()?;
        Ok(Self(Arc::new(Inner {
            urls,
            db: Mutex::new(db),
            budget,
            client,
            admitted: Semaphore::new(MAX_REQUESTS),
            downloads: Arc::new(Semaphore::new(3)),
            stripes: std::array::from_fn(|_| AsyncMutex::new(())),
            failures: Mutex::new(HashMap::new()),
        })))
    }

    pub(crate) fn urls(&self) -> &CatalogThumbnailUrls {
        &self.0.urls
    }

    async fn read(&self, key: &str) -> CacheResult<Option<Thumbnail>> {
        let inner = self.0.clone();
        let key = key.to_owned();
        tokio::task::spawn_blocking(move || {
            let db = inner.db.lock().map_err(|_| "Thumbnail cache unavailable")?;
            let mut statement =
                db.prepare_cached("SELECT bytes, stored_at FROM thumbnails WHERE key=?1")?;
            Ok(statement
                .query_row([key], |row| {
                    Ok(Thumbnail {
                        bytes: row.get(0)?,
                        stored_at: row.get(1)?,
                    })
                })
                .optional()?)
        })
        .await?
    }

    async fn refresh(&self, key: &str, source: Url) -> CacheResult<Thumbnail> {
        let download = self.0.downloads.clone().acquire_owned().await?;
        let referer = format!("{}/", source.origin().ascii_serialization());
        let mut response = self
            .0
            .client
            .get(source)
            .header(header::ACCEPT, "image/jpeg,image/png,image/webp,image/gif")
            .header(header::REFERER, referer)
            .send()
            .await?
            .error_for_status()?;
        if response
            .content_length()
            .is_some_and(|n| n > MAX_SOURCE_BYTES as u64)
        {
            return Err("Thumbnail source too large".into());
        }
        let mut data = Vec::new();
        while let Some(chunk) = response.chunk().await? {
            if data.len() + chunk.len() > MAX_SOURCE_BYTES {
                return Err("Thumbnail source too large".into());
            }
            data.extend_from_slice(&chunk);
        }
        let inner = self.0.clone();
        let key = key.to_owned();
        tokio::task::spawn_blocking(move || {
            let _download = download;
            let bytes = encode_thumbnail(&data)?;
            let stored_at = chrono::Utc::now().timestamp();
            let mut db = inner.db.lock().map_err(|_| "Thumbnail cache unavailable")?;
            db.execute(
                "INSERT INTO thumbnails(key,bytes,stored_at) VALUES(?1,?2,?3)
                ON CONFLICT(key) DO UPDATE SET bytes=excluded.bytes,stored_at=excluded.stored_at",
                params![key, bytes, stored_at],
            )?;
            prune(&mut db, inner.budget, &key)?;
            Ok(Thumbnail { bytes, stored_at })
        })
        .await?
    }

    async fn get(&self, key: &str, source: Url) -> CacheResult<Option<Thumbnail>> {
        let hit = self.read(key).await?;
        if hit.as_ref().is_some_and(Thumbnail::fresh) {
            return Ok(hit);
        }
        let stripe = usize::from_str_radix(&key[..2], 16)? % STRIPES;
        let _lock = self.0.stripes[stripe].lock().await;
        let hit = self.read(key).await?;
        if hit.as_ref().is_some_and(Thumbnail::fresh) {
            return Ok(hit);
        }
        let now = Instant::now();
        if self
            .0
            .failures
            .lock()
            .map_err(|_| "Thumbnail cache unavailable")?
            .get(key)
            .is_some_and(|until| *until > now)
        {
            return Ok(hit);
        }
        match self.refresh(key, source).await {
            Ok(thumbnail) => Ok(Some(thumbnail)),
            Err(_) => {
                let mut failures = self
                    .0
                    .failures
                    .lock()
                    .map_err(|_| "Thumbnail cache unavailable")?;
                if failures.len() >= 2048 {
                    failures.retain(|_, until| *until > now);
                }
                if failures.len() < 2048 {
                    failures.insert(key.to_owned(), Instant::now() + RETRY_DELAY);
                }
                Ok(hit)
            }
        }
    }
}

fn encode_thumbnail(data: &[u8]) -> CacheResult<Vec<u8>> {
    let reader = || ImageReader::new(Cursor::new(data)).with_guessed_format();
    let (width, height) = reader()?.into_dimensions()?;
    if width == 0 || height == 0 || u64::from(width) * u64::from(height) > 8_000_000 {
        return Err("Thumbnail dimensions exceed limit".into());
    }
    let mut decoder = reader()?;
    let mut limits = Limits::default();
    limits.max_image_width = Some(width);
    limits.max_image_height = Some(height);
    limits.max_alloc = Some(32 << 20);
    decoder.limits(limits);
    let poster = decoder
        .decode()?
        .thumbnail(width.min(360), height.min(540))
        .to_rgb8();
    let mut bytes = Vec::new();
    JpegEncoder::new_with_quality(&mut bytes, 80).encode_image(&poster)?;
    Ok(bytes)
}

fn prune(db: &mut Connection, budget: u64, keep: &str) -> rusqlite::Result<()> {
    fn bytes(db: &Connection) -> rusqlite::Result<u64> {
        db.query_row("SELECT (page_count-freelist_count)*page_size FROM pragma_page_count(),pragma_freelist_count(),pragma_page_size()", [], |row| row.get::<_, i64>(0).map(|n| n as u64))
    }
    if bytes(db)? <= budget {
        return Ok(());
    }
    let tx = db.transaction()?;
    while bytes(&tx)? > budget * 9 / 10 {
        if tx.execute("DELETE FROM thumbnails WHERE key IN (SELECT key FROM thumbnails WHERE key<>?1 ORDER BY stored_at,key LIMIT 64)", [keep])? == 0 {
            break;
        }
    }
    tx.commit()?;
    // SQLite performs one incremental vacuum step for every result row read.
    let mut vacuum = db.prepare("PRAGMA incremental_vacuum")?;
    let mut rows = vacuum.query([])?;
    while rows.next()?.is_some() {}
    Ok(())
}

pub(crate) async fn serve(
    State(state): State<super::ServerState>,
    Extension(work): Extension<super::CatalogWork>,
    Path((id, file)): Path<(String, String)>,
    uri: Uri,
    headers: HeaderMap,
) -> Response {
    let result = async {
        let id = Uuid::parse_str(&id).map_err(|_| StatusCode::NOT_FOUND)?;
        let key = file
            .strip_suffix(".jpg")
            .filter(|key| {
                key.len() == 64
                    && key
                        .bytes()
                        .all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b))
            })
            .ok_or(StatusCode::NOT_FOUND)?;
        if uri.query().is_some_and(|query| !query.is_empty())
            || uri.path() != format!("{CATALOG_THUMBNAIL_PREFIX}{id}/{file}")
        {
            return Err(StatusCode::NOT_FOUND);
        }
        let cache = work.thumbnails.as_ref().ok_or(StatusCode::NOT_FOUND)?;
        let _admitted = cache
            .0
            .admitted
            .try_acquire()
            .map_err(|_| StatusCode::SERVICE_UNAVAILABLE)?;
        let catalog = work.reader(&state.catalog);
        let source = work
            .run(move || Ok(catalog.thumbnail_source(id)?))
            .await
            .map_err(|e| e.0)?;
        let source = source
            .as_deref()
            .and_then(CatalogThumbnailUrls::source)
            .filter(|url| CatalogThumbnailUrls::key(url) == key)
            .ok_or(StatusCode::NOT_FOUND)?;
        let thumbnail = cache
            .get(key, source)
            .await
            .map_err(|_| StatusCode::SERVICE_UNAVAILABLE)?
            .ok_or(StatusCode::SERVICE_UNAVAILABLE)?;
        Ok::<_, StatusCode>(thumbnail.response(&headers))
    };
    match tokio::time::timeout(Duration::from_secs(24), result).await {
        Ok(Ok(response)) => response,
        failure => {
            let status = failure
                .ok()
                .and_then(Result::err)
                .unwrap_or(StatusCode::SERVICE_UNAVAILABLE);
            (status, [(header::CACHE_CONTROL, "no-store")]).into_response()
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use axum::{
        body::{to_bytes, Body},
        http::Request,
    };
    use tower::ServiceExt;
    use umd_core::{CatalogDocument, CatalogStore};

    #[tokio::test]
    async fn listings_and_details_use_persistent_public_thumbnails() {
        let dir = tempfile::tempdir().unwrap();
        let origin = "https://umd.example.org";
        let source = "https://cdn.example.org/art/cover.png?v=1";
        let id = Uuid::new_v4();
        let catalog = CatalogStore::in_memory().unwrap();
        let title = serde_json::from_value(serde_json::json!({
            "syncId": id, "name": "Portada", "mediaType": "book", "subtype": "manga",
            "thumbnailRef": source, "units": [{"kind": "chapter", "displayNumber": "1",
                "thumbnailRef": "https://cdn.example.org/nested.jpg"}]
        }))
        .unwrap();
        assert!(catalog.merge_scraped("test", "poster", title).unwrap());
        assert_eq!(
            catalog.thumbnail_source(id).unwrap().as_deref(),
            Some(source)
        );
        let key = CatalogThumbnailUrls::key(&Url::parse(source).unwrap());
        let path = format!("{CATALOG_THUMBNAIL_PREFIX}{id}/{key}.jpg");
        let mut raster = Cursor::new(Vec::new());
        image::DynamicImage::new_rgb8(800, 1200)
            .write_to(&mut raster, image::ImageFormat::Png)
            .unwrap();
        let poster = encode_thumbnail(raster.get_ref()).unwrap();
        assert_eq!(image::load_from_memory(&poster).unwrap().width(), 360);
        assert_eq!(image::load_from_memory(&poster).unwrap().height(), 540);
        let cache = ThumbnailCache::open(dir.path(), origin, 16 << 20).unwrap();
        cache
            .0
            .db
            .lock()
            .unwrap()
            .execute(
                "INSERT INTO thumbnails VALUES(?1,?2,?3)",
                params![key, poster, chrono::Utc::now().timestamp()],
            )
            .unwrap();
        drop(cache);

        let cache = ThumbnailCache::open(dir.path(), origin, 16 << 20).unwrap();
        let app = crate::router_with_thumbnails(
            crate::ServerState {
                admin: crate::Admin::open(dir.path()).unwrap(),
                catalog: catalog.clone(),
                accounts: crate::AccountStore::in_memory().unwrap(),
                releases: crate::ReleaseStore::new(dir.path(), crate::DEFAULT_MAX_PACKAGE_BYTES),
                admin_token: None,
                allow_registration: false,
            },
            Some(cache),
        );
        let detail = format!("/api/v1/catalog/items/{id}");
        for (resource, field) in [
            (
                "/api/v1/catalog/search?summary=true",
                "/items/0/thumbnailRef",
            ),
            ("/api/v1/catalog/items", "/document/items/0/thumbnailRef"),
            ("/api/v1/catalog/recent", "/items/0/thumbnailRef"),
            (&detail, "/items/0/thumbnailRef"),
        ] {
            let response = app
                .clone()
                .oneshot(Request::get(resource).body(Body::empty()).unwrap())
                .await
                .unwrap();
            assert_eq!(response.status(), StatusCode::OK);
            let value: serde_json::Value =
                serde_json::from_slice(&to_bytes(response.into_body(), 32 << 10).await.unwrap())
                    .unwrap();
            assert_eq!(value.pointer(field).unwrap(), &format!("{origin}{path}"));
        }
        let response = app
            .clone()
            .oneshot(
                Request::get("/api/v1/catalog/current")
                    .body(Body::empty())
                    .unwrap(),
            )
            .await
            .unwrap();
        let export =
            CatalogDocument::parse(&to_bytes(response.into_body(), 32 << 10).await.unwrap())
                .unwrap();
        assert_eq!(export.items[0].metadata["thumbnailRef"], source);
        let response = app
            .clone()
            .oneshot(Request::get(&path).body(Body::empty()).unwrap())
            .await
            .unwrap();
        assert_eq!(response.status(), StatusCode::OK);
        assert_eq!(response.headers()[header::CONTENT_TYPE], "image/jpeg");
        assert!(response.headers()[header::CACHE_CONTROL]
            .to_str()
            .unwrap()
            .starts_with("public, max-age="));
        assert!(!response.headers().contains_key(header::SET_COOKIE));
        let etag = response.headers()[header::ETAG].clone();
        assert_eq!(
            &to_bytes(response.into_body(), 128 << 10).await.unwrap()[..],
            poster
        );

        let response = app
            .clone()
            .oneshot(Request::head(&path).body(Body::empty()).unwrap())
            .await
            .unwrap();
        assert_eq!(response.status(), StatusCode::OK);
        assert!(to_bytes(response.into_body(), 1).await.unwrap().is_empty());
        let response = app
            .clone()
            .oneshot(
                Request::get(&path)
                    .header(header::IF_NONE_MATCH, etag)
                    .body(Body::empty())
                    .unwrap(),
            )
            .await
            .unwrap();
        assert_eq!(response.status(), StatusCode::NOT_MODIFIED);
        let invalid = format!("{CATALOG_THUMBNAIL_PREFIX}{id}/{}.jpg", "0".repeat(64));
        for request in [
            &invalid,
            &format!("{path}?url=https://example.org/cover.jpg"),
        ] {
            let response = app
                .clone()
                .oneshot(Request::get(request).body(Body::empty()).unwrap())
                .await
                .unwrap();
            assert_eq!(response.status(), StatusCode::NOT_FOUND);
            assert_eq!(response.headers()[header::CACHE_CONTROL], "no-store");
        }
    }

    #[test]
    fn eviction_keeps_the_newest_thumbnail_and_releases_space() {
        let mut db = Connection::open_in_memory().unwrap();
        db.execute_batch(
            "PRAGMA auto_vacuum=INCREMENTAL;
            CREATE TABLE thumbnails (key TEXT PRIMARY KEY, bytes BLOB, stored_at INTEGER);
            INSERT INTO thumbnails VALUES ('old', zeroblob(40000), 1), ('new', zeroblob(8000), 2);",
        )
        .unwrap();
        prune(&mut db, 32 << 10, "new").unwrap();
        let key: String = db
            .query_row("SELECT key FROM thumbnails", [], |row| row.get(0))
            .unwrap();
        assert_eq!(key, "new");
        assert!(
            db.pragma_query_value(None, "page_count", |row| row.get::<_, i64>(0))
                .unwrap()
                <= 8
        );
    }
}
