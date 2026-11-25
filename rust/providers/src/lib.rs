mod av1;
pub mod browser_http;
mod doramas;
mod doramas_public;
mod jk;
mod manga;
mod manhwa;
pub use manga::Manga;
mod cinema;
pub use cinema::Cinema;
mod framex;
mod indexed;
use chrono::DateTime;
use reqwest::{cookie::Jar, Client, Url};
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};
use std::{collections::HashSet, sync::Arc, time::Duration};
use tokio::time::{sleep_until, Instant};
use umd_core::CatalogItem;

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Site {
    Jk,
    Doramas,
    Manhwa,
    Cinema(Cinema),
    AnimeAv1,
    Manga(Manga),
    Olympus,
    AnimeTmo,
    Framex,
}
impl Site {
    pub fn parse(id: &str) -> Option<Self> {
        match id {
            "jkanime" => Some(Self::Jk),
            "doramasflix" => Some(Self::Doramas),
            "manhwaweb" => Some(Self::Manhwa),
            "olympusxyz" => Some(Self::Olympus),
            "animetmo" => Some(Self::AnimeTmo),
            "framextv" => Some(Self::Framex),
            "pelisplus" => Some(Self::Cinema(Cinema::PelisPlus)),
            "pelisplus-to" => Some(Self::Cinema(Cinema::TioPlus)),
            "pelisflix-hd" => Some(Self::Cinema(Cinema::Pelisflix)),
            "poseidon-hd2" => Some(Self::Cinema(Cinema::Poseidon)),
            "seriesflix" => Some(Self::Cinema(Cinema::Seriesflix)),
            "animeav1" => Some(Site::AnimeAv1),
            "leercapitulo" => Some(Site::Manga(Manga::Leer)),
            "zonatmo" => Some(Site::Manga(Manga::Zona)),
            "uchuujinmangas" => Some(Site::Manga(Manga::Uchuujin)),
            _ => None,
        }
    }
    pub fn base(self) -> &'static str {
        match self {
            Self::Manga(site) => site.base(),
            Self::AnimeAv1 => "https://animeav1.com/",
            Self::Cinema(site) => site.base(),
            Self::Jk => "https://jkanime.net/",
            Self::Manhwa => "https://manhwaweb.com/",
            Self::Doramas => "https://doramasflix.in/",
            Self::Olympus => "https://olympusxyz.com/",
            Self::AnimeTmo => "https://animetmo.com/",
            Self::Framex => "https://api.framextv.tech/",
        }
    }
}
#[derive(Debug)]
pub struct Failure {
    pub kind: &'static str,
    pub retry: i64,
    pub status: Option<u16>,
}
impl Failure {
    fn new(kind: &'static str) -> Self {
        Self {
            kind,
            retry: 0,
            status: None,
        }
    }
}
pub type Result<T> = std::result::Result<T, Failure>;
fn invalid() -> Failure {
    Failure::new("parse_or_storage_error")
}
#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Entry {
    pub external_id: String,
    pub preview: Value,
}
#[derive(Serialize)]
pub struct Page {
    pub items: Vec<Entry>,
    pub next: Option<String>,
}
impl Page {
    fn directory(items: Vec<Entry>, next: Option<String>) -> Result<Self> {
        if items.is_empty() {
            return Err(invalid());
        }
        Ok(Self { items, next })
    }
}
pub type PublicReader = std::sync::Arc<
    dyn Fn(
            Url,
            usize,
        ) -> std::pin::Pin<Box<dyn std::future::Future<Output = Option<Vec<u8>>> + Send>>
        + Send
        + Sync,
>;
pub struct Provider {
    interactive: Option<PublicReader>,
    site: Site,
    base: Url,
    client: Client,
    cookies: Option<Arc<Jar>>,
    interval: Duration,
    pacing: std::sync::Arc<tokio::sync::Mutex<Pacing>>,
    batched: bool,
    index: Option<Vec<indexed::CompactEntry>>,
    api: Option<(Url, Client)>,
    csrf: Option<String>,
    doramas_dirs: std::collections::BTreeMap<String, Vec<Entry>>,
    doramas_eps: Option<doramas_public::Episodes>,
    doramas_batches: bool,
    doramas_routes: Option<doramas::EpisodeRoutes>,
    olympus_routes: Option<indexed::OlympusChapters>,
    leer_decoder: Option<(Url, Instant, (String, String))>,
}

struct Pacing {
    next: Instant,
    cooldown: Option<(Instant, Option<u16>)>,
}
impl Provider {
    pub fn set_public_reader(&mut self, reader: PublicReader) {
        self.interactive = Some(reader);
    }
    pub fn new(site: Site, base: Url, pinned_client: Client, interval: Duration) -> Self {
        Self {
            interactive: None,
            site,
            base,
            client: pinned_client,
            cookies: None,
            interval,
            pacing: std::sync::Arc::new(tokio::sync::Mutex::new(Pacing {
                next: Instant::now(),
                cooldown: None,
            })),
            batched: false,
            index: None,
            api: None,
            csrf: None,
            doramas_dirs: Default::default(),
            doramas_eps: None,
            doramas_batches: true,
            doramas_routes: None,
            olympus_routes: None,
            leer_decoder: None,
        }
    }
    pub async fn open(site: Site) -> Result<Self> {
        Self::open_with_interval(site, Duration::from_millis(200)).await
    }
    pub async fn open_at(site: Site, base: Url) -> Result<Self> {
        Self::open_base(site, base, Duration::from_millis(200), None).await
    }
    pub async fn open_with_interval(site: Site, interval: Duration) -> Result<Self> {
        Self::open_default(site, interval, None).await
    }
    pub async fn open_with_cookies(
        site: Site,
        base: Option<Url>,
        cookies: Arc<Jar>,
    ) -> Result<Self> {
        let interval = Duration::from_millis(200);
        match base {
            Some(base) => Self::open_base(site, base, interval, Some(cookies)).await,
            None => Self::open_default(site, interval, Some(cookies)).await,
        }
    }
    async fn open_base(
        site: Site,
        base: Url,
        interval: Duration,
        cookies: Option<Arc<Jar>>,
    ) -> Result<Self> {
        let client = pinned(&base, cookies.as_ref()).await?;
        let mut provider = Self::new(site, base, client, interval);
        provider.cookies = cookies;
        Ok(provider)
    }
    async fn open_default(
        site: Site,
        interval: Duration,
        cookies: Option<Arc<Jar>>,
    ) -> Result<Self> {
        let urls: &[&str] = if site == Site::Framex {
            &[
                "https://api.framextv.tech/",
                "https://api.framextv.xyz/",
                "https://api.framextv.cc/",
            ]
        } else {
            &[site.base()]
        };
        for raw in urls {
            let base = Url::parse(raw).map_err(|_| invalid())?;
            if let Ok(provider) = Self::open_base(site, base, interval, cookies.clone()).await {
                return Ok(provider);
            }
        }
        Err(Failure::new("network"))
    }
    async fn bytes(&mut self, route: &str, accept: &str, limit: usize) -> Result<Vec<u8>> {
        Ok(self.bytes_with_url(route, accept, limit).await?.0)
    }

    async fn wait_request(&self) -> Result<()> {
        loop {
            let deadline = {
                let mut slot = self.pacing.lock().await;
                let now = Instant::now();
                if let Some((until, status)) = slot.cooldown.take() {
                    if until > now {
                        slot.cooldown = Some((until, status));
                        return Err(Failure {
                            kind: "throttled",
                            status,
                            retry: (until - now).as_secs().max(1) as i64,
                        });
                    }
                }
                if slot.next <= now {
                    slot.next = now + self.interval;
                    return Ok(());
                }
                slot.next
            };
            sleep_until(deadline).await;
        }
    }

    async fn report_backoff<T: Send>(&self, result: Result<T>) -> Result<T> {
        if let Err(error) = &result {
            if self.batched && error.kind == "throttled" && error.retry > 0 {
                let blocked = Instant::now() + Duration::from_secs(error.retry as u64);
                let mut slot = self.pacing.lock().await;
                if slot.cooldown.is_none_or(|(until, _)| until < blocked) {
                    slot.cooldown = Some((blocked, error.status));
                }
            }
        }
        result
    }

    async fn request_batch<T, R, F, Fut>(&self, jobs: Vec<T>, request: F) -> Result<Vec<R>>
    where
        T: Send + 'static,
        R: Send + 'static,
        F: Fn(Provider, T) -> Fut + Send + Sync + 'static,
        Fut: std::future::Future<Output = Result<R>> + Send + 'static,
    {
        use futures_util::{stream, StreamExt, TryStreamExt};
        stream::iter(jobs)
            .map(|job| {
                let mut connection = Self::new(
                    self.site,
                    self.base.clone(),
                    self.client.clone(),
                    self.interval,
                );
                connection.pacing = self.pacing.clone();
                connection.cookies = self.cookies.clone();
                connection.batched = true;
                connection.interactive = self.interactive.clone();
                connection.csrf = self.csrf.clone();
                connection.api = self.api.clone();
                request(connection, job)
            })
            .buffered(3)
            .try_collect()
            .await
    }
    async fn bytes_with_url(
        &mut self,
        route: &str,
        accept: &str,
        limit: usize,
    ) -> Result<(Vec<u8>, Url)> {
        let url = self.base.join(route).map_err(|_| invalid())?;
        if url.origin() != self.base.origin() {
            return Err(invalid());
        }
        let mut current = url;
        let mut active_client = self.client.clone();
        let mut response = None;
        for _ in 0..6 {
            self.wait_request().await?;
            let same_origin = current.origin() == self.base.origin();
            let site_origin = if self.site == Site::Framex
                && self
                    .base
                    .host_str()
                    .is_some_and(|h| h.starts_with("api.framextv."))
            {
                "https://framextv.xyz".into()
            } else {
                self.base.origin().ascii_serialization()
            };
            let mut request = active_client
                .get(current.clone())
                .header("accept", accept)
                .header("referer", format!("{site_origin}/"));
            // TioPlus serves directory pages as document navigations. Its server
            // rejects a synthetic Origin header on these GETs. Never send our base
            // Origin header to an unrelated cross-origin redirect target.
            if self.site != Site::Cinema(Cinema::TioPlus) && same_origin {
                request = request.header("origin", site_origin);
            }
            let res = request.send().await.map_err(|_| Failure::new("network"))?;
            if !res.status().is_redirection() {
                response = Some(res);
                break;
            }
            let status = res.status().as_u16();
            let next = res
                .headers()
                .get("location")
                .and_then(|v| v.to_str().ok())
                .and_then(|v| current.join(v).ok())
                .filter(|v| {
                    umd_core::public_url::web_url(v)
                        && (current.scheme() != "https" || v.scheme() == "https")
                })
                .ok_or_else(invalid)?;
            if current.origin() != next.origin() {
                let next_client = pinned(&next, self.cookies.as_ref()).await?;
                if is_permanent_domain_move(status, &current, &next, &self.base) {
                    self.client = next_client.clone();
                    self.csrf = None;
                    self.api = None;
                    self.base = next.join("/").map_err(|_| invalid())?;
                }
                active_client = next_client;
            }
            current = next;
        }
        let response = response.ok_or_else(invalid)?;
        let final_url = response.url().clone();
        if matches!(response.status().as_u16(), 401 | 403) {
            if let Some(reader) = &self.interactive {
                if let Some(bytes) = reader(final_url.clone(), limit).await {
                    return Ok((bytes, final_url));
                }
            }
        }
        Ok((
            self.report_backoff(bounded(response, limit).await).await?,
            final_url,
        ))
    }
    async fn send_json(&mut self, req: reqwest::RequestBuilder, limit: usize) -> Result<Value> {
        let is_get = req
            .try_clone()
            .and_then(|r| r.build().ok())
            .is_some_and(|r| r.method() == reqwest::Method::GET);
        self.wait_request().await?;
        let res = req
            .header("accept", "application/json")
            .send()
            .await
            .map_err(|_| Failure::new("network"))?;
        if is_get && matches!(res.status().as_u16(), 401 | 403) {
            if let Some(reader) = &self.interactive {
                if let Some(bytes) = reader(res.url().clone(), limit).await {
                    return serde_json::from_slice(&bytes).map_err(|_| invalid());
                }
            }
        }
        serde_json::from_slice(&self.report_backoff(bounded(res, limit).await).await?)
            .map_err(|_| invalid())
    }
    async fn json(&mut self, route: &str, mb: usize) -> Result<Value> {
        serde_json::from_slice(&self.bytes(route, "application/json", mb << 20).await?)
            .map_err(|_| invalid())
    }
    async fn html(&mut self, route: &str) -> Result<String> {
        Ok(self.html_with_url(route).await?.0)
    }
    async fn html_with_url(&mut self, route: &str) -> Result<(String, Url)> {
        let (bytes, url) = self
            .bytes_with_url(route, browser_http::PAGE_ACCEPT, 8 << 20)
            .await?;
        let text = String::from_utf8(bytes).map_err(|_| invalid())?;
        Ok((text, url))
    }
    pub async fn page(&mut self, cursor: Option<&str>) -> Result<Page> {
        match self.site {
            Site::Jk => self.jk_page(page_number(cursor)?).await,
            Site::Doramas => self.doramas_page(cursor).await,
            Site::Manhwa => {
                self.manhwa_page(
                    cursor
                        .unwrap_or("0")
                        .parse::<u32>()
                        .ok()
                        .filter(|n| *n <= 100000)
                        .ok_or_else(invalid)?,
                    None,
                )
                .await
            }
            Site::AnimeAv1 => self.av1_page(page_number(cursor)?, None).await,
            Site::Manga(site) => self.manga_page(site, page_number(cursor)?, None).await,
            Site::Cinema(site) => self.cinema_page(site, cursor).await,
            Site::Framex => self.framex_page(cursor).await,
            _ => self.index_page(cursor).await,
        }
    }
    pub async fn updates(&mut self) -> Result<Vec<Entry>> {
        match self.site {
            Site::Jk => self.jk_updates().await,
            Site::Doramas => self.doramas_updates().await,
            Site::Manhwa => self.manhwa_updates().await,
            Site::AnimeAv1 => self.av1_updates().await,
            Site::Manga(site) => self.manga_updates(site).await,
            Site::Cinema(site) => self.cinema_updates(site).await,
            Site::Framex => self.framex_updates().await,
            Site::Olympus => self.olympus_updates().await,
            Site::AnimeTmo => Ok(self
                .index()
                .await?
                .iter()
                .take(24)
                .map(indexed::CompactEntry::entry)
                .collect()),
        }
    }
    pub async fn detail(&mut self, e: &Entry) -> Result<CatalogItem> {
        let value = match self.site {
            Site::Jk => self.jk_detail(e).await?,
            Site::Doramas => self.doramas_detail(e).await?,
            Site::Manhwa => self.manhwa_detail(e).await?,
            Site::AnimeAv1 => self.av1_detail(e).await?,
            Site::Manga(site) => self.manga_detail(site, e).await?,
            Site::Cinema(site) => self.cinema_detail(site, e).await?,
            Site::Framex => self.framex_detail(e).await?,
            Site::AnimeTmo => self.anime_detail(e).await?,
            Site::Olympus => self.olympus_detail(e).await?,
        };
        let item: CatalogItem = serde_json::from_value(value).map_err(|_| invalid())?;
        item.validate().map_err(|_| invalid())?;
        Ok(item)
    }
    pub async fn search(&mut self, query: &str, page: u32) -> Result<Page> {
        let trimmed = query.trim();
        let q = umd_core::normalize(trimmed);
        if q.is_empty() || q.len() > 512 || !(1..=100000).contains(&page) {
            return Err(invalid());
        }
        match self.site {
            Site::Jk => return self.jk_search(trimmed).await,
            Site::Doramas => return self.doramas_search(trimmed).await,
            Site::Manhwa => return self.manhwa_page(page - 1, Some(trimmed)).await,
            _ => {}
        }
        if let Site::Manga(site) = self.site {
            return self.manga_page(site, page, Some(trimmed)).await;
        }
        if self.site == Site::AnimeAv1 {
            return self.av1_page(page, Some(trimmed)).await;
        }
        if let Site::Cinema(site) = self.site {
            return self.cinema_search(site, trimmed).await;
        }
        if self.site == Site::Framex {
            return self.framex_search(trimmed, page).await;
        }
        let mut hits = self
            .index()
            .await?
            .iter()
            .filter(|e| e.normalized.contains(&q))
            .skip((page as usize - 1) * 50);
        let items = hits
            .by_ref()
            .take(50)
            .map(indexed::CompactEntry::entry)
            .collect();
        Ok(Page {
            items,
            next: hits.next().map(|_| (page + 1).to_string()),
        })
    }
}
fn is_permanent_domain_move(status: u16, current: &Url, next: &Url, base: &Url) -> bool {
    matches!(status, 301 | 308)
        && next.scheme() == "https"
        && current.origin() == base.origin()
        && current.origin() != next.origin()
        && current.path() == next.path()
        && current.query() == next.query()
}
fn pinned_host_port(root: &Url) -> Result<(&str, u16)> {
    if !umd_core::public_url::web_url(root) {
        return Err(invalid());
    }
    let host = root
        .host_str()
        .map(|h| h.trim_matches(['[', ']']))
        .filter(|h| !h.is_empty())
        .ok_or_else(invalid)?;
    let port = root.port_or_known_default().ok_or_else(invalid)?;
    Ok((host, port))
}
async fn pinned(root: &Url, cookies: Option<&Arc<Jar>>) -> Result<Client> {
    let (host, port) = pinned_host_port(root)?;
    let addrs: Vec<_> = tokio::net::lookup_host((host, port))
        .await
        .map_err(|_| Failure::new("network"))?
        .collect();
    if addrs.is_empty()
        || addrs
            .iter()
            .any(|addr| !umd_core::public_url::public_ip(addr.ip()))
    {
        return Err(Failure::new("network"));
    }
    let builder = browser_http::client_builder();
    let builder = match cookies {
        Some(cookies) => builder.cookie_provider(cookies.clone()),
        None => builder.cookie_store(true),
    };
    builder
        .no_proxy()
        .redirect(reqwest::redirect::Policy::none())
        .timeout(Duration::from_secs(90))
        .resolve_to_addrs(host, &addrs)
        .build()
        .map_err(|_| Failure::new("network"))
}
fn artwork_url(base: &Url, raw: Option<&str>) -> Option<String> {
    let raw = raw?.trim();
    if raw.is_empty() || raw.len() > 2048 {
        return None;
    }
    let mut url = base.join(raw).ok()?;
    if url.path() == "/_next/image" {
        if let Some((_, inner)) = url.query_pairs().find(|(k, _)| k == "url") {
            if let Ok(unwrapped) = base.join(&inner) {
                if unwrapped.scheme() == "https" && umd_core::public_url::web_url(&unwrapped) {
                    url = unwrapped;
                }
            }
        }
    }
    (url.scheme() == "https" && umd_core::public_url::web_url(&url)).then(|| url.to_string())
}
fn canonical_number(s: &str) -> String {
    let trimmed = s.trim();
    if trimmed.is_empty() {
        return String::new();
    }
    let normalized = trimmed.replace(',', ".");
    let mut parts = normalized.split('.');
    let int_part = parts.next().unwrap_or("");
    let frac_part = parts.next();
    if parts.next().is_none()
        && (!int_part.is_empty() || frac_part.is_some())
        && int_part.bytes().all(|b| b.is_ascii_digit())
        && frac_part.is_none_or(|f| !f.is_empty() && f.bytes().all(|b| b.is_ascii_digit()))
    {
        let int = int_part.trim_start_matches('0');
        let int = if int.is_empty() { "0" } else { int };
        let frac = frac_part.unwrap_or("").trim_end_matches('0');
        return if frac.is_empty() {
            int.to_string()
        } else {
            format!("{int}.{frac}")
        };
    }
    trimmed
        .split_whitespace()
        .collect::<Vec<_>>()
        .join(" ")
        .to_lowercase()
}
fn decode_base64(input: &str) -> Option<Vec<u8>> {
    let clean: Vec<u8> = input.bytes().filter(|b| !b.is_ascii_whitespace()).collect();
    if clean.is_empty() || clean.len() > (2 << 20) || clean.len() % 4 == 1 {
        return None;
    }
    let trimmed = clean
        .strip_suffix(b"==")
        .or_else(|| clean.strip_suffix(b"="))
        .unwrap_or(&clean);
    if trimmed.contains(&b'=') {
        return None;
    }
    let mut out = Vec::with_capacity((trimmed.len() * 3) / 4);
    let mut buf = 0u32;
    let mut bits = 0u32;
    for &b in trimmed {
        let val = match b {
            b'A'..=b'Z' => b - b'A',
            b'a'..=b'z' => b - b'a' + 26,
            b'0'..=b'9' => b - b'0' + 52,
            b'+' | b'-' => 62,
            b'/' | b'_' => 63,
            _ => return None,
        };
        buf = (buf << 6) | u32::from(val);
        bits += 6;
        if bits >= 8 {
            bits -= 8;
            out.push(((buf >> bits) & 0xff) as u8);
        }
    }
    if bits > 0 && (buf & ((1 << bits) - 1)) != 0 {
        return None;
    }
    Some(out)
}
fn decode_base64_utf8(input: &str) -> Option<String> {
    String::from_utf8(decode_base64(input)?).ok()
}
fn encode_base64(bytes: &[u8]) -> String {
    const TABLE: &[u8; 64] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    let mut out = String::with_capacity(bytes.len().div_ceil(3) * 4);
    for chunk in bytes.chunks(3) {
        let b0 = u32::from(chunk[0]);
        let b1 = u32::from(*chunk.get(1).unwrap_or(&0));
        let b2 = u32::from(*chunk.get(2).unwrap_or(&0));
        let triple = (b0 << 16) | (b1 << 8) | b2;
        out.push(TABLE[((triple >> 18) & 0x3f) as usize] as char);
        out.push(TABLE[((triple >> 12) & 0x3f) as usize] as char);
        if chunk.len() > 1 {
            out.push(TABLE[((triple >> 6) & 0x3f) as usize] as char);
        } else {
            out.push('=');
        }
        if chunk.len() > 2 {
            out.push(TABLE[(triple & 0x3f) as usize] as char);
        } else {
            out.push('=');
        }
    }
    out
}
fn string(v: &Value) -> Option<String> {
    match v {
        Value::String(s) if !s.trim().is_empty() => {
            Some(s.split_whitespace().collect::<Vec<_>>().join(" "))
        }
        Value::Number(n) => Some(n.to_string()),
        _ => None,
    }
}
fn integer(v: &Value) -> Option<i64> {
    v.as_i64().or_else(|| v.as_str()?.parse().ok())
}
fn rows(v: &Value) -> Result<&Vec<Value>> {
    v.as_array().ok_or_else(invalid)
}
fn segment(s: &str, max: usize) -> bool {
    !s.is_empty()
        && s.len() <= max
        && s.as_bytes()[0].is_ascii_alphanumeric()
        && s.bytes()
            .all(|b| b.is_ascii_alphanumeric() || matches!(b, b'-' | b'_'))
}
fn dedup(items: impl IntoIterator<Item = Entry>) -> Vec<Entry> {
    let mut seen = HashSet::new();
    items
        .into_iter()
        .filter(|e| seen.insert(e.external_id.clone()))
        .collect()
}
fn date(v: &Value) -> Option<&str> {
    let s = v.as_str()?.get(..10)?;
    chrono::NaiveDate::parse_from_str(s, "%Y-%m-%d")
        .ok()
        .map(|_| s)
}
fn episode(n: impl ToString, order: usize, name: Option<&str>) -> Value {
    json!({"kind":"episode","displayNumber":n.to_string(),"sortOrder":order,"name":name})
}

fn page_number(s: Option<&str>) -> Result<u32> {
    s.unwrap_or("1")
        .parse()
        .ok()
        .filter(|n| (1..=100000).contains(n))
        .ok_or_else(invalid)
}

async fn bounded(mut res: reqwest::Response, limit: usize) -> Result<Vec<u8>> {
    if !res.status().is_success() {
        let status = res.status().as_u16();
        let delay = res
            .headers()
            .get("retry-after")
            .and_then(|s| s.to_str().ok());
        let kind = match status {
            429 => "throttled",
            503 if delay.is_some() => "throttled",
            404 | 410 => "not_found",
            401 | 403 => "challenge_or_access_denied",
            _ => "network",
        };
        let retry = if kind == "throttled" {
            delay
                .and_then(|s| {
                    s.parse().ok().or_else(|| {
                        DateTime::parse_from_rfc2822(s)
                            .ok()
                            .map(|d| d.timestamp() - chrono::Utc::now().timestamp())
                    })
                })
                .unwrap_or(3600i64)
                .clamp(1, 86400)
        } else {
            0
        };
        return Err(Failure {
            kind,
            retry,
            status: Some(status),
        });
    }
    let mut body = Vec::new();
    while let Some(chunk) = res.chunk().await.map_err(|_| Failure::new("network"))? {
        if chunk.len() > limit - body.len() {
            return Err(invalid());
        }
        body.extend_from_slice(&chunk);
    }
    Ok(body)
}

fn site_player(url: Url) -> Value {
    json!({"url":url.as_str(),"external":true,"label":"Abrir reproductor del sitio"})
}

/// Some Streamtape player pages end in the uploaded filename, including .mp4.
pub fn streamtape_player_page(url: &Url) -> bool {
    url.domain().is_some_and(|domain| {
        ["streamtape.com", "streamtape.to"].iter().any(|host| {
            domain == *host
                || domain
                    .strip_suffix(host)
                    .is_some_and(|prefix| prefix.ends_with('.'))
        })
    }) && matches!(url.path().split('/').nth(1), Some("e" | "v" | "embed"))
}

#[cfg(test)]
mod tests {
    use super::*;
    use dom_query::Document;

    #[test]
    fn jk_keeps_streamtape_embed_filenames_as_player_pages() {
        let page = Url::parse("https://jkanime.net/example/1/").unwrap();
        let html = r#"<iframe src="https://streamtape.com/e/abc/episode.mp4"></iframe>
            <iframe src="https://video.example/episode.mp4"></iframe>"#;
        let streams = jk::jk_extract_streams(&page, html).unwrap();
        let source = |host: &str| {
            streams
                .iter()
                .find(|s| s["url"].as_str().is_some_and(|url| url.contains(host)))
                .unwrap()
        };
        assert_eq!(source("streamtape.com")["embed"], true);
        assert_eq!(source("video.example")["embed"], false);
    }

    #[test]
    fn canonical_number_normalizes_zeros_decimals_and_non_numbers() {
        assert_eq!(canonical_number("01"), "1");
        assert_eq!(canonical_number("1.0"), "1");
        assert_eq!(canonical_number("1.00"), "1");
        assert_eq!(canonical_number("012.50"), "12.5");
        assert_eq!(canonical_number("12,5"), "12.5");
        assert_eq!(canonical_number("0"), "0");
        assert_eq!(canonical_number("00.0"), "0");
        assert_eq!(canonical_number(" 007 "), "7");
        assert_eq!(canonical_number("inf"), "inf");
        assert_eq!(canonical_number("NaN"), "nan");
        assert_eq!(canonical_number("Extra  1"), "extra 1");
    }

    #[test]
    fn pinned_host_port_validates_web_url_and_strips_ipv6_brackets() {
        let ipv6 = Url::parse("https://[2606:4700:4700::1111]/").unwrap();
        assert_eq!(
            pinned_host_port(&ipv6).unwrap(),
            ("2606:4700:4700::1111", 443)
        );

        let custom_port = Url::parse("https://example.com:8443/path").unwrap();
        assert_eq!(
            pinned_host_port(&custom_port).unwrap(),
            ("example.com", 8443)
        );

        // Reject invalid schemes, credentials, loopback, and local domains
        assert!(pinned_host_port(&Url::parse("ftp://example.com/").unwrap()).is_err());
        assert!(pinned_host_port(&Url::parse("https://user:pass@example.com/").unwrap()).is_err());
        assert!(pinned_host_port(&Url::parse("https://[::1]/").unwrap()).is_err());
        assert!(pinned_host_port(&Url::parse("https://127.0.0.1/").unwrap()).is_err());
        assert!(pinned_host_port(&Url::parse("https://app.localhost/").unwrap()).is_err());
    }

    #[test]
    fn permanent_domain_move_only_accepts_301_308_https_same_path_and_query() {
        let base = Url::parse("https://old.example/").unwrap();
        let current = Url::parse("https://old.example/directorio?p=2").unwrap();
        let moved = Url::parse("https://new.example/directorio?p=2").unwrap();
        let diff_path = Url::parse("https://new.example/login").unwrap();
        let diff_query = Url::parse("https://new.example/directorio?p=1").unwrap();
        let http_target = Url::parse("http://new.example/directorio?p=2").unwrap();

        assert!(is_permanent_domain_move(301, &current, &moved, &base));
        assert!(is_permanent_domain_move(308, &current, &moved, &base));
        assert!(!is_permanent_domain_move(302, &current, &moved, &base));
        assert!(!is_permanent_domain_move(307, &current, &moved, &base));
        assert!(!is_permanent_domain_move(301, &current, &diff_path, &base));
        assert!(!is_permanent_domain_move(301, &current, &diff_query, &base));
        assert!(!is_permanent_domain_move(
            301,
            &current,
            &http_target,
            &base
        ));
        // Secondary hop from an unrelated origin must not overwrite self.base
        let third = Url::parse("https://third.example/directorio?p=2").unwrap();
        assert!(!is_permanent_domain_move(301, &moved, &third, &base));
    }

    #[test]
    fn artwork_url_enforces_public_https_and_unwraps_next_image() {
        let base = Url::parse("https://site.example/").unwrap();
        assert_eq!(
            artwork_url(&base, Some("/img/cover.jpg")).as_deref(),
            Some("https://site.example/img/cover.jpg")
        );
        assert_eq!(
            artwork_url(
                &base,
                Some("/_next/image?url=https%3A%2F%2Fcdn.example%2Fposter.webp&w=640")
            )
            .as_deref(),
            Some("https://cdn.example/poster.webp")
        );
        assert!(artwork_url(&base, Some("http://site.example/cover.jpg")).is_none());
        assert!(artwork_url(&base, Some("https://user:pass@site.example/cover.jpg")).is_none());
        assert!(artwork_url(&base, Some("https://127.0.0.1/cover.jpg")).is_none());
        assert!(artwork_url(&base, Some("javascript:alert(1)")).is_none());
    }

    #[test]
    fn leercapitulo_obfuscated_decoder_extracts_alphabets_and_orders_pages() {
        let decoded_alpha = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        // Deterministic 62-char permutation (reverse of decoded_alpha)
        let encoded_alpha: String = decoded_alpha.chars().rev().collect();
        let d1 = &decoded_alpha[..31];
        let d2 = &decoded_alpha[31..];
        let e1 = &encoded_alpha[..31];
        let e2 = &encoded_alpha[31..];

        // String table is rotated by 2 relative to the indices used in the expression
        let script = format!(
            r#"
            function _0x4a2b() {{
                var _0x99 = ['noise0', 'noise1', '{d1}', '{d2}', '{e1}', '{e2}'];
                return _0x99;
            }}
            function _0x1c3d(_0x1, _0x2) {{
                var _0x3 = _0x4a2b();
                return _0x1c3d = function(_0x5, _0x6) {{
                    _0x5 = _0x5 - (0x100 + 0x2);
                    return _0x3[_0x5];
                }}, _0x1c3d(_0x1, _0x2);
            }}
            var out = raw.replace(/[A-Z0-9]/gi, c => (_0x1c3d('0x102') + _0x1c3d('0x103'))[(_0x1c3d('0x104') + _0x1c3d('0x105')).indexOf(c)]);
            "#
        );
        let (parsed_dec, parsed_enc) =
            manga::parse_leer_decoder(&script).expect("decoder should parse");
        assert_eq!(parsed_dec, decoded_alpha);
        assert_eq!(parsed_enc, encoded_alpha);

        // Encode 3 shuffled image URLs with the substitution cipher
        let csv =
            "https://cdn.example/p3.jpg,https://cdn.example/p1.jpg,https://cdn.example/p2.jpg";
        let b64 = encode_base64(csv.as_bytes());
        let dec_bytes = decoded_alpha.as_bytes();
        let enc_bytes = encoded_alpha.as_bytes();
        let obfuscated: String = b64
            .chars()
            .map(|c| {
                dec_bytes
                    .iter()
                    .position(|&b| b as char == c)
                    .map(|i| enc_bytes[i] as char)
                    .unwrap_or(c)
            })
            .collect();
        // Order we want: [1, 2, 0] -> reversed string is "0-2-1" with non-digit separators
        let order_key = "0a2bb1";
        let base = Url::parse("https://www.leercapitulo.co/leer/1/slug/1/").unwrap();
        let urls =
            manga::decode_leer_images(&obfuscated, order_key, &parsed_dec, &parsed_enc, &base)
                .expect("images should decode");
        assert_eq!(
            urls,
            vec![
                "https://cdn.example/p1.jpg",
                "https://cdn.example/p2.jpg",
                "https://cdn.example/p3.jpg"
            ]
        );

        // Mismatched order length must fail
        assert!(
            manga::decode_leer_images(&obfuscated, "0-1", &parsed_dec, &parsed_enc, &base).is_err()
        );
    }

    #[test]
    fn jkanime_js_server_parser_extracts_atob_servers_and_video_slots() {
        let ep_url = Url::parse("https://jkanime.net/example/12.5/").unwrap();
        let hls_b64 = encode_base64(b"https://cdn.example/hls/master.m3u8");
        let streamwish_b64 = encode_base64(b"https://streamwish.example/e/abc123");
        let voe_b64 = encode_base64(b"https://voe.example/e/xyz789");
        let html = format!(
            r#"
            <html><body>
            <script>
                var dp = new DPlayer({{ video: {{ url: atob('{hls_b64}') }} }});
                var video = [];
                video[1] = '<iframe src="https://jkanime.net/jkplayer/c?u=123"></iframe>';
                var servers = [
                    {{"server":"Streamwish","remote":"{streamwish_b64}"}},
                    {{"server":"VOE","remote":"{voe_b64}"}}
                ];
            </script>
            </body></html>
            "#
        );
        let streams = jk::jk_extract_streams(&ep_url, &html).expect("streams should parse");
        assert_eq!(streams[0]["url"], "https://cdn.example/hls/master.m3u8");
        assert_eq!(streams[0]["hls"], true);
        assert_eq!(streams[0]["embed"], false);
        assert!(streams
            .iter()
            .any(|s| s["url"] == "https://jkanime.net/jkplayer/c?u=123"
                && s["label"] == "JKPlayer 1"));
        assert!(streams.iter().any(
            |s| s["url"] == "https://streamwish.example/e/abc123" && s["label"] == "Streamwish"
        ));
        assert!(streams
            .iter()
            .any(|s| s["url"] == "https://voe.example/e/xyz789" && s["label"] == "VOE"));
        assert_eq!(streams.last().unwrap()["external"], true);

        // Episode 0 and fractional episodes are supported
        assert_eq!(jk::parse_jk_episode("0"), Some((0.0, "0".into())));
        assert_eq!(jk::parse_jk_episode("12.5"), Some((12.5, "12.5".into())));
        assert_eq!(jk::parse_jk_episode("01.0"), Some((1.0, "1".into())));
    }

    #[test]
    fn animetmo_relative_and_protocol_relative_streams_join_against_base() {
        let base = Url::parse("https://animetmo.com/").unwrap();
        let html = r#"
            <button class="az-srv-btn" data-url="//player.zilla.example/embed/1">Zilla</button>
            <button class="az-srv-btn" data-url="/embed/local-player/42">Local</button>
            <button class="az-srv-btn" data-url="http://insecure.example/v">Insecure</button>
            <button class="az-srv-btn" data-url="https://127.0.0.1/private">Private</button>
        "#;
        let streams = indexed::extract_anime_streams(&base, html).unwrap();
        assert_eq!(streams.len(), 2);
        assert_eq!(streams[0]["url"], "https://player.zilla.example/embed/1");
        assert_eq!(streams[0]["label"], "Zilla");
        assert_eq!(
            streams[1]["url"],
            "https://animetmo.com/embed/local-player/42"
        );
        assert_eq!(streams[1]["label"], "Local");
    }

    #[test]
    fn seriesflix_movies_cursor_coerces_to_series_without_double_crawl() {
        assert_eq!(
            cinema::cinema_cursor(Cinema::Seriesflix, Some("movies:1")).unwrap(),
            ("series", 1)
        );
        assert_eq!(
            cinema::cinema_next_cursor(Cinema::Seriesflix, "series", 1, false),
            None
        );
        assert_eq!(
            cinema::cinema_next_cursor(Cinema::Seriesflix, "movies", 1, false),
            None
        );
        // Other Cinema sites still transition from movies to series:1
        assert_eq!(
            cinema::cinema_next_cursor(Cinema::TioPlus, "movies", 1, false).as_deref(),
            Some("series:1")
        );

        // Case-insensitive card prefix stripping
        assert_eq!(cinema::clean_card_title("Película Matrix"), "Matrix");
        assert_eq!(cinema::clean_card_title("pelicula Matrix"), "Matrix");
        assert_eq!(cinema::clean_card_title("Serie Dark"), "Dark");
        assert_eq!(cinema::clean_card_title("ver serie Dark"), "Dark");
        assert_eq!(cinema::clean_card_title("poster Dune"), "Dune");
    }

    #[test]
    fn cinema_video_links_extracts_poseidon_next_data_and_base64_attributes() {
        let base = Url::parse("https://www.poseidonhd2.co/pelicula/1/demo").unwrap();
        let b64_server = encode_base64(b"https://hoster.example/embed/99");
        let html = format!(
            r#"
            <html><body>
            <li data-tr="{b64_server}">StreamSB Reproducir</li>
            <script id="__NEXT_DATA__" type="application/json">
                {{"props":{{"pageProps":{{"thisMovie":{{"videos":{{"latin":[{{"result":"https://player.example/v/1","cyberlocker":"Fembed"}}]}}}}}}}}}}
            </script>
            </body></html>
            "#
        );
        let links = cinema::cinema_video_links(Some(Cinema::Poseidon), &base, &html).unwrap();
        assert!(links
            .iter()
            .any(|v| v["url"] == "https://hoster.example/embed/99" && v["label"] == "StreamSB"));
        assert!(links
            .iter()
            .any(|v| v["url"] == "https://player.example/v/1" && v["label"] == "Fembed"));
    }

    #[test]
    fn framex_allows_season_zero_and_rejects_invalid_coordinates() {
        assert_eq!(
            framex::framex_stream_suffix("tv", Some(0), 1).unwrap(),
            "&season=0&episode=1"
        );
        assert_eq!(
            framex::framex_stream_suffix("tv", Some(2), 5).unwrap(),
            "&season=2&episode=5"
        );
        assert_eq!(framex::framex_stream_suffix("movie", None, 1).unwrap(), "");
        assert!(framex::framex_stream_suffix("tv", Some(0), 0).is_err());
        assert!(framex::framex_stream_suffix("tv", None, 1).is_err());
        assert!(framex::framex_stream_suffix("movie", Some(0), 1).is_err());
        assert!(framex::framex_stream_suffix("movie", None, 2).is_err());
    }

    #[test]
    fn indexed_olympus_helpers_avoid_part_collisions_and_non_finite_numbers() {
        let p1 = indexed::clean_number("Capítulo 1 - Parte 1");
        let p2 = indexed::clean_number("Capítulo 1 - Parte 2");
        assert_ne!(p1, p2);
        assert_eq!(indexed::clean_number(&p1), p1);
        assert_eq!(indexed::clean_number(&p2), p2);
        assert_eq!(indexed::clean_number("Capítulo 01"), "1");
        assert!(!indexed::is_finite_number("inf"));
        assert!(!indexed::is_finite_number("NaN"));
        assert!(indexed::is_finite_number("12.5"));
        assert_eq!(indexed::map_olympus_status("Anunciado"), "announced");
        assert_eq!(indexed::map_olympus_status("Announced"), "announced");
    }

    #[test]
    fn manhwa_script_selection_and_adult_tagging() {
        let base = Url::parse("https://manhwaweb.com/").unwrap();
        let doc = Document::from(
            r#"<script src="/assets/vendor-abc.js"></script><script src="/assets/index-xyz.js"></script>"#,
        );
        assert_eq!(
            manhwa::select_app_script(&base, &doc).unwrap().path(),
            "/assets/index-xyz.js"
        );
        let fallback_doc = Document::from(r#"<script src="/assets/app-123.js"></script>"#);
        assert_eq!(
            manhwa::select_app_script(&base, &fallback_doc)
                .unwrap()
                .path(),
            "/assets/app-123.js"
        );

        let entry = manhwa::map_item(
            &base,
            &json!({
                "name_esp": "Ejemplo",
                "_tipo": "manhwa",
                "_erotico": "si",
                "_imagen": "https://cdn.example/cover.jpg"
            }),
            "ejemplo_1",
            None,
        )
        .unwrap();
        assert_eq!(
            entry.preview["thumbnailRef"],
            "https://cdn.example/cover.jpg"
        );
        let item: CatalogItem = serde_json::from_value(entry.preview).unwrap();
        assert!(item.adult());
    }

    #[test]
    fn doramas_and_zona_helpers_handle_escaped_ids_xml_entities_and_cascade() {
        assert_eq!(
            doramas::series_id(r#"{\"serie_id\":\"abc123def\"}"#).as_deref(),
            Some("abc123def")
        );
        assert_eq!(
            doramas::series_id(r#"{"serie_id":"xyz789"}"#).as_deref(),
            Some("xyz789")
        );
        assert_eq!(
            doramas_public::parse_episode_coords("mi-dorama-1x1.5"),
            Some(("mi-dorama", 1, "1.5".into()))
        );
        let base = Url::parse("https://doramasflix.in/").unwrap();
        let xml = "<urlset><url><loc>https://doramasflix.in/episodios/a-1x1.5</loc></url></urlset>";
        let locs: Vec<_> = doramas_public::locations(&base, xml).collect();
        assert_eq!(locs.len(), 1);
        assert_eq!(doramas_public::unescape_xml("a&amp;b&lt;c"), "a&b<c");

        let paged = Url::parse("https://zonatmo.org/viewer/12345/paginated").unwrap();
        assert_eq!(
            manga::zona_cascade_url(&paged).unwrap().as_str(),
            "https://zonatmo.org/viewer/12345/cascade"
        );
    }

    #[tokio::test]
    async fn public_site_methods_enforce_self_site_guards() {
        let client = Client::builder().no_proxy().build().unwrap();
        let base = Url::parse("https://example.com/").unwrap();

        let mut framex = Provider::new(Site::Framex, base.clone(), client.clone(), Duration::ZERO);
        assert_eq!(
            framex
                .manga_images(Manga::Leer, "1/slug", "1")
                .await
                .unwrap_err()
                .kind,
            "parse_or_storage_error"
        );
        assert_eq!(
            framex.jk_streams("slug", "1").await.unwrap_err().kind,
            "parse_or_storage_error"
        );
        assert_eq!(
            framex
                .doramas_streams("peliculas-online/slug", None, "1")
                .await
                .unwrap_err()
                .kind,
            "parse_or_storage_error"
        );
        assert_eq!(
            framex.manhwa_images("slug", "1").await.unwrap_err().kind,
            "parse_or_storage_error"
        );

        let mut leer = Provider::new(Site::Manga(Manga::Leer), base, client, Duration::ZERO);
        assert_eq!(
            leer.manga_images(Manga::Zona, "manga/1/slug", "1")
                .await
                .unwrap_err()
                .kind,
            "parse_or_storage_error"
        );
    }
}
