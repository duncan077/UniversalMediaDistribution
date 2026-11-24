use crate::CommandResult;
use axum::{
    body::Body,
    extract::{Path, State},
    http::{header, HeaderMap, HeaderValue, StatusCode},
    response::{IntoResponse, Response},
    routing::get,
    Router,
};
use regex::Regex;
use reqwest::Url;
use serde::Serialize;
use std::{
    collections::HashMap,
    sync::{Arc, LazyLock, Mutex},
    time::Duration,
};
use tokio::sync::{oneshot, Semaphore};
use uuid::Uuid;

type ActiveRelays = HashMap<String, (Uuid, oneshot::Sender<()>)>;
static ACTIVE: LazyLock<Mutex<ActiveRelays>> = LazyLock::new(Default::default);
static URI: LazyLock<Regex> = LazyLock::new(|| Regex::new(r#"\bURI="([^"]*)""#).unwrap());
#[derive(Clone)]
struct Relay {
    page: Url,
    token: Uuid,
    origin: String,
    resources: Arc<Mutex<HashMap<Uuid, Resource>>>,
    permits: Arc<Semaphore>,
}

#[derive(Clone, Copy, PartialEq)]
enum Kind {
    Auto,
    Hls,
    Subtitle,
}

#[derive(Clone)]
struct Resource {
    url: Url,
    kind: Kind,
}

#[derive(Serialize)]
pub struct Source {
    pub url: String,
    pub tracks: Vec<String>,
}

impl Relay {
    fn link(&self, url: Url, kind: Kind) -> CommandResult<String> {
        if !umd_core::public_url::web_url(&url) {
            return Err("Dirección HLS inválida.".into());
        }
        let id = Uuid::new_v5(
            &self.token,
            &[&[kind as u8], url.as_str().as_bytes()].concat(),
        );
        let mut map = self.resources.lock().map_err(|_| "Reproductor ocupado.")?;
        if map.len() >= 32768 && !map.contains_key(&id) {
            return Err("La transmisión publicó demasiados recursos.".into());
        }
        let name = match kind {
            Kind::Hls => "stream.m3u8",
            Kind::Subtitle => "track.vtt",
            Kind::Auto if url.path().to_ascii_lowercase().ends_with(".mp4") => "video.mp4",
            Kind::Auto => "media",
        };
        map.insert(id, Resource { url, kind });
        Ok(format!("{}/{}/{id}/{name}", self.origin, self.token))
    }
    fn manifest(&self, base: &Url, body: &str) -> CommandResult<String> {
        if !body.trim_start_matches('\u{feff}').starts_with("#EXTM3U") {
            return Err("Lista HLS inválida.".into());
        }
        let mut out = String::new();
        let mut variant = false;
        for line in body.trim_start_matches('\u{feff}').lines().map(str::trim) {
            if line.starts_with("#EXT-X-DEFINE") || line.contains("{$") {
                return Err("La lista HLS usa variables externas.".into());
            }
            if line.starts_with('#') {
                if !(line == "#EXTM3U"
                    || line.starts_with("#EXT-X-")
                    || line.starts_with("#EXTINF:"))
                {
                    continue;
                }
                if line.contains("URI=") {
                    let mut at = 0;
                    for c in URI.captures_iter(line) {
                        let span = c.get(1).unwrap();
                        out.push_str(&line[at..span.start()]);
                        out.push_str(
                            &self.link(
                                base.join(span.as_str())
                                    .map_err(|_| "Dirección HLS inválida.")?,
                                if line.starts_with("#EXT-X-MEDIA:")
                                    || line.starts_with("#EXT-X-I-FRAME-STREAM-INF:")
                                    || line.starts_with("#EXT-X-RENDITION-REPORT:")
                                {
                                    Kind::Hls
                                } else {
                                    Kind::Auto
                                },
                            )?,
                        );
                        at = span.end();
                    }
                    if at == 0 {
                        return Err("Atributo HLS no compatible.".into());
                    }
                    out.push_str(&line[at..]);
                } else {
                    out.push_str(line);
                }
                variant |= line.starts_with("#EXT-X-STREAM-INF:");
            } else if !line.is_empty() {
                let kind = if std::mem::take(&mut variant) {
                    Kind::Hls
                } else {
                    Kind::Auto
                };
                out.push_str(&self.link(
                    base.join(line).map_err(|_| "Dirección HLS inválida.")?,
                    kind,
                )?);
            }
            out.push('\n');
        }
        Ok(out)
    }
}
pub fn end(id: &str) {
    if let Ok(mut active) = ACTIVE.lock() {
        active.remove(id);
    }
}
fn end_token(id: &str, token: Uuid) {
    if let Ok(mut active) = ACTIVE.lock() {
        if active.get(id).is_some_and(|(current, _)| *current == token) {
            active.remove(id);
        }
    }
}
pub async fn start(
    id: Uuid,
    address: &str,
    referer: &str,
    hls: bool,
    tracks: &[String],
) -> CommandResult<Source> {
    let parse = |s: &str| {
        Url::parse(s)
            .ok()
            .filter(umd_core::public_url::web_url)
            .ok_or("Fuente HLS pública inválida.")
    };
    let page = parse(referer)?;
    let url = parse(address)?;
    let listener = tokio::net::TcpListener::bind((std::net::Ipv4Addr::LOCALHOST, 0))
        .await
        .map_err(|e| e.to_string())?;
    let relay = Relay {
        page,
        token: Uuid::new_v4(),
        origin: format!(
            "http://{}",
            listener.local_addr().map_err(|e| e.to_string())?
        ),
        resources: Default::default(),
        permits: Arc::new(Semaphore::new(6)),
    };
    let token = relay.token;
    let root = relay.link(url, if hls { Kind::Hls } else { Kind::Auto })?;
    let tracks = tracks
        .iter()
        .take(100)
        .map(|s| {
            parse(s)
                .ok()
                .and_then(|url| relay.link(url, Kind::Subtitle).ok())
                .unwrap_or_default()
        })
        .collect();
    let id = id.to_string();
    let (tx, rx) = oneshot::channel();
    {
        let mut active = ACTIVE.lock().map_err(|_| "Reproductor ocupado.")?;
        if active.len() >= 4 && !active.contains_key(&id) {
            return Err("Hay demasiados reproductores abiertos.".into());
        }
        active.insert(id.clone(), (token, tx));
    }
    tauri::async_runtime::spawn(async move {
        let app = Router::new()
            .route(
                "/{token}/{resource}/{name}",
                get(resource).options(preflight),
            )
            .with_state(relay);
        let _ = axum::serve(listener, app)
            .with_graceful_shutdown(async {
                let _ = tokio::time::timeout(Duration::from_secs(24 * 3600), rx).await;
            })
            .await;
        end_token(&id, token);
    });
    Ok(Source { url: root, tracks })
}

#[tauri::command]
pub async fn web_stream_relay(
    window: tauri::WebviewWindow,
    id: Uuid,
    address: String,
    referer: String,
    hls: bool,
    tracks: Vec<String>,
) -> CommandResult<Source> {
    if window.label() != "main" {
        return Err("Acción reservada a la biblioteca.".into());
    }
    start(id, &address, &referer, hls, &tracks).await
}

#[tauri::command]
pub fn close_stream_relay(window: tauri::WebviewWindow, id: Uuid) {
    if window.label() == "main" {
        end(&id.to_string());
    }
}

fn origin(headers: &HeaderMap) -> Option<Option<HeaderValue>> {
    let Some(origin) = headers.get(header::ORIGIN) else {
        return Some(None);
    };
    matches!(
        origin.to_str().ok()?,
        "http://tauri.localhost"
            | "https://tauri.localhost"
            | "tauri://localhost"
            | "http://127.0.0.1:1420"
            | "null"
    )
    .then(|| Some(origin.clone()))
}

async fn preflight(
    State(relay): State<Relay>,
    Path((token, _, _)): Path<(Uuid, Uuid, String)>,
    headers: HeaderMap,
) -> Response {
    let Some(Some(origin)) = origin(&headers).filter(|_| token == relay.token) else {
        return StatusCode::FORBIDDEN.into_response();
    };
    Response::builder()
        .status(StatusCode::NO_CONTENT)
        .header(header::ACCESS_CONTROL_ALLOW_ORIGIN, origin)
        .header(header::ACCESS_CONTROL_ALLOW_HEADERS, "range")
        .header(header::ACCESS_CONTROL_ALLOW_METHODS, "GET, HEAD")
        .header(header::VARY, "Origin")
        .body(Body::empty())
        .unwrap()
}
async fn resource(
    State(relay): State<Relay>,
    Path((token, id, _)): Path<(Uuid, Uuid, String)>,
    headers: HeaderMap,
) -> Response {
    let Some(origin) = origin(&headers).filter(|_| token == relay.token) else {
        return StatusCode::NOT_FOUND.into_response();
    };
    let Some(resource) = relay
        .resources
        .lock()
        .ok()
        .and_then(|m| m.get(&id).cloned())
    else {
        return StatusCode::NOT_FOUND.into_response();
    };
    let Ok(permit) = relay.permits.clone().try_acquire_owned() else {
        return StatusCode::SERVICE_UNAVAILABLE.into_response();
    };
    let range = headers
        .get(header::RANGE)
        .and_then(|s| s.to_str().ok())
        .filter(|s| {
            s.len() <= 80
                && s.strip_prefix("bytes=")
                    .is_some_and(|v| v.bytes().all(|b| b.is_ascii_digit() || b == b'-'))
        });
    let Ok(res) = crate::streams::relay_media(resource.url, &relay.page, range).await else {
        return StatusCode::BAD_GATEWAY.into_response();
    };
    let mime = res
        .headers()
        .get(header::CONTENT_TYPE)
        .and_then(|s| s.to_str().ok())
        .unwrap_or("application/octet-stream")
        .to_owned();
    let final_url = res.url().clone();
    let mime = mime.to_ascii_lowercase();
    let is_list = resource.kind == Kind::Hls
        || mime.contains("mpegurl")
        || final_url.path().to_ascii_lowercase().ends_with(".m3u8");
    if mime.starts_with("text/html") || mime.contains("json") || mime.contains("javascript") {
        return StatusCode::BAD_GATEWAY.into_response();
    }
    let mut builder = Response::builder()
        .status(res.status())
        .header(header::CACHE_CONTROL, "no-store")
        .header(header::X_CONTENT_TYPE_OPTIONS, "nosniff")
        .header(header::VARY, "Origin");
    if let Some(origin) = origin {
        builder = builder
            .header(header::ACCESS_CONTROL_ALLOW_ORIGIN, origin)
            .header(
                header::ACCESS_CONTROL_EXPOSE_HEADERS,
                "Content-Range, Accept-Ranges, Content-Length",
            );
    }
    if is_list {
        let Ok(text) = crate::streams::bounded_text(res, 2 << 20).await else {
            return StatusCode::BAD_GATEWAY.into_response();
        };
        let Ok(text) = relay.manifest(&final_url, &text) else {
            return StatusCode::BAD_GATEWAY.into_response();
        };
        return builder
            .header(header::CONTENT_TYPE, "application/vnd.apple.mpegurl")
            .body(text.into())
            .unwrap();
    }
    for key in [
        header::CONTENT_RANGE,
        header::ACCEPT_RANGES,
        header::CONTENT_LENGTH,
    ] {
        if let Some(v) = res.headers().get(&key) {
            builder = builder.header(key, v);
        }
    }
    let mime = if resource.kind == Kind::Subtitle {
        "text/vtt"
    } else if mime.starts_with("video/") || mime.starts_with("audio/") {
        &mime
    } else {
        "application/octet-stream"
    };
    let body =
        futures_util::stream::try_unfold((res, permit), |(mut upstream, permit)| async move {
            Ok::<_, reqwest::Error>(
                upstream
                    .chunk()
                    .await?
                    .map(|bytes| (bytes, (upstream, permit))),
            )
        });
    builder
        .header(header::CONTENT_TYPE, mime)
        .body(Body::from_stream(body))
        .unwrap()
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn manifest_rewrites_variants_fragments_and_inline_attributes() {
        let base = Url::parse("https://media.example.org/a/master.m3u8").unwrap();
        let relay = Relay {
            page: base.clone(),
            token: Uuid::new_v4(),
            origin: "http://127.0.0.1:8181".into(),
            resources: Default::default(),
            permits: Arc::new(Semaphore::new(1)),
        };
        let rewritten=relay.manifest(&base,"#EXTM3U\n#EXT-X-MAP:URI=\"init.mp4\"\n#EXT-X-STREAM-INF:BANDWIDTH=1000\nlow/index.m3u8\n#EXTVLCOPT:http-referrer=outer\n").unwrap();
        assert!(!rewritten.contains("EXTVLCOPT") && !rewritten.contains("init.mp4"));
        assert_eq!(rewritten.matches(&relay.origin).count(), 2);
        assert!(relay
            .resources
            .lock()
            .unwrap()
            .values()
            .any(|resource| resource.url.path() == "/a/low/index.m3u8"));
        assert!(relay
            .manifest(&base, "#EXTM3U\nfile:///tmp/file.ts")
            .is_err());
    }
}
