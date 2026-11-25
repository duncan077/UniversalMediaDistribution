mod declared;
mod hosters;
mod pixeldrain;
use crate::CommandResult;
use dom_query::Document;
use regex::Regex;
use reqwest::{header, Client, Url};
use serde::Serialize;
use serde_json::Value;
use std::{
    net::SocketAddr,
    sync::{Arc, LazyLock},
    time::Duration,
};
use tokio::{net::lookup_host, sync::Semaphore, time::timeout};

static REQUESTS: Semaphore = Semaphore::const_new(4);

use umd_core::public_url::{public_ip, web_url};
use umd_providers::browser_http;

struct PublicDns;
impl reqwest::dns::Resolve for PublicDns {
    fn resolve(&self, name: reqwest::dns::Name) -> reqwest::dns::Resolving {
        Box::pin(async move {
            // Reqwest supplies the URL's port after resolution. Returning the checked
            // addresses directly avoids a second DNS lookup at connect time.
            let addresses: Vec<SocketAddr> = lookup_host((name.as_str(), 0)).await?.collect();
            if addresses.is_empty() || addresses.iter().any(|address| !public_ip(address.ip())) {
                return Err(std::io::Error::new(
                    std::io::ErrorKind::PermissionDenied,
                    "Public addresses required",
                )
                .into());
            }
            Ok(Box::new(addresses.into_iter()) as reqwest::dns::Addrs)
        })
    }
}

static PUBLIC_CLIENT: LazyLock<Result<Client, reqwest::Error>> = LazyLock::new(|| {
    browser_http::client_builder()
        .no_proxy()
        .connect_timeout(Duration::from_secs(15))
        .read_timeout(Duration::from_secs(30))
        .pool_idle_timeout(Duration::from_secs(45))
        .pool_max_idle_per_host(2)
        .redirect(reqwest::redirect::Policy::none())
        .dns_resolver(Arc::new(PublicDns))
        .build()
});

#[derive(Clone, Copy, PartialEq)]
enum RequestKind {
    Get,
    Page,
    Image,
    Relay,
    LoadX,
    UpnShare,
}

pub(crate) async fn get_public(
    url: Url,
    request_timeout: Duration,
) -> CommandResult<reqwest::Response> {
    fetch(url, request_timeout, None, false).await
}

pub(crate) async fn get_public_image(
    url: Url,
    request_timeout: Duration,
    referer: Option<&Url>,
) -> CommandResult<reqwest::Response> {
    fetch_response(
        url,
        request_timeout,
        None,
        None,
        referer,
        RequestKind::Image,
    )
    .await
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
        player_page,
    )
    .await
}
pub(crate) async fn fetch_media(
    url: Url,
    request_timeout: Duration,
    player_page: Option<&Url>,
    range: Option<&str>,
    referer: Option<&Url>,
) -> CommandResult<reqwest::Response> {
    fetch_response(
        url,
        request_timeout,
        player_page,
        range,
        referer,
        RequestKind::Get,
    )
    .await
}

pub(crate) async fn relay_media(
    url: Url,
    player_page: &Url,
    range: Option<&str>,
) -> CommandResult<reqwest::Response> {
    let duration = Duration::from_secs(30);
    timeout(
        duration,
        fetch_response(
            url,
            duration,
            Some(player_page),
            range,
            Some(player_page),
            RequestKind::Relay,
        ),
    )
    .await
    .map_err(|_| "La fuente tardó demasiado en responder.")?
}

async fn fetch_response(
    mut url: Url,
    request_timeout: Duration,
    player_page: Option<&Url>,
    range: Option<&str>,
    referer: Option<&Url>,
    kind: RequestKind,
) -> CommandResult<reqwest::Response> {
    let streaming = kind == RequestKind::Relay;
    let client = PUBLIC_CLIENT.as_ref().map_err(|e| e.to_string())?;
    for _ in 0..6 {
        if !web_url(&url) {
            return Err("Ingresá una dirección pública HTTP o HTTPS.".into());
        }
        if player_page.is_some_and(|page| {
            let kind = if streaming { "media" } else { "document" };
            crate::ad_filter::blocked(url.as_str(), page.as_str(), kind, "GET")
        }) {
            return Err("El bloqueador identificó esta dirección como publicidad.".into());
        }
        let mut request = match kind {
            RequestKind::LoadX => client
                .post(url.clone())
                .header(header::ACCEPT, "application/json, */*;q=0.5")
                .header("X-Requested-With", "XMLHttpRequest"),
            RequestKind::Page => client
                .get(url.clone())
                .header(header::ACCEPT, browser_http::PAGE_ACCEPT),
            RequestKind::Image => client.get(url.clone()).header(
                header::ACCEPT,
                "image/avif,image/webp,image/apng,image/*,*/*;q=0.8",
            ),
            RequestKind::UpnShare => client
                .get(url.clone())
                .header(header::ACCEPT, "application/json"),
            RequestKind::Get | RequestKind::Relay => client.get(url.clone()),
        };
        if !streaming {
            request = request.timeout(request_timeout);
        }
        if let Some(range) = range {
            request = request.header(header::RANGE, range);
        }
        if let Some(page) = referer {
            let origin = page.origin().ascii_serialization();
            let reference = if player_page.is_some() || page.origin() == url.origin() {
                let mut page = page.clone();
                page.set_fragment(None);
                page.to_string()
            } else {
                format!("{origin}/")
            };
            if page.scheme() != "https" || url.scheme() == "https" {
                request = request
                    .header(header::REFERER, reference)
                    .header(header::ORIGIN, origin);
            }
        }
        let response = request
            .send()
            .await
            .map_err(|_| "No se pudo conectar con la fuente.")?;
        if response.status().is_redirection() {
            if kind == RequestKind::LoadX {
                return Err("El servidor de video cambió de dirección.".into());
            }
            let location = response
                .headers()
                .get(header::LOCATION)
                .and_then(|v| v.to_str().ok())
                .ok_or("Redirección inválida.")?;
            url = url
                .join(location)
                .map_err(|_| "Dirección de destino inválida.")?;
        } else if response.status().is_success()
            || kind == RequestKind::UpnShare && matches!(response.status().as_u16(), 404 | 410)
        {
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

#[derive(Clone, Debug, Serialize)]
pub struct Stream {
    #[serde(skip_serializing_if = "Option::is_none")]
    referer: Option<String>,
    url: String,
    hls: bool,
    dash: bool,
    tracks: Vec<Subtitle>,
}

#[derive(Clone, Debug, Serialize)]
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
    if umd_providers::streamtape_player_page(&url)
        || matches!(
            mime.as_str(),
            "text/html" | "application/xhtml+xml" | "application/json"
        )
    {
        return None;
    }
    let hls = mime.contains("mpegurl") || path.ends_with(".m3u8");
    let dash = mime == "application/dash+xml" || path.ends_with(".mpd");
    (hls || dash
        || mime.starts_with("video/")
        || mime.starts_with("audio/")
        || [
            ".mp4", ".webm", ".m4v", ".mkv", ".mp3", ".ogg", ".aac", ".ts",
        ]
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
    if let Some(found) = declared::player(base, doc) {
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
        for link in doc.select("a[href]").iter() {
            let Some(url) = link
                .attr("href")
                .and_then(|v| declared::address(base, &v, "media"))
            else {
                continue;
            };
            let is_download = url
                .host_str()
                .and_then(|h| h.strip_suffix(".mediafire.com"))
                .and_then(|h| h.strip_prefix("download"))
                .is_some_and(|tail| tail.bytes().all(|b| b.is_ascii_digit()));
            if is_download
                || link
                    .attr("id")
                    .is_some_and(|id| id.as_ref() == "downloadButton")
            {
                if let Some(found) = media(url, "") {
                    return Some(found);
                }
            }
        }
    }
    hosters::extract(base, doc).or_else(|| declared::metadata(base, doc))
}

enum Page {
    Media(Stream),
    Html(Url, String),
    Redirect(Url, Url),
    ProviderUnavailable(&'static str),
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
        let protected = text
            .lines()
            .map(str::trim)
            .filter_map(|line| {
                line.strip_prefix("#EXT-X-KEY:")
                    .or_else(|| line.strip_prefix("#EXT-X-SESSION-KEY:"))
            })
            .any(|attrs| {
                attrs.split(',').any(|attr| {
                    attr.trim()
                        .strip_prefix("METHOD=")
                        .is_some_and(|value| !matches!(value, "NONE" | "AES-128"))
                        || attr
                            .trim()
                            .strip_prefix("KEYFORMAT=")
                            .is_some_and(|value| value.trim_matches('"') != "identity")
                })
            });
        if protected {
            return Err("Este video necesita el reproductor del proveedor.".into());
        }
        let mut child = false;
        let mut initialization = None;
        let mut entry = None;
        for line in text.lines().map(str::trim) {
            if line.starts_with("#EXT-X-STREAM-INF:") {
                child = true;
            }
            if let Some((_, uri)) = line
                .strip_prefix("#EXT-X-MAP:")
                .and_then(|s| s.split_once("URI=\""))
            {
                initialization = uri.split('"').next();
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
        if let Some(initialization) = initialization {
            let initialization = base
                .join(initialization)
                .ok()
                .filter(web_url)
                .ok_or("Zilla publicó un enlace de inicialización inválido.")?;
            check_zilla_bytes(
                get(initialization, true).await?,
                "El archivo de inicialización de Zilla no contiene video.",
            )
            .await?;
        }
        check_zilla_bytes(
            get(url, true).await?,
            "El segmento de Zilla no contiene video.",
        )
        .await?;
        return Ok(Stream {
            referer: None,
            url: playlist.into(),
            hls: true,
            dash: false,
            tracks: Vec::new(),
        });
    }
    Err("El servidor de video no publicó segmentos reproducibles.".into())
}

async fn check_zilla_bytes(mut response: reqwest::Response, error: &str) -> CommandResult<()> {
    let mime = response
        .headers()
        .get(header::CONTENT_TYPE)
        .and_then(|v| v.to_str().ok())
        .unwrap_or("")
        .to_ascii_lowercase();
    if mime.contains("text/html")
        || mime.contains("json")
        || response
            .chunk()
            .await
            .map_err(|_| "Zilla interrumpió la descarga.")?
            .is_none_or(|bytes| bytes.is_empty())
    {
        return Err(error.to_owned());
    }
    Ok(())
}

pub(crate) async fn probe_public_media(mut address: Url, page: &Url) -> CommandResult<Stream> {
    let failure = "Este video necesita el reproductor del proveedor.";
    address.set_fragment(None);
    let request = |target, segment: bool| async move {
        let range = segment.then_some("bytes=0-31");
        let response = fetch_media(
            target,
            Duration::from_secs(8),
            Some(page),
            range,
            Some(page),
        )
        .await?;
        if response.url().scheme() != "https" {
            return Err(failure.to_owned());
        }
        Ok(response)
    };
    let mut stream = if address.path().to_ascii_lowercase().ends_with(".m3u8") {
        zilla_stream(address, request).await?
    } else {
        let response = request(address.clone(), true).await?;
        let mime = response
            .headers()
            .get(header::CONTENT_TYPE)
            .and_then(|v| v.to_str().ok())
            .unwrap_or("")
            .to_ascii_lowercase();
        if mime.contains("mpegurl") {
            zilla_stream(address, request).await?
        } else if mime.starts_with("application/dash+xml") {
            let manifest = bounded_text(request(address, false).await?, 2 << 20).await?;
            if manifest.to_ascii_lowercase().contains("contentprotection") {
                return Err(failure.into());
            }
            media(response.url().clone(), &mime).ok_or(failure)?
        } else {
            if !mime.starts_with("video/") && !mime.starts_with("audio/") {
                return Err(failure.into());
            }
            let final_address = response.url().clone();
            check_zilla_bytes(response, failure).await?;
            media(final_address, &mime).ok_or(failure)?
        }
    };
    stream.referer = Some(page.to_string());
    Ok(stream)
}

enum UpnShare {
    Player(Url),
    Unavailable,
}

async fn upnshare_player(url: &Url, endpoint: Url) -> CommandResult<UpnShare> {
    let response = fetch_response(
        endpoint,
        Duration::from_secs(10),
        Some(url),
        None,
        Some(url),
        RequestKind::UpnShare,
    )
    .await?;
    if matches!(response.status().as_u16(), 404 | 410) {
        return Ok(UpnShare::Unavailable);
    }
    let json: Value = serde_json::from_str(&bounded_text(response, 64 << 10).await?)
        .map_err(|_| "UPNShare no devolvió una fuente válida.")?;
    let target = json["url"]
        .as_str()
        .and_then(|address| declared::address(url, address, "subdocument"))
        .filter(|target| target.scheme() == "https")
        .ok_or("UPNShare no devolvió una fuente válida.")?;
    {
        let mut addresses = lookup_host((
            target.host_str().unwrap(),
            target.port_or_known_default().unwrap(),
        ))
        .await
        .map_err(|_| "No se pudo localizar el servidor de UPNShare.")?;
        if !addresses.next().is_some_and(|a| public_ip(a.ip()))
            || addresses.any(|a| !public_ip(a.ip()))
        {
            return Err("La dirección de UPNShare no es pública.".into());
        }
    }
    Ok(UpnShare::Player(target))
}

async fn load_page(url: Url, parent: Url) -> CommandResult<Page> {
    if let Some(endpoint) = hosters::upnshare_endpoint(&url) {
        return match upnshare_player(&url, endpoint).await? {
            UpnShare::Player(target) => Ok(Page::Redirect(url, target)),
            UpnShare::Unavailable => Ok(Page::ProviderUnavailable(
                "UPNShare ya no encuentra este video. Elegí otro servidor.",
            )),
        };
    }
    if let Some(address) = pixeldrain::address(&url) {
        let referer = (parent != url).then_some(&parent);
        return pixeldrain::resolve(address, referer).await.map(Page::Media);
    }
    if let Some(endpoint) = hosters::protocols::loadx_endpoint(&url) {
        if let Some(mut stream) = loadx(&url, endpoint).await {
            stream.referer = Some(url.to_string());
            return Ok(Page::Media(stream));
        }
    }
    if let Some(playlist) = zilla_playlist(&url) {
        let mut stream = zilla_stream(playlist, |target, probe| {
            fetch(target, Duration::from_secs(10), Some(&url), probe)
        })
        .await?;
        stream.referer = Some(url.to_string());
        return Ok(Page::Media(stream));
    }
    let response = fetch_response(
        url,
        Duration::from_secs(20),
        Some(&parent),
        None,
        Some(&parent),
        RequestKind::Page,
    )
    .await?;
    let base = response.url().clone();
    let mime = response
        .headers()
        .get(header::CONTENT_TYPE)
        .and_then(|v| v.to_str().ok())
        .unwrap_or("");
    if let Some(mut stream) = media(base.clone(), mime) {
        if parent != base {
            stream.referer = Some(parent.to_string());
        }
        return Ok(Page::Media(stream));
    }
    let body = bounded_text(response, 2 * 1024 * 1024).await?;
    if let Some((ticket, token)) = hosters::protocols::dood_request(&base, &body) {
        let response = fetch(ticket, Duration::from_secs(10), Some(&base), false).await?;
        let source = bounded_text(response, 4096).await?;
        if let Some(mut stream) = hosters::protocols::dood_media(&base, &source, &token) {
            stream.referer = Some(base.to_string());
            return Ok(Page::Media(stream));
        }
    }
    Ok(Page::Html(base, body))
}

async fn loadx(page: &Url, endpoint: Url) -> Option<Stream> {
    let response = fetch_response(
        endpoint,
        Duration::from_secs(12),
        Some(page),
        None,
        Some(page),
        RequestKind::LoadX,
    )
    .await
    .ok()?;
    let body = bounded_text(response, 1024 * 1024).await.ok()?;
    let data: Value = serde_json::from_str(&body).ok()?;
    declared::stream(page, data["videoSource"].as_str()?, "")
}

async fn resolve_pages(
    mut url: Url,
    referer: Option<Url>,
    browser: &mut Option<Url>,
    load: impl AsyncFn(Url, Url) -> CommandResult<Page>,
) -> CommandResult<Stream> {
    let mut parent = referer.unwrap_or_else(|| url.clone());
    parent.set_fragment(None);
    let mut visited = std::collections::HashSet::new();
    for _ in 0..3 {
        if hosters::upnshare_endpoint(&url).is_none() {
            url.set_fragment(None);
        }
        if !web_url(&url) || !visited.insert(url.clone()) {
            break;
        }
        if crate::ad_filter::blocked(url.as_str(), parent.as_str(), "media", "GET") {
            return Err("El bloqueador identificó esta dirección como publicidad.".into());
        }
        if let Some(mut direct) = media(url.clone(), "") {
            if url != parent {
                direct.referer = Some(parent.to_string());
            }
            return Ok(direct);
        }
        if matches_host(&url, hosters::protocols::NUPLOAD)
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
            Page::ProviderUnavailable(message) => {
                *browser = None;
                return Err(message.into());
            }
            Page::Redirect(mut base, next) => {
                let from_upnshare = hosters::upnshare_endpoint(&base).is_some();
                base.set_fragment(None);
                if browser.is_some()
                    && (hosters::alternate_player(&next)
                        || from_upnshare && next.scheme() == "https" && web_url(&next))
                {
                    *browser = Some(next.clone());
                }
                url = next;
                parent = base;
            }
            Page::Html(mut base, html) => {
                base.set_fragment(None);
                visited.insert(base.clone());
                if let Some(next) = hosters::protocols::frembed(&base, &html) {
                    parent = base;
                    url = next;
                    continue;
                }
                let doc = Document::from(html.as_str());
                if let Some(mut stream) = extract(&base, &doc) {
                    stream.referer = Some(base.to_string());
                    return Ok(stream);
                }
                let Some(next) =
                    hosters::redirect(&base, &doc).or_else(|| declared::embed(&base, &doc))
                else {
                    break;
                };
                if browser.is_some() && hosters::approved_delegate(&base, &next) {
                    *browser = Some(next.clone());
                }
                parent = base;
                url = next;
            }
        }
    }
    Err("La página no publica una fuente compatible con el reproductor.".into())
}

pub(crate) async fn provider_web_address(url: Url) -> CommandResult<Url> {
    if let Some(endpoint) = hosters::upnshare_endpoint(&url) {
        if let Ok(resolved) =
            timeout(Duration::from_secs(12), upnshare_player(&url, endpoint)).await
        {
            return match resolved {
                Ok(UpnShare::Player(target)) => Ok(target),
                Ok(UpnShare::Unavailable) => {
                    Err("UPNShare ya no encuentra este video. Elegí otro servidor.".into())
                }
                Err(_) => Ok(url),
            };
        }
    }
    Ok(hosters::mega_embed(&url).unwrap_or(url))
}

#[derive(Serialize)]
#[serde(untagged)]
pub enum VideoResolution {
    Direct(Stream),
    Web {
        #[serde(rename = "providerPlayer")]
        provider_player: String,
    },
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
    provider: Option<String>,
    web_fallback: Option<bool>,
    store: tauri::State<'_, umd_core::CatalogStore>,
    filter: tauri::State<'_, crate::ad_filter::Directory>,
) -> CommandResult<VideoResolution> {
    let url = Url::parse(address.trim()).map_err(|_| "Enlace de video inválido.")?;
    if trusted_server(&store, &url)? {
        if let Some(stream) = direct_media(url.clone(), "") {
            return Ok(VideoResolution::Direct(stream));
        }
    }
    let mut browser =
        (web_fallback == Some(true) && hosters::alternate_player(&url)).then(|| url.clone());
    if let Some(embed) = browser.as_ref().and_then(hosters::mega_embed) {
        return Ok(VideoResolution::Web {
            provider_player: embed.into(),
        });
    }
    let _permit = REQUESTS
        .try_acquire()
        .map_err(|_| "Hay otras fuentes cargando. Volvé a intentar.")?;
    crate::ad_filter::prepare_player(&filter).await;
    let referer = provider
        .as_deref()
        .and_then(crate::providers::provider_referer)
        .filter(web_url);
    let resolution = timeout(
        Duration::from_secs(35),
        resolve_pages(url, referer, &mut browser, load_page),
    )
    .await
    .map_err(|_| "La fuente tardó demasiado en responder.".to_owned())
    .and_then(|result| result);
    match (resolution, browser) {
        (Ok(stream), _) => Ok(VideoResolution::Direct(stream)),
        (Err(_), Some(url)) => Ok(VideoResolution::Web {
            provider_player: url.into(),
        }),
        (Err(error), None) => Err(error),
    }
}

#[derive(Serialize)]
pub struct LiveVariant {
    label: String,
    #[serde(flatten)]
    stream: Stream,
}

fn live_server_links(page: &Url, html: &str) -> Vec<(String, Url)> {
    let doc = Document::from(html);
    let mut seen = std::collections::HashSet::new();
    doc.select("a[href],button[data-src],button[data-url],[data-src],[data-url]")
        .iter()
        .filter_map(|node| {
            let label = node.text().split_whitespace().collect::<Vec<_>>().join(" ");
            let normalized = umd_core::normalize(&label);
            if label.len() > 80
                || !["opcion", "servidor", "fhd", "server"]
                    .iter()
                    .any(|term| normalized.contains(term))
            {
                return None;
            }
            let address = ["href", "data-src", "data-url"]
                .into_iter()
                .filter_map(|attr| node.attr(attr))
                .find_map(|raw| declared::address(page, &raw, "subdocument"))?;
            (address != *page && seen.insert(address.clone())).then_some((label, address))
        })
        .take(6)
        .collect()
}

fn live_hls_qualities(base: &Url, text: &str) -> Vec<(u32, Url)> {
    let text = text.trim_start_matches('\u{feff}');
    if !text.starts_with("#EXTM3U") {
        return Vec::new();
    }
    let mut resolution = None;
    let mut variants = Vec::new();
    for line in text.lines().map(str::trim) {
        if let Some(info) = line.strip_prefix("#EXT-X-STREAM-INF:") {
            // The master is still needed for playlists that reference separate audio or subtitle groups.
            resolution = if info.contains("AUDIO=") || info.contains("SUBTITLES=") {
                None
            } else {
                info.split(',').find_map(|part| {
                    part.strip_prefix("RESOLUTION=")
                        .and_then(|value| value.split_once('x'))
                        .and_then(|(_, height)| height.parse::<u32>().ok())
                })
            };
        } else if !line.is_empty() && !line.starts_with('#') {
            if let Some((height, address)) = resolution
                .take()
                .and_then(|height| Some((height, declared::address(base, line, "media")?)))
            {
                variants.push((height, address));
                if variants.len() >= 8 {
                    break;
                }
            }
        }
    }
    variants.sort_by_key(|(height, _)| std::cmp::Reverse(*height));
    variants
}

#[tauri::command]
pub async fn live_variants(
    address: String,
    filter: tauri::State<'_, crate::ad_filter::Directory>,
) -> CommandResult<Vec<LiveVariant>> {
    use futures_util::{stream, StreamExt};
    let url = Url::parse(address.trim())
        .ok()
        .filter(web_url)
        .ok_or("Enlace de video inválido.")?;
    let _permit = REQUESTS
        .try_acquire()
        .map_err(|_| "Hay otras fuentes cargando. Volvé a intentar.")?;
    crate::ad_filter::prepare_player(&filter).await;
    timeout(Duration::from_secs(40), async {
        let mut direct = None;
        let (page, mut links) = if let Some(stream) = media(url.clone(), "") {
            direct = Some(stream);
            (url.clone(), vec![])
        } else {
            match load_page(url.clone(), url.clone()).await? {
                Page::Html(page, body) => {
                    let docs = Document::from(body.as_str());
                    direct = extract(&page, &docs).map(|mut stream| {
                        stream.referer = Some(page.to_string());
                        stream
                    });
                    let links = live_server_links(&page, &body);
                    (page, links)
                }
                Page::Media(stream) => {
                    direct = Some(stream);
                    (url.clone(), vec![])
                }
                Page::Redirect(page, next) => (page, vec![("".into(), next)]),
                Page::ProviderUnavailable(message) => return Err(message.into()),
            }
        };
        if direct.is_none() && links.is_empty() {
            links.push(("".into(), page.clone()));
        }
        let mut results = direct
            .into_iter()
            .map(|stream| ("".into(), stream))
            .collect::<Vec<_>>();
        let resolved = stream::iter(links)
            .map(|(label, link)| {
                let page = page.clone();
                async move {
                    resolve_pages(link, Some(page), &mut None, load_page)
                        .await
                        .ok()
                        .map(|stream| (label, stream))
                }
            })
            .buffered(2)
            .collect::<Vec<_>>()
            .await;
        results.extend(resolved.into_iter().flatten());
        let variants = stream::iter(results)
            .map(|(label, stream)| async move {
                let mut alternatives = vec![];
                if stream.hls {
                    if let Ok(url) = Url::parse(&stream.url) {
                        let referer = stream
                            .referer
                            .as_deref()
                            .and_then(|raw| Url::parse(raw).ok());
                        if let Ok(response) = fetch_media(
                            url,
                            Duration::from_secs(8),
                            referer.as_ref(),
                            None,
                            referer.as_ref(),
                        )
                        .await
                        {
                            let base = response.url().clone();
                            if let Ok(text) = bounded_text(response, 1 << 20).await {
                                alternatives = live_hls_qualities(&base, &text)
                                    .into_iter()
                                    .map(|(height, address)| {
                                        let mut stream = stream.clone();
                                        stream.url = address.into();
                                        let label = format!("{label} {height}p").trim().to_owned();
                                        LiveVariant { label, stream }
                                    })
                                    .collect();
                            }
                        }
                    }
                }
                (LiveVariant { label, stream }, alternatives)
            })
            .buffered(2)
            .collect::<Vec<_>>()
            .await;
        let mut seen = std::collections::HashSet::new();
        let result: Vec<_> = variants
            .into_iter()
            .flat_map(|(initial, alternatives)| std::iter::once(initial).chain(alternatives))
            .filter(|item| seen.insert(item.stream.url.clone()))
            .take(24)
            .collect();
        if result.is_empty() {
            Err("El canal no ofreció una señal compatible.".into())
        } else {
            Ok(result)
        }
    })
    .await
    .map_err(|_| "Se agotó el tiempo al cargar las señales del canal.")?
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
