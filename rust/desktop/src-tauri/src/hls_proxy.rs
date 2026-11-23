use crate::CommandResult;
use axum::{
    extract::{Path, State},
    http::{header, HeaderMap, StatusCode},
    response::{IntoResponse, Response},
    routing::get,
    Router,
};
use regex::Regex;
use reqwest::Url;
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
    resources: Arc<Mutex<HashMap<Uuid, Url>>>,
    permits: Arc<Semaphore>,
}
impl Relay {
    fn link(&self, url: Url) -> CommandResult<String> {
        if !umd_core::public_url::web_url(&url) {
            return Err("Dirección HLS inválida.".into());
        }
        let id = Uuid::new_v5(&self.token, url.as_str().as_bytes());
        let mut map = self.resources.lock().map_err(|_| "Reproductor ocupado.")?;
        if map.len() >= 32768 && !map.contains_key(&id) {
            return Err("La transmisión publicó demasiados recursos.".into());
        }
        map.insert(id, url);
        Ok(format!("{}/{}/{id}", self.origin, self.token))
    }
    fn manifest(&self, base: &Url, body: &str) -> CommandResult<String> {
        if !body.trim_start_matches('\u{feff}').starts_with("#EXTM3U") {
            return Err("Lista HLS inválida.".into());
        }
        let mut out = String::new();
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
            } else if !line.is_empty() {
                out.push_str(&self.link(base.join(line).map_err(|_| "Dirección HLS inválida.")?)?);
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
pub async fn start(args: &serde_json::Value, address: &str) -> CommandResult<String> {
    let parse = |s: &str| {
        Url::parse(s)
            .ok()
            .filter(umd_core::public_url::web_url)
            .ok_or("Fuente HLS pública inválida.")
    };
    let page = parse(args["referer"].as_str().unwrap_or(""))?;
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
    let root = relay.link(url)?;
    let id = args["id"].as_str().ok_or("Sesión inválida.")?.to_owned();
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
            .route("/{token}/{resource}", get(resource))
            .with_state(relay);
        let _ = axum::serve(listener, app)
            .with_graceful_shutdown(async {
                let _ = tokio::time::timeout(Duration::from_secs(24 * 3600), rx).await;
            })
            .await;
        end_token(&id, token);
    });
    Ok(root)
}
async fn resource(
    State(relay): State<Relay>,
    Path((token, id)): Path<(Uuid, Uuid)>,
    headers: HeaderMap,
) -> Response {
    if token != relay.token {
        return StatusCode::NOT_FOUND.into_response();
    }
    let Some(url) = relay
        .resources
        .lock()
        .ok()
        .and_then(|m| m.get(&id).cloned())
    else {
        return StatusCode::NOT_FOUND.into_response();
    };
    let Ok(_permit) = relay.permits.try_acquire() else {
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
    let Ok(mut res) = crate::streams::fetch_media(
        url,
        Duration::from_secs(30),
        Some(&relay.page),
        range,
        Some(&relay.page),
    )
    .await
    else {
        return StatusCode::BAD_GATEWAY.into_response();
    };
    let mime = res
        .headers()
        .get(header::CONTENT_TYPE)
        .and_then(|s| s.to_str().ok())
        .unwrap_or("application/octet-stream")
        .to_owned();
    let final_url = res.url().clone();
    let mut is_list = mime.to_lowercase().contains("mpegurl")
        || final_url.path().to_ascii_lowercase().ends_with(".m3u8");
    let mut builder = Response::builder()
        .status(res.status())
        .header(header::CACHE_CONTROL, "no-store");
    for key in [header::CONTENT_RANGE, header::ACCEPT_RANGES] {
        if let Some(v) = res.headers().get(&key) {
            builder = builder.header(key, v);
        }
    }
    let mut bytes = Vec::new();
    loop {
        match res.chunk().await {
            Ok(Some(chunk)) => {
                if chunk.len() > (if is_list { 2 << 20 } else { 32 << 20 }) - bytes.len() {
                    return StatusCode::PAYLOAD_TOO_LARGE.into_response();
                }
                bytes.extend_from_slice(&chunk);
            }
            Ok(None) => break,
            Err(_) => return StatusCode::BAD_GATEWAY.into_response(),
        }
    }
    is_list |= bytes.starts_with(b"#EXTM3U");
    if is_list {
        if bytes.len() > 2 << 20 {
            return StatusCode::PAYLOAD_TOO_LARGE.into_response();
        }
        let Ok(text) = std::str::from_utf8(&bytes) else {
            return StatusCode::BAD_GATEWAY.into_response();
        };
        let Ok(text) = relay.manifest(&final_url, text) else {
            return StatusCode::BAD_GATEWAY.into_response();
        };
        return builder
            .header(header::CONTENT_TYPE, "application/vnd.apple.mpegurl")
            .body(text.into())
            .unwrap();
    }
    builder
        .header(header::CONTENT_TYPE, mime)
        .body(bytes.into())
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
            .any(|url| url.path() == "/a/low/index.m3u8"));
        assert!(relay
            .manifest(&base, "#EXTM3U\nfile:///tmp/file.ts")
            .is_err());
    }
}
