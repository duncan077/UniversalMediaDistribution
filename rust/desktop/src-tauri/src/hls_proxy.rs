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
static SRT_TIMING: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r"^(\d{2,}:\d{2}:\d{2})[,.](\d{3})\s+-->\s+(\d{2,}:\d{2}:\d{2})[,.](\d{3})(?:\s.*)?$").unwrap()
});
static VTT_TIMING: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r"^((?:\d{2,}:)?\d{2}:\d{2})\.(\d{3})\s+-->\s+((?:\d{2,}:)?\d{2}:\d{2})\.(\d{3})(?:\s.*)?$").unwrap()
});
#[derive(Clone)]
struct Relay {
    page: Option<Url>,
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
    VlcSubtitle,
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
            Kind::VlcSubtitle => "track.srt",
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
    referer: Option<&str>,
    hls: bool,
    tracks: &[String],
) -> CommandResult<Source> {
    start_relay(id, address, referer, hls, tracks.iter().map(|url| (url.as_str(), None))).await
}

pub async fn start_vlc(
    id: Uuid,
    address: &str,
    referer: Option<&str>,
    hls: bool,
    tracks: &[serde_json::Value],
) -> CommandResult<Source> {
    start_relay(id, address, referer, hls, tracks.iter().map(|track| {
        (track["url"].as_str().unwrap_or_default(), Some(track["language"].as_str().unwrap_or_default()))
    })).await
}

async fn start_relay<'a>(
    id: Uuid,
    address: &str,
    referer: Option<&str>,
    hls: bool,
    tracks: impl Iterator<Item = (&'a str, Option<&'a str>)>,
) -> CommandResult<Source> {
    let parse = |s: &str| {
        Url::parse(s)
            .ok()
            .filter(umd_core::public_url::web_url)
            .ok_or("Fuente HLS pública inválida.")
    };
    let page = referer.map(parse).transpose()?;
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
    let root = if relay.page.is_some() {
        relay.link(parse(address)?, if hls { Kind::Hls } else { Kind::Auto })?
    } else {
        address.to_owned()
    };
    let tracks = tracks.take(100).map(|(address, language)| {
        if let Some(language) = language {
            if language.is_empty() || language.len() > 48 || !language.bytes().all(|c| c.is_ascii_alphanumeric() || c == b'-') {
                return String::new();
            }
            // VLC reads the language of external SubRip tracks from their filenames.
            return parse(address).ok().and_then(|url| relay.link(url, Kind::VlcSubtitle).ok())
                .map(|url| url.trim_end_matches(".srt").to_owned() + "." + language + ".srt")
                .unwrap_or_default();
        }
        parse(address).ok().and_then(|url| relay.link(url, Kind::Subtitle).ok()).unwrap_or_default()
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
    referer: Option<String>,
    hls: bool,
    tracks: Vec<String>,
) -> CommandResult<Source> {
    if window.label() != "main" {
        return Err("Acción reservada a la biblioteca.".into());
    }
    start(id, &address, referer.as_deref(), hls, &tracks).await
}

fn subtitle_vtt(text: String) -> Option<String> {
    let text = text.trim_start_matches('\u{feff}');
    if text.lines().next().is_some_and(|line| {
        line == "WEBVTT" || line.starts_with("WEBVTT ") || line.starts_with("WEBVTT\t")
    }) {
        return Some(text.to_owned());
    }
    let mut out = String::from("WEBVTT\n\n");
    let mut lines = text.lines().peekable();
    while let Some(line) = lines.next() {
        let Some(timing) = SRT_TIMING.captures(line.trim()) else {
            continue;
        };
        out.push_str(&format!("{}.{} --> {}.{}\n", &timing[1], &timing[2], &timing[3], &timing[4]));
        while lines.peek().is_some_and(|line| !line.trim().is_empty()) {
            out.push_str(lines.next().unwrap());
            out.push('\n');
        }
        out.push('\n');
    }
    (out.len() > "WEBVTT\n\n".len()).then_some(out)
}

fn subtitle_srt(text: String) -> Option<String> {
    let text = text.trim_start_matches('\u{feff}');
    if !text.starts_with("WEBVTT") {
        return text.lines().any(|line| SRT_TIMING.is_match(line.trim())).then(|| text.to_owned());
    }
    let mut out = String::new();
    let mut lines = text.lines().peekable();
    let mut index = 1;
    while let Some(line) = lines.next() {
        let Some(timing) = VTT_TIMING.captures(line.trim()) else { continue };
        let start = &timing[1];
        let end = &timing[3];
        let hours = |time: &str| if time.len() == 5 { "00:" } else { "" };
        out.push_str(&format!("{index}\n{}{start},{} --> {}{end},{}\n", hours(start), &timing[2], hours(end), &timing[4]));
        while lines.peek().is_some_and(|line| !line.trim().is_empty()) {
            out.push_str(lines.next().unwrap());
            out.push('\n');
        }
        out.push('\n');
        index += 1;
    }
    (!out.is_empty()).then_some(out)
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
    let subtitle = matches!(resource.kind, Kind::Subtitle | Kind::VlcSubtitle);
    let range = headers
        .get(header::RANGE)
        .and_then(|s| s.to_str().ok())
        .filter(|s| {
            !subtitle
                && s.len() <= 80
                && s.strip_prefix("bytes=")
                    .is_some_and(|v| v.bytes().all(|b| b.is_ascii_digit() || b == b'-'))
        });
    let Ok(res) = crate::streams::relay_media(resource.url, relay.page.as_ref(), range).await else {
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
    if subtitle {
        if !res.status().is_success() {
            return StatusCode::BAD_GATEWAY.into_response();
        }
        let Ok(Ok(text)) = tokio::time::timeout(
            Duration::from_secs(12),
            crate::streams::bounded_text(res, 1024 * 1024),
        ).await else {
            return StatusCode::BAD_GATEWAY.into_response();
        };
        let (text, mime) = if resource.kind == Kind::VlcSubtitle {
            (subtitle_srt(text), "application/x-subrip; charset=utf-8")
        } else {
            (subtitle_vtt(text), "text/vtt; charset=utf-8")
        };
        let Some(text) = text else {
            return StatusCode::BAD_GATEWAY.into_response();
        };
        return builder.header(header::CONTENT_TYPE, mime)
            .body(text.into()).unwrap();
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
    let mime = if mime.starts_with("video/") || mime.starts_with("audio/") {
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
    fn converts_srt_sidecars_and_preserves_existing_webvtt() {
        let srt = "\u{feff}1\r\n00:00:38,208 --> 00:00:44,700\r\n<i>Prueba</i>\r\n\r\n2\r\n00:01:00,000 --> 00:01:02,000\r\nDos\r\n";
        let vtt = "WEBVTT\n\n00:00:38.208 --> 00:00:44.700\n<i>Prueba</i>\n\n00:01:00.000 --> 00:01:02.000\nDos\n\n";
        assert_eq!(subtitle_vtt(srt.into()).as_deref(), Some(vtt));
        assert_eq!(subtitle_vtt(vtt.into()).as_deref(), Some(vtt));
        assert_eq!(subtitle_vtt("invalid subtitle".into()), None);
        let short_vtt = "WEBVTT\n\n00:38.208 --> 00:44.700 align:start\n<i>Prueba</i>\n";
        assert_eq!(subtitle_srt(short_vtt.into()).as_deref(), Some("1\n00:00:38,208 --> 00:00:44,700\n<i>Prueba</i>\n\n"));
        assert_eq!(subtitle_srt(srt.into()).as_deref(), Some(srt.trim_start_matches('\u{feff}')));
    }

    #[test]
    fn manifest_rewrites_variants_fragments_and_inline_attributes() {
        let base = Url::parse("https://media.example.org/a/master.m3u8").unwrap();
        let relay = Relay {
            page: Some(base.clone()),
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
