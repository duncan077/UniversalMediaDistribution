mod declared;
use crate::CommandResult;
use base64::{engine::general_purpose::STANDARD, Engine};
use dom_query::Document;
use regex::Regex;
use reqwest::{header, Client, Url};
use serde::Serialize;
use serde_json::Value;
use std::{net::SocketAddr, sync::LazyLock, time::Duration};
use tokio::{net::lookup_host, sync::Semaphore, time::timeout};

static REQUESTS: Semaphore = Semaphore::const_new(4);

use umd_core::public_url::{public_ip, web_url};

pub(crate) async fn get_public(
    url: Url,
    request_timeout: Duration,
) -> CommandResult<reqwest::Response> {
    fetch(url, request_timeout, None, false).await
}

async fn fetch(
    url: Url,
    request_timeout: Duration,
    player_page: Option<&Url>,
    first_byte: bool,
) -> CommandResult<reqwest::Response> {
    fetch_media(
        url,
        request_timeout,
        player_page,
        first_byte.then_some("bytes=0-0"),
        None,
    )
    .await
}
pub(crate) async fn fetch_media(
    mut url: Url,
    request_timeout: Duration,
    player_page: Option<&Url>,
    range: Option<&str>,
    referer: Option<&Url>,
) -> CommandResult<reqwest::Response> {
    for _ in 0..6 {
        if !web_url(&url) {
            return Err("Ingresá una dirección pública HTTP o HTTPS.".into());
        }
        if player_page.is_some_and(|page| {
            crate::ad_filter::blocked(url.as_str(), page.as_str(), "document", "GET")
        }) {
            return Err("El bloqueador identificó esta dirección como publicidad.".into());
        }
        let host = url.host_str().unwrap().trim_matches(['[', ']']);
        let addresses: Vec<SocketAddr> = lookup_host((host, url.port_or_known_default().unwrap()))
            .await
            .map_err(|_| "No se pudo localizar el servidor.")?
            .collect();
        if addresses.is_empty() || addresses.iter().any(|a| !public_ip(a.ip())) {
            return Err("La dirección de la fuente no es pública.".into());
        }
        let client = Client::builder()
            .no_proxy()
            .timeout(request_timeout)
            .redirect(reqwest::redirect::Policy::none())
            .resolve_to_addrs(host, &addresses)
            .build()
            .map_err(|e| e.to_string())?;
        let mut request = client.get(url.clone());
        if let Some(range) = range {
            request = request.header(header::RANGE, range);
        }
        if let Some(page) = referer {
            let origin = page.origin().ascii_serialization();
            let reference = if page.origin() == url.origin() {
                page.as_str().to_owned()
            } else {
                format!("{origin}/")
            };
            if page.scheme() != "https" || url.scheme() == "https" {
                request = request
                    .header(header::REFERER, reference)
                    .header(header::ORIGIN, origin);
            }
            request = request.header(
                header::USER_AGENT,
                concat!("UniversalMediaDistribution/", env!("CARGO_PKG_VERSION")),
            );
        }
        let response = request
            .send()
            .await
            .map_err(|_| "No se pudo conectar con la fuente.")?;
        if response.status().is_redirection() {
            let location = response
                .headers()
                .get(header::LOCATION)
                .and_then(|v| v.to_str().ok())
                .ok_or("Redirección inválida.")?;
            url = url
                .join(location)
                .map_err(|_| "Dirección de destino inválida.")?;
        } else if response.status().is_success() {
            return Ok(response);
        } else {
            return Err(format!("La fuente respondió HTTP {}.", response.status()));
        }
    }
    Err("Demasiadas redirecciones al abrir la fuente.".into())
}

pub(crate) async fn bounded_text(
    mut response: reqwest::Response,
    limit: usize,
) -> CommandResult<String> {
    let mut bytes = Vec::new();
    while let Some(chunk) = response
        .chunk()
        .await
        .map_err(|_| "Se interrumpió la descarga.")?
    {
        if bytes.len() + chunk.len() > limit {
            return Err("El documento supera el tamaño permitido.".into());
        }
        bytes.extend_from_slice(&chunk);
    }
    Ok(String::from_utf8_lossy(&bytes).into_owned())
}

#[derive(Debug, Serialize)]
pub struct Stream {
    #[serde(skip_serializing_if = "Option::is_none")]
    referer: Option<String>,
    url: String,
    hls: bool,
    dash: bool,
    tracks: Vec<Subtitle>,
}

#[derive(Debug, Serialize)]
struct Subtitle {
    url: String,
    language: String,
    label: String,
    default: bool,
}

pub(crate) fn media(url: Url, mime: &str) -> Option<Stream> {
    if !web_url(&url) {
        return None;
    }
    direct_media(url, mime)
}
fn direct_media(url: Url, mime: &str) -> Option<Stream> {
    let path = url.path().to_ascii_lowercase();
    let mime = mime
        .split(';')
        .next()
        .unwrap_or("")
        .trim()
        .to_ascii_lowercase();
    let hls = mime.contains("mpegurl") || path.ends_with(".m3u8");
    let dash = mime == "application/dash+xml" || path.ends_with(".mpd");
    (hls || dash
        || mime.starts_with("video/")
        || mime.starts_with("audio/")
        || [".mp4", ".webm", ".m4v", ".mp3", ".ogg", ".aac", ".ts"]
            .iter()
            .any(|ext| path.ends_with(ext)))
    .then(|| Stream {
        referer: None,
        url: url.into(),
        hls,
        dash,
        tracks: Vec::new(),
    })
}

fn matches_host(url: &Url, domains: &[&str]) -> bool {
    url.domain().is_some_and(|host| {
        domains
            .iter()
            .any(|d| host == *d || host.strip_suffix(d).is_some_and(|p| p.ends_with('.')))
    })
}

fn extract(base: &Url, doc: &Document) -> Option<Stream> {
    if let Some(found) = declared::extract(base, doc) {
        return Some(found);
    }
    let stream = |value: &str, mime: &str| {
        let url = base.join(&value.replace("\\/", "/")).ok()?;
        if crate::ad_filter::blocked(url.as_str(), base.as_str(), "media", "GET") {
            return None;
        }
        media(url, mime)
    };
    for el in doc
        .select("video[src], video source[src], audio[src], audio source[src], source[src][type]")
        .iter()
    {
        if let Some(result) = el
            .attr("src")
            .and_then(|v| stream(&v, &el.attr_or("type", "")))
        {
            return Some(result);
        }
    }
    if matches_host(base, &["ok.ru", "odnoklassniki.ru"]) {
        for el in doc.select("[data-options]").iter() {
            let Some(options) = el
                .attr("data-options")
                .and_then(|s| serde_json::from_str::<Value>(&s).ok())
            else {
                continue;
            };
            let v = &options["flashvars"]["metadata"];
            let parsed;
            let meta = if let Some(s) = v.as_str() {
                parsed = serde_json::from_str::<Value>(s).ok();
                parsed.as_ref().unwrap_or(&Value::Null)
            } else {
                v
            };
            for key in ["hlsManifestUrl", "ondemandHls", "hlsMasterPlaylistUrl"] {
                if let Some(found) = meta[key]
                    .as_str()
                    .and_then(|v| stream(v, "application/vnd.apple.mpegurl"))
                {
                    return Some(found);
                }
            }
            let qualities = [
                "mobile", "lowest", "low", "sd", "hd", "full", "quad", "ultra",
            ];
            if let Some((_, found)) = meta["videos"]
                .as_array()
                .into_iter()
                .flatten()
                .filter_map(|v| {
                    Some((
                        qualities
                            .iter()
                            .position(|q| Some(*q) == v["name"].as_str())
                            .unwrap_or(0),
                        stream(v["url"].as_str()?, "video/mp4")?,
                    ))
                })
                .max_by_key(|(rank, _)| *rank)
            {
                return Some(found);
            }
        }
    }
    if matches_host(base, &["mediafire.com"]) {
        if let Some(link) = doc
            .select("a#downloadButton")
            .attr("href")
            .and_then(|v| stream(&v, ""))
        {
            return Some(link);
        }
    }
    static COMMENTS: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"(?s)/\*.*?\*/").unwrap());
    static DPLAYER: LazyLock<Regex> = LazyLock::new(|| {
        Regex::new(r#"(?s)\bvideo\s*:\s*\{\s*url\s*:\s*(?:(atob)\(\s*)?["']([^'"<>]+)["']"#)
            .unwrap()
    });
    static FILE: LazyLock<Regex> = LazyLock::new(|| {
        Regex::new(r#"(?i)\b(?:sources\s*:\s*\[\s*(?:\{\s*(?:src|file)\s*:\s*)?|file\s*:\s*)["']([^'"<>]+)["']"#).unwrap()
    });
    // Video.js accepts either a source object or a direct URL. Inspect only
    // literal arguments to the public player API.
    static PLAYER_SOURCE: LazyLock<Regex> = LazyLock::new(|| {
        Regex::new(
        r#"(?is)\bplayer\.src\s*\(\s*(?:\{[^{};]{0,384}?\b["']?src["']?\s*:\s*)?["']([^"'<>]+)["']"#
    ).unwrap()
    });
    static VOE_HLS: LazyLock<Regex> = LazyLock::new(|| {
        Regex::new(r#"(?is)\bsources\s*=\s*\{[^{};]{0,384}?["']?hls["']?\s*:\s*["']([^"'<>]+)["']"#)
            .unwrap()
    });
    for el in doc.select("script:not([src])").iter() {
        let text = el.text();
        let script = COMMENTS.replace_all(&text, "");
        if let Some(found) = PLAYER_SOURCE
            .captures_iter(&script)
            .find_map(|c| stream(&c[1], ""))
        {
            return Some(found);
        }
        if matches_host(base, &["voe.sx"]) {
            if let Some(found) = VOE_HLS
                .captures_iter(&script)
                .find_map(|c| stream(&c[1], ""))
            {
                return Some(found);
            }
        }
        if script.contains("new DPlayer") {
            if let Some(c) = DPLAYER.captures(&script).filter(|c| c[2].len() <= 16_384) {
                let url = if c.get(1).is_some() {
                    STANDARD
                        .decode(&c[2])
                        .ok()
                        .and_then(|v| String::from_utf8(v).ok())
                } else {
                    Some(c[2].into())
                };
                if let Some(found) = url.and_then(|v| stream(&v, "")) {
                    return Some(found);
                }
            }
        }
        if matches_host(
            base,
            &[
                "vidmoly.to",
                "vidmoly.me",
                "yourupload.com",
                "yucache.net",
                "uqload.cx",
                "uqload.is",
                "uqload.com",
                "vidoza.net",
            ],
        ) || script.contains("jwplayer(")
        {
            if let Some(found) = FILE.captures_iter(&script).find_map(|c| stream(&c[1], "")) {
                return Some(found);
            }
        }
    }
    None
}

enum Page {
    Media(Stream),
    Html(Url, String),
}

fn zilla_playlist(page: &Url) -> Option<Url> {
    if page.host_str()? != "player.zilla-networks.com" {
        return None;
    }
    let mut parts = page.path().trim_matches('/').split('/');
    if parts.next()? != "play" {
        return None;
    }
    let id = parts.next()?;
    if parts.next().is_some()
        || id.is_empty()
        || id.len() > 128
        || !id
            .bytes()
            .all(|b| b.is_ascii_alphanumeric() || matches!(b, b'-' | b'_'))
    {
        return None;
    }
    page.join(&format!("/m3u8/{id}")).ok()
}

async fn zilla_stream<F>(playlist: Url, get: impl Fn(Url, bool) -> F) -> CommandResult<Stream>
where
    F: std::future::Future<Output = CommandResult<reqwest::Response>>,
{
    let mut next = playlist.clone();
    for _ in 0..4 {
        let response = get(next, false).await?;
        let base = response.url().clone();
        let text = bounded_text(response, 2 * 1024 * 1024).await?;
        if !text.trim_start().starts_with("#EXTM3U") {
            break;
        }
        let mut child = false;
        let mut entry = None;
        for line in text.lines().map(str::trim) {
            if line.starts_with("#EXT-X-STREAM-INF:") {
                child = true;
            }
            if let Some((_, uri)) = line
                .strip_prefix("#EXT-X-MAP:")
                .and_then(|s| s.split_once("URI=\""))
            {
                entry = uri.split('"').next();
                break;
            }
            if !line.is_empty() && !line.starts_with('#') {
                entry = Some(line);
                break;
            }
        }
        let Some(url) = entry.and_then(|s| base.join(s).ok()).filter(web_url) else {
            break;
        };
        if child {
            next = url;
            continue;
        }
        let mut segment = get(url, true).await?;
        let mime = segment
            .headers()
            .get(header::CONTENT_TYPE)
            .and_then(|v| v.to_str().ok())
            .unwrap_or("");
        if mime.contains("text/html") || mime.contains("json") {
            break;
        }
        if segment
            .chunk()
            .await
            .ok()
            .flatten()
            .is_some_and(|v| !v.is_empty())
        {
            return Ok(Stream {
                referer: None,
                url: playlist.into(),
                hls: true,
                dash: false,
                tracks: Vec::new(),
            });
        }
        break;
    }
    Err("El servidor de video no publicó segmentos reproducibles.".into())
}

async fn load_page(url: Url, parent: Url) -> CommandResult<Page> {
    if let Some(playlist) = zilla_playlist(&url) {
        return zilla_stream(playlist, |target, probe| {
            fetch(target, Duration::from_secs(10), Some(&url), probe)
        })
        .await
        .map(Page::Media);
    }
    let response = fetch(url, Duration::from_secs(20), Some(&parent), false).await?;
    let base = response.url().clone();
    let mime = response
        .headers()
        .get(header::CONTENT_TYPE)
        .and_then(|v| v.to_str().ok())
        .unwrap_or("");
    if let Some(stream) = media(base.clone(), mime) {
        return Ok(Page::Media(stream));
    }
    Ok(Page::Html(
        base,
        bounded_text(response, 2 * 1024 * 1024).await?,
    ))
}

async fn resolve_pages(
    mut url: Url,
    load: impl AsyncFn(Url, Url) -> CommandResult<Page>,
) -> CommandResult<Stream> {
    let mut parent = url.clone();
    let mut visited = std::collections::HashSet::new();
    for _ in 0..3 {
        url.set_fragment(None);
        if !web_url(&url) || !visited.insert(url.clone()) {
            break;
        }
        if crate::ad_filter::blocked(url.as_str(), parent.as_str(), "media", "GET") {
            return Err("El bloqueador identificó esta dirección como publicidad.".into());
        }
        if let Some(direct) = media(url.clone(), "") {
            return Ok(direct);
        }
        if matches_host(&url, &["nupload.top", "nupload.me"])
            && url.path().trim_end_matches('/') == "/iframe"
        {
            if let Some(next) = url
                .query_pairs()
                .find(|(k, _)| k == "url")
                .and_then(|(_, v)| Url::parse(&v).ok())
                .filter(web_url)
            {
                parent = url;
                url = next;
                continue;
            }
        }
        match load(url, parent).await? {
            Page::Media(stream) => return Ok(stream),
            Page::Html(mut base, html) => {
                base.set_fragment(None);
                visited.insert(base.clone());
                let doc = Document::from(html.as_str());
                if let Some(mut stream) = extract(&base, &doc) {
                    if stream.hls {
                        stream.referer = Some(base.to_string());
                    }
                    return Ok(stream);
                }
                let Some(next) = declared::embed(&base, &doc) else {
                    break;
                };
                parent = base;
                url = next;
            }
        }
    }
    Err("La página no publica una fuente compatible con el reproductor.".into())
}

pub(crate) fn trusted_server(store: &umd_core::CatalogStore, url: &Url) -> CommandResult<bool> {
    if !matches!(url.scheme(), "http" | "https")
        || !url.username().is_empty()
        || url.password().is_some()
    {
        return Ok(false);
    }
    Ok(store
        .setting("server_url")
        .map_err(|e| e.to_string())?
        .and_then(|s| Url::parse(&s).ok())
        .is_some_and(|s| s.origin() == url.origin()))
}
#[tauri::command]
pub async fn resolve_video(
    address: String,
    store: tauri::State<'_, umd_core::CatalogStore>,
) -> CommandResult<Stream> {
    let url = Url::parse(address.trim()).map_err(|_| "Enlace de video inválido.")?;
    if trusted_server(&store, &url)? {
        if let Some(stream) = direct_media(url.clone(), "") {
            return Ok(stream);
        }
    }
    let _permit = REQUESTS
        .try_acquire()
        .map_err(|_| "Hay otras fuentes cargando. Volvé a intentar.")?;
    timeout(Duration::from_secs(35), resolve_pages(url, load_page))
        .await
        .map_err(|_| "La fuente tardó demasiado en responder.")?
}

#[derive(Serialize)]
pub struct Channel {
    logo: Option<String>,
    name: String,
    group: String,
    url: String,
}

fn channels(base: &Url, text: &str) -> CommandResult<Vec<Channel>> {
    let mut lines = text.trim_start_matches('\u{feff}').lines();
    if !lines
        .next()
        .is_some_and(|s| s.trim().starts_with("#EXTM3U"))
    {
        return Err("El archivo no es una lista M3U.".into());
    }
    static GROUP: LazyLock<Regex> =
        LazyLock::new(|| Regex::new(r#"(?i)\bgroup-title="([^"]*)""#).unwrap());
    static LOGO: LazyLock<Regex> =
        LazyLock::new(|| Regex::new(r#"(?i)\btvg-logo="([^"]+)""#).unwrap());
    let mut info = None;
    let mut result = Vec::new();
    for line in lines.map(str::trim) {
        if line.starts_with("#EXT-X-") {
            return Err("Es una transmisión HLS. Abrila como enlace de video.".into());
        }
        if let Some(meta) = line.strip_prefix("#EXTINF:") {
            let mut quoted = false;
            let split = meta.char_indices().find(|(_, c)| {
                if *c == '"' {
                    quoted = !quoted;
                }
                *c == ',' && !quoted
            });
            info = split.map(|(i, _)| {
                (
                    meta[i + 1..].trim(),
                    LOGO.captures(&meta[..i])
                        .and_then(|m| base.join(&m[1]).ok())
                        .filter(web_url)
                        .map(Into::into),
                    GROUP
                        .captures(&meta[..i])
                        .map(|c| c[1].to_owned())
                        .unwrap_or_default(),
                )
            });
        } else if !line.is_empty() && !line.starts_with('#') {
            if let Some((name, logo, group)) = info.take() {
                if let Ok(url) = base.join(line) {
                    if !name.is_empty() && web_url(&url) {
                        result.push(Channel {
                            name: name.into(),
                            logo,
                            group,
                            url: url.into(),
                        });
                    }
                }
            }
        }
        if result.len() > 50_000 {
            return Err("La lista supera los 50.000 canales.".into());
        }
    }
    Ok(result)
}

fn grid_channels(base: &Url, html: &str) -> (Vec<Channel>, Option<Url>) {
    let doc = Document::from(html);
    let mut seen = std::collections::HashSet::new();
    let mut rows = Vec::new();
    let mut catalog = None;
    for link in doc.select("a[href]").nodes().iter() {
        let Some(url) = link
            .attr("href")
            .and_then(|s| base.join(s.as_ref()).ok())
            .filter(|u| {
                web_url(u)
                    && u.origin() == base.origin()
                    && u.path() != "/"
                    && u.path() != base.path()
            })
        else {
            continue;
        };
        let mut name = link.text().split_whitespace().collect::<Vec<_>>().join(" ");
        if name.is_empty() {
            name = link
                .find(&["img"])
                .first()
                .and_then(|img| img.attr("alt"))
                .or_else(|| link.attr("title"))
                .map(|s| s.to_string())
                .unwrap_or_default();
        }
        let lower = name.to_lowercase();
        if lower.contains("parrilla") || (lower.contains("ver") && lower.contains("canales")) {
            catalog.get_or_insert(url);
            continue;
        }
        if name.is_empty()
            || lower.contains("soporte")
            || lower.contains("telegram")
            || url.path().contains("/category/")
            || url.path().contains("/tag/")
            || !seen.insert(url.clone())
        {
            continue;
        }
        let logo = link
            .find(&["img"])
            .first()
            .and_then(|img| img.attr("data-src").or_else(|| img.attr("src")))
            .and_then(|href| base.join(&href).ok())
            .filter(web_url)
            .map(Into::into);
        rows.push(Channel {
            logo,
            name,
            group: String::new(),
            url: url.into(),
        });
        if rows.len() >= 2000 {
            break;
        }
    }
    (rows, catalog)
}

#[tauri::command]
pub async fn live_channels(address: String, grid: Option<bool>) -> CommandResult<Vec<Channel>> {
    let url = Url::parse(address.trim()).map_err(|_| "Dirección M3U inválida.")?;
    let _permit = REQUESTS
        .try_acquire()
        .map_err(|_| "Hay otras fuentes cargando.")?;
    timeout(Duration::from_secs(35), async {
        let response = get_public(url, Duration::from_secs(20)).await?;
        let base = response.url().clone();
        let body = bounded_text(response, 8 * 1024 * 1024).await?;
        if grid != Some(true) {
            return channels(&base, &body);
        }
        let (rows, catalog) = grid_channels(&base, &body);
        if !rows.is_empty() {
            return Ok(rows);
        }
        if let Some(url) = catalog {
            let response = get_public(url, Duration::from_secs(15)).await?;
            let final_url = response.url().clone();
            let (rows, _) =
                grid_channels(&final_url, &bounded_text(response, 4 * 1024 * 1024).await?);
            if !rows.is_empty() {
                return Ok(rows);
            }
        }
        Err("La grilla no publicó canales.".into())
    })
    .await
    .map_err(|_| "Se agotó el tiempo al cargar la lista.")?
}

#[cfg(test)]
mod tests;

#[cfg(test)]
mod existing_formats {
    use super::*;
    #[test]
    fn reads_public_player_formats_without_using_unrelated_links() {
        let cases = [
            (
                "watch.example",
                r#"<video><source src="https://ads.example/preroll.mp4"><source src="/program.mp4"></video>"#,
                "/program.mp4",
            ),
            (
                "movies.example",
                r#"<video><source src="/media.mp4?x=1&amp;y=2"></video>"#,
                "/media.mp4?x=1&y=2",
            ),
            (
                "jkanime.net",
                r#"<script>new DPlayer({video:{url:atob("L2ZpbG0ubXA0")}})</script>"#,
                "/film.mp4",
            ),
            (
                "vidmoly.to",
                r#"<script>player({sources:[{file:"/play.m3u8"}]})</script>"#,
                "/play.m3u8",
            ),
            (
                "yourupload.com",
                r#"<script>jwplayer().setup({file:"/play.mp4"})</script>"#,
                "/play.mp4",
            ),
            (
                "ok.ru",
                r#"<div data-options='{"flashvars":{"metadata":{"hlsMasterPlaylistUrl":"/playlist"}}}'></div>"#,
                "/playlist",
            ),
            (
                "mediafire.com",
                r#"<a id="downloadButton" href="/film.webm">Ver</a>"#,
                "/film.webm",
            ),
        ];
        for (host, html, path) in cases {
            let base = Url::parse(&format!("https://{host}/embed")).unwrap();
            assert_eq!(
                extract(&base, &Document::from(html)).unwrap().url,
                base.join(path).unwrap().as_str()
            );
        }
        let base = Url::parse("https://movies.example/playlist/main.m3u").unwrap();
        assert!(extract(&base, &Document::from("<a href='ad.mp4'>Anuncio</a>")).is_none());
        let list = channels(&base,"#EXTM3U\n#EXTINF:-1 group-title=\"TV, AR\",Canal Uno\nstream/live.m3u8\n#EXTINF:-1,Local\nhttp://127.0.0.1/test.mp4").unwrap();
        assert_eq!(list.len(), 1);
        assert_eq!(list[0].group, "TV, AR");
        assert!(list[0].url.ends_with("/playlist/stream/live.m3u8"));
    }
}
