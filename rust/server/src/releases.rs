use super::{authorize_publication, ApiError, ApiResult, ServerState};
use axum::{
    body::{to_bytes, Body},
    extract::{Path, Request, State},
    http::{header, HeaderMap, HeaderValue, StatusCode},
    response::IntoResponse,
    routing::get,
    Json, Router,
};
use base64::{engine::general_purpose::STANDARD, Engine};
use chrono::DateTime;
use futures_util::StreamExt;
use serde::{Deserialize, Serialize};
use serde_json::json;
use sha2::{Digest, Sha256};
use std::{
    io,
    path::{Path as FsPath, PathBuf},
    sync::Arc,
};
use tempfile::{Builder, TempPath};
use tokio::{fs, io::AsyncWriteExt, sync::Mutex};
use tower::ServiceExt;
use tower_http::services::ServeFile;

pub const DEFAULT_MAX_PACKAGE_BYTES: u64 = 2 * 1024 * 1024 * 1024;

#[derive(Clone)]
pub struct ReleaseStore {
    directory: PathBuf,
    maximum_bytes: u64,
    upload_gate: Arc<Mutex<()>>,
}
impl ReleaseStore {
    pub fn new(directory: impl Into<PathBuf>, maximum_bytes: u64) -> Self {
        Self {
            directory: directory.into(),
            maximum_bytes,
            upload_gate: Default::default(),
        }
    }
    fn manifest(&self, channel: &str, runtime: &str) -> PathBuf {
        self.directory
            .join("releases")
            .join(channel)
            .join(runtime)
            .join("latest.json")
    }
}

pub(super) fn routes() -> Router<ServerState> {
    Router::new()
        .route(
            "/api/v1/releases/{channel}/{runtime}/latest",
            get(latest).put(publish_manifest),
        )
        .route(
            "/api/v1/releases/{channel}/{runtime}/{version}/package/{name}",
            get(download).put(upload),
        )
}

fn invalid(message: &str) -> ApiError {
    ApiError(StatusCode::BAD_REQUEST, message.into())
}
fn storage(error: io::Error) -> ApiError {
    if error.kind() == io::ErrorKind::NotFound {
        return ApiError(StatusCode::NOT_FOUND, "Publicación no encontrada.".into());
    }
    tracing::error!(%error, "Release storage operation failed");
    ApiError(
        StatusCode::INTERNAL_SERVER_ERROR,
        "No se pudo acceder a la publicación.".into(),
    )
}

fn valid_name(value: &str, filename: bool) -> bool {
    let stem = value
        .split('.')
        .next()
        .unwrap_or_default()
        .to_ascii_uppercase();
    let reserved = matches!(stem.as_str(), "CON" | "PRN" | "AUX" | "NUL")
        || stem
            .strip_prefix("COM")
            .or_else(|| stem.strip_prefix("LPT"))
            .is_some_and(|s| s.len() == 1 && s.as_bytes()[0].is_ascii_digit());
    !value.is_empty()
        && !value.ends_with('.')
        && !reserved
        && value.len() <= if filename { 160 } else { 64 }
        && value
            .bytes()
            .all(|c| c.is_ascii_alphanumeric() || matches!(c, b'.' | b'-') || filename && c == b'_')
}
fn segment(raw: &str) -> Result<String, ApiError> {
    let value = raw.trim().to_ascii_lowercase();
    if !valid_name(&value, false) {
        return Err(invalid("Identificador de publicación inválido."));
    }
    Ok(value)
}
type PackageRoute = (String, String, String, String);
struct Package {
    path: PathBuf,
    relative: String,
    name: String,
}
fn package(store: &ReleaseStore, route: PackageRoute) -> Result<Package, ApiError> {
    let (channel, runtime, version, name) = route;
    let channel = segment(&channel)?;
    let runtime = segment(&runtime)?;
    let version = segment(version.trim_start_matches(['v', 'V']))?;
    let name = name.trim();
    if !valid_name(name, true) {
        return Err(invalid("Nombre de paquete inválido."));
    }
    Ok(Package {
        path: store
            .directory
            .join("packages")
            .join(&channel)
            .join(&runtime)
            .join(&version)
            .join(name),
        relative: format!("/api/v1/releases/{channel}/{runtime}/{version}/package/{name}"),
        name: name.to_owned(),
    })
}
fn valid_hash(value: &str) -> bool {
    value.len() == 64 && value.bytes().all(|c| c.is_ascii_hexdigit())
}

#[derive(Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct Manifest {
    version: String,
    channel: String,
    runtime_identifier: String,
    download_uri: String,
    sha256: String,
    size_bytes: i64,
    published_at: String,
    signing_key_id: String,
    signature_algorithm: String,
    signature: String,
    release_notes: Option<String>,
}
impl Manifest {
    fn validate(&self, channel: &str, runtime: &str) -> Result<(), ApiError> {
        let signature: String = self
            .signature
            .chars()
            .filter(|c| !c.is_ascii_whitespace())
            .collect();
        if self.version.trim().is_empty()
            || !self.channel.eq_ignore_ascii_case(channel)
            || !self.runtime_identifier.eq_ignore_ascii_case(runtime)
            || !url::Url::parse(&self.download_uri)
                .is_ok_and(|u| u.scheme() == "https" && u.has_host())
            || !valid_hash(&self.sha256)
            || self.size_bytes <= 0
            || DateTime::parse_from_rfc3339(&self.published_at).is_err()
            || self.signing_key_id.trim().is_empty()
            || self.signature_algorithm != "ECDSA-P256-SHA256"
            || !STANDARD.decode(signature).is_ok_and(|b| b.len() == 64)
        {
            return Err(invalid("El manifiesto de actualización es inválido."));
        }
        Ok(())
    }
}

// Keep the temporary file on the destination filesystem for an atomic replacement.
// Its prefix cannot be used as a public package name. TempPath also cleans up on cancellation.
async fn temporary(destination: &FsPath) -> Result<(fs::File, TempPath), ApiError> {
    let directory = destination
        .parent()
        .expect("release paths include a parent")
        .to_owned();
    fs::create_dir_all(&directory).await.map_err(storage)?;
    let (file, path) = Builder::new()
        .prefix("~umd-")
        .tempfile_in(directory)
        .map_err(storage)?
        .into_parts();
    Ok((fs::File::from_std(file), path))
}
async fn commit(mut file: fs::File, temp: TempPath, destination: &FsPath) -> Result<(), ApiError> {
    file.flush().await.map_err(storage)?;
    file.sync_all().await.map_err(storage)?;
    drop(file);
    temp.persist(destination).map_err(|e| storage(e.error))
}
async fn latest(
    State(s): State<ServerState>,
    Path((channel, runtime)): Path<(String, String)>,
) -> ApiResult {
    let channel = segment(&channel)?;
    let runtime = segment(&runtime)?;
    let data = fs::read(s.releases.manifest(&channel, &runtime))
        .await
        .map_err(storage)?;
    let manifest: Manifest =
        serde_json::from_slice(&data).map_err(|e| storage(io::Error::other(e)))?;
    manifest.validate(&channel, &runtime)?;
    Ok(Json(manifest).into_response())
}
async fn publish_manifest(
    State(s): State<ServerState>,
    Path((channel, runtime)): Path<(String, String)>,
    headers: HeaderMap,
    body: Body,
) -> ApiResult {
    authorize_publication(&s, &headers)?;
    let channel = segment(&channel)?;
    let runtime = segment(&runtime)?;
    let bytes = to_bytes(body, umd_core::MAX_DOCUMENT_BYTES)
        .await
        .map_err(|_| invalid("No se pudo leer el manifiesto dentro del límite permitido."))?;
    let manifest: Manifest =
        serde_json::from_slice(&bytes).map_err(|_| invalid("Manifiesto JSON inválido."))?;
    manifest.validate(&channel, &runtime)?;
    let destination = s.releases.manifest(&channel, &runtime);
    let (mut file, temp) = temporary(&destination).await?;
    file.write_all(&bytes).await.map_err(storage)?;
    commit(file, temp, &destination).await?;
    Ok(Json(manifest).into_response())
}
async fn upload(
    State(s): State<ServerState>,
    Path(route): Path<PackageRoute>,
    headers: HeaderMap,
    body: Body,
) -> ApiResult {
    authorize_publication(&s, &headers)?;
    let package = package(&s.releases, route)?;
    let size = headers
        .get(header::CONTENT_LENGTH)
        .and_then(|v| v.to_str().ok()?.parse::<u64>().ok())
        .filter(|n| *n > 0 && *n <= s.releases.maximum_bytes)
        .ok_or_else(|| invalid("Tamaño de paquete ausente o fuera de los límites."))?;
    let expected = headers
        .get("x-umd-sha256")
        .and_then(|h| h.to_str().ok())
        .filter(|s| valid_hash(s))
        .ok_or_else(|| {
            invalid("Se requiere la cabecera X-UMD-SHA256 con el SHA-256 del paquete.")
        })?;
    let _gate = s.releases.upload_gate.lock().await;
    let (mut file, temp) = temporary(&package.path).await?;
    let mut chunks = body.into_data_stream();
    let mut digest = Sha256::new();
    let mut written = 0;
    while let Some(frame) = chunks.next().await {
        let bytes = frame.map_err(|_| invalid("La carga se interrumpió."))?;
        if bytes.len() as u64 > size - written {
            return Err(invalid("El paquete supera su tamaño declarado."));
        }
        file.write_all(&bytes).await.map_err(storage)?;
        digest.update(&bytes);
        written += bytes.len() as u64;
    }
    let sha = format!("{:X}", digest.finalize());
    if written != size || !sha.eq_ignore_ascii_case(expected) {
        return Err(invalid("El tamaño o el SHA-256 del paquete no coincide."));
    }
    commit(file, temp, &package.path).await?;
    Ok(
        Json(json!({"relativePath":package.relative,"sha256":sha,"sizeBytes":written}))
            .into_response(),
    )
}
async fn download(
    State(s): State<ServerState>,
    Path(route): Path<PackageRoute>,
    request: Request,
) -> ApiResult {
    let package = package(&s.releases, route)?;
    let mut response = ServeFile::new(package.path)
        .oneshot(request)
        .await
        .expect("ServeFile is infallible")
        .into_response();
    if response.status().is_success() {
        response.headers_mut().insert(
            header::CONTENT_TYPE,
            HeaderValue::from_static("application/octet-stream"),
        );
        response.headers_mut().insert(
            header::CONTENT_DISPOSITION,
            HeaderValue::from_str(&format!("attachment; filename=\"{}\"", package.name))
                .expect("validated ASCII filename"),
        );
    }
    Ok(response)
}
