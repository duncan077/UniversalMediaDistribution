use super::*;
use dom_query::Document;
use regex::Regex;
use std::{collections::BTreeMap, sync::LazyLock};

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Cinema {
    PelisPlus,
    TioPlus,
    Pelisflix,
    Poseidon,
    Seriesflix,
}

impl Cinema {
    pub fn base(self) -> &'static str {
        match self {
            Self::PelisPlus => "https://pelisplus.autos/",
            Self::TioPlus => "https://tioplus.app/",
            Self::Pelisflix => "https://pelisflixhd.love/",
            Self::Poseidon => "https://www.poseidonhd2.co/",
            Self::Seriesflix => "https://seriesflixhd.cfd/",
        }
    }
    fn directory(self, section: &str, page: u32) -> String {
        let path = if section == "movies" {
            "peliculas"
        } else {
            "series"
        };
        match (self, page) {
            (Self::Seriesflix, 1) => "series-online/".into(),
            (Self::Seriesflix, _) => format!("peliculas-online/series-online/page/{page}"),
            (Self::PelisPlus, 1) => format!("{path}/"),
            (_, 1) => path.into(),
            (Self::TioPlus, _) => format!("{path}/{page}"),
            (Self::PelisPlus, _) => format!("{path}/page/{page}/"),
            _ => format!("{path}/page/{page}"),
        }
    }
}

static YEAR: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"\b(19|20)[0-9]{2}\b").unwrap());
static COORD: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r"(?i)(?:^|[-/\s])(\d+)[ ]*x[ ]*(\d+(?:\.\d+)?)$").unwrap());
static EP: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r"(?i)^(?:episodio|episode)\s*[:/ -]*([0-9]+(?:[.,][0-9]+)?)\b").unwrap()
});
static SEASON: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r"(?i)(?:temporada|season)[- /:]+(\d+)\b").unwrap());
static PREFIX: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r"(?i)^(?:ver\s+)?(?:pel[ií]cula|serie)\s+").unwrap());
static CARD_PREFIX: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r"(?i)^(?:(?:poster|ver)\s+)?(?:pel[ií]cula|serie)\s+|^(?:poster|ver)\s+").unwrap()
});
static JSON_SERVER: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r#"(?i)["'](?:link|result)["']\s*:\s*["'](https?:\\?/\\?/[^"']+)["']"#).unwrap()
});

fn tidy(s: &str) -> String {
    s.split_whitespace().collect::<Vec<_>>().join(" ")
}

struct Link {
    url: Url,
    label: String,
    text: String,
    artwork: Option<String>,
}

fn links(root: &Url, doc: &Document) -> Vec<Link> {
    doc.select("a[href]")
        .iter()
        .filter_map(|a| {
            let mut url = root.join(a.attr("href")?.as_ref()).ok()?;
            // The current directory still emits absolute links on its former domains.
            if root.host_str() == Some("seriesflixhd.cfd")
                && matches!(
                    url.host_str(),
                    Some("seriesflixhd.boats" | "seriesflixhd.pw")
                )
                && url.scheme() == "https"
                && url.port().is_none()
                && url.username().is_empty()
                && url.password().is_none()
            {
                url.set_host(root.host_str()).ok()?;
            }
            if url.origin() != root.origin() || url.fragment().is_some() {
                return None;
            }
            if root.host_str() == Some("tioplus.app") {
                if let Some((prefix, rest)) = url.path().trim_start_matches('/').split_once('/') {
                    if prefix.eq_ignore_ascii_case("index%2ephp") || prefix == "index.php" {
                        let path = format!("/{rest}");
                        url.set_path(&path);
                    }
                }
            }
            let text = tidy(&a.text());
            let heading = tidy(&a.select("h2,h3,h4,.title,.Title").first().text());
            let img = a.select("img").first();
            let label = [
                Some(heading),
                img.attr("alt").map(|s| tidy(&s)),
                a.attr("title").map(|s| tidy(&s)),
                Some(text.clone()),
            ]
            .into_iter()
            .flatten()
            .find(|s| !s.is_empty())
            .unwrap_or_default();
            let raw_img = img
                .attr("data-src")
                .or_else(|| img.attr("src"))
                .map(|s| s.to_string());
            let artwork = artwork_url(root, raw_img.as_deref());
            Some(Link {
                url,
                label,
                text,
                artwork,
            })
        })
        .collect()
}

fn kind(route: &str) -> Option<&'static str> {
    let mut parts = route.trim_matches('/').split('/');
    let prefix = parts.next()?;
    let slug = parts.next()?;
    let extra = parts.next();
    if parts.next().is_some()
        || extra.is_some_and(|s| slug.parse::<u64>().is_err() || !segment(s, 512))
        || !segment(slug, 512)
        || matches!(slug, "page" | "series-online")
    {
        return None;
    }
    match prefix {
        "pelicula" | "peliculas-online" => Some("movie"),
        "serie" | "doramas-online" => Some("series"),
        _ => None,
    }
}

pub(super) fn clean_card_title(raw: &str) -> String {
    CARD_PREFIX.replace(raw.trim(), "").trim().to_string()
}

fn cards(links: &[Link], site: Cinema) -> Vec<Entry> {
    dedup(links.iter().filter_map(|l| {
        let path = l.url.path().trim_start_matches('/');
        let sub = kind(path)?;
        let name = clean_card_title(&l.label);
        if name.is_empty() {
            return None;
        }
        let external = if site == Cinema::PelisPlus {
            path.trim_end_matches('/')
        } else {
            path
        };
        Some(Entry {
            external_id: external.into(),
            preview: json!({
                "mediaType": "movie",
                "subtype": sub,
                "name": name,
                "originalLanguage": "es",
                "thumbnailRef": l.artwork,
                "releaseYear": YEAR.find(&l.text).and_then(|m| m.as_str().parse::<u16>().ok())
            }),
        })
    }))
}

pub(crate) fn cinema_cursor(site: Cinema, cursor: Option<&str>) -> Result<(&'static str, u32)> {
    let start = if site == Cinema::Seriesflix {
        "series:1"
    } else {
        "movies:1"
    };
    let (raw_section, p) = cursor
        .unwrap_or(start)
        .split_once(':')
        .ok_or_else(invalid)?;
    let p = p
        .parse::<u32>()
        .ok()
        .filter(|p| (1..=100000).contains(p))
        .ok_or_else(invalid)?;
    let section = match (site, raw_section) {
        (Cinema::Seriesflix, "movies" | "series") => "series",
        (_, "movies") => "movies",
        (_, "series") => "series",
        _ => return Err(invalid()),
    };
    Ok((section, p))
}

pub(crate) fn cinema_next_cursor(
    site: Cinema,
    section: &str,
    page: u32,
    has_next_page: bool,
) -> Option<String> {
    if has_next_page {
        Some(format!("{section}:{}", page + 1))
    } else if section == "movies" && site != Cinema::Seriesflix {
        Some("series:1".into())
    } else {
        None
    }
}

#[derive(Default)]
struct Episodes {
    units: Vec<Value>,
    routes: BTreeMap<String, String>,
}

impl Episodes {
    fn add(&mut self, n: &str, label: Option<&str>, route: Option<String>) -> Result<()> {
        let n = n.replace(',', ".");
        if n.len() > 32 || !n.parse::<f64>().is_ok_and(|v| v.is_finite() && v >= 0.) {
            return Ok(());
        }
        let key = canonical_number(&n);
        if key.is_empty() || self.routes.contains_key(&key) {
            return Ok(());
        }
        if self.units.len() >= 20000 {
            return Err(invalid());
        }
        self.units.push(episode(&key, self.units.len(), label));
        self.routes.insert(key, route.unwrap_or_default());
        Ok(())
    }
}

fn list_episodes(links: &[Link], season: u32) -> Result<Episodes> {
    let mut episodes = Episodes::default();
    for l in links {
        let path = l.url.path().trim_end_matches('/');
        if !["/episodio/", "/episode/", "/episodios/"]
            .iter()
            .any(|term| path.contains(term))
        {
            continue;
        }
        let n = if let Some(cap) = COORD.captures(path) {
            if cap[1].parse::<u32>().ok() != Some(season) {
                continue;
            }
            cap[2].to_string()
        } else if let Some(cap) = EP.captures(&l.text) {
            cap[1].to_string()
        } else if let Some((_, n)) = path.rsplit_once('/') {
            n.into()
        } else {
            continue;
        };
        episodes.add(&n, Some(&l.label), Some(l.url.to_string()))?;
    }
    Ok(episodes)
}

fn find_seasons(v: &Value) -> Option<&Vec<Value>> {
    if let Some(s) = v.get("seasons").and_then(Value::as_array) {
        return Some(s);
    }
    match v {
        Value::Object(o) => o.values().find_map(find_seasons),
        Value::Array(a) => a.iter().find_map(find_seasons),
        _ => None,
    }
}

fn inline_seasons(
    site: Cinema,
    doc: &Document,
    base: &Url,
    route: &str,
) -> Result<BTreeMap<u32, Episodes>> {
    let mut seasons = BTreeMap::new();
    if site == Cinema::TioPlus {
        for node in doc.select("script:not([src])").iter() {
            let script = node.text();
            let Some((_, rest)) = script.split_once("const seasonsJson") else {
                continue;
            };
            let Some(rest) = rest.trim_start().strip_prefix('=') else {
                continue;
            };
            let v = serde_json::Deserializer::from_str(rest.trim_start())
                .into_iter::<Value>()
                .next()
                .ok_or_else(invalid)?
                .map_err(|_| invalid())?;
            for (key, eps) in v.as_object().ok_or_else(invalid)? {
                let Ok(n) = key.parse::<u32>() else { continue };
                let list = seasons.entry(n).or_insert_with(Episodes::default);
                for e in rows(eps)? {
                    if let Some(num) = string(&e["episode"]) {
                        list.add(
                            &num,
                            e["title"].as_str(),
                            Some(format!(
                                "{}/season/{n}/episode/{num}",
                                route.trim_end_matches('/')
                            )),
                        )?;
                    }
                }
            }
            break;
        }
    } else if site == Cinema::Poseidon {
        if let Ok(v) = serde_json::from_str::<Value>(&doc.select("script#__NEXT_DATA__").text()) {
            for s in find_seasons(&v).into_iter().flatten() {
                let Some(n) = integer(&s["number"]).and_then(|n| u32::try_from(n).ok()) else {
                    continue;
                };
                let list = seasons.entry(n).or_insert_with(Episodes::default);
                for e in s["episodes"].as_array().into_iter().flatten() {
                    let num = string(&e["number"]).or_else(|| {
                        COORD
                            .captures(e["title"].as_str()?)
                            .filter(|c| c[1].parse() == Ok(n))
                            .map(|c| c[2].to_string())
                    });
                    if let Some(num) = num {
                        let slug = e["slug"].as_str().or_else(|| e["slug"]["name"].as_str());
                        let path = slug
                            .and_then(|s| {
                                base.join(&format!(
                                    "serie/{}",
                                    s.trim_start_matches("series/")
                                        .replace("/seasons/", "/temporada/")
                                        .replace("/episodes/", "/episodio/")
                                ))
                                .ok()
                            })
                            .filter(|u| u.origin() == base.origin())
                            .map(String::from);
                        list.add(&num, e["title"].as_str(), path)?;
                    }
                }
            }
        }
    }
    Ok(seasons)
}

impl Provider {
    pub(super) async fn cinema_page(&mut self, site: Cinema, cursor: Option<&str>) -> Result<Page> {
        let (section, p) = cinema_cursor(site, cursor)?;
        let html = self.html(&site.directory(section, p)).await?;
        let links = links(&self.base, &Document::from(html));
        let next = self
            .base
            .join(&site.directory(section, p + 1))
            .map_err(|_| invalid())?;
        let has_next_page = links
            .iter()
            .any(|l| l.url.path().trim_end_matches('/') == next.path().trim_end_matches('/'));
        let next = cinema_next_cursor(site, section, p, has_next_page);
        Page::directory(cards(&links, site), next)
    }
    pub(super) async fn cinema_search(&mut self, site: Cinema, query: &str) -> Result<Page> {
        let encoded: String = url::form_urlencoded::byte_serialize(query.as_bytes()).collect();
        let route = match site {
            Cinema::TioPlus => format!("search/{encoded}"),
            Cinema::Poseidon => format!("search?q={encoded}"),
            Cinema::Seriesflix => format!("?s={encoded}"),
            _ => format!("busqueda/{encoded}"),
        };
        let html = self.html(&route).await?;
        Ok(Page {
            items: cards(&links(&self.base, &Document::from(html)), site),
            next: None,
        })
    }
    pub(super) async fn cinema_updates(&mut self, site: Cinema) -> Result<Vec<Entry>> {
        let html = self.html("").await?;
        Ok(cards(&links(&self.base, &Document::from(html)), site)
            .into_iter()
            .take(24)
            .collect())
    }
    async fn cinema_seasons(
        &mut self,
        site: Cinema,
        route: &str,
        html: &str,
        wanted: Option<u32>,
    ) -> Result<BTreeMap<u32, Episodes>> {
        let (mut seasons, links) = {
            let doc = Document::from(html);
            (
                inline_seasons(site, &doc, &self.base, route)?,
                links(&self.base, &doc),
            )
        };
        if !seasons.is_empty() {
            return Ok(seasons);
        }
        let mut pages = BTreeMap::new();
        for l in &links {
            let p = l.url.path().trim_end_matches('/');
            if !p.contains("/temporada/") && !p.contains("/season/") {
                continue;
            }
            let n = SEASON
                .captures(&l.label)
                .and_then(|c| c[1].parse().ok())
                .or_else(|| p.rsplit(['-', '/']).next()?.parse::<u32>().ok());
            if let Some(n) = n.filter(|n| wanted.is_none_or(|w| w == *n)) {
                pages.entry(n).or_insert(l.url.clone());
            }
        }
        if pages.len() > 100 {
            return Err(invalid());
        }
        if pages.is_empty() {
            let eps = list_episodes(&links, wanted.unwrap_or(1))?;
            if !eps.units.is_empty() {
                seasons.insert(wanted.unwrap_or(1), eps);
            }
            return Ok(seasons);
        }
        let mut total = 0;
        for (n, url) in pages {
            let html = self.html(url.as_str()).await?;
            let eps = list_episodes(&self::links(&self.base, &Document::from(html)), n)?;
            total += eps.units.len();
            if total > 20000 {
                return Err(invalid());
            }
            seasons.insert(n, eps);
        }
        Ok(seasons)
    }
    pub(super) async fn cinema_detail(&mut self, site: Cinema, e: &Entry) -> Result<Value> {
        let route = &e.external_id;
        let sub = kind(route).ok_or_else(invalid)?;
        let html = self.html(route).await?;
        let mut item = {
            let doc = Document::from(html.as_str());
            let heading = tidy(&doc.select("h1").first().text());
            let name = PREFIX.replace(&heading, "");
            if name.is_empty() {
                return Err(invalid());
            }
            let desc = doc
                .select("meta[property='og:description']")
                .attr("content")
                .map(|s| s.to_string())
                .or_else(|| {
                    doc.select("p")
                        .iter()
                        .map(|p| tidy(&p.text()))
                        .find(|s| s.len() >= 40)
                });
            let raw_thumb = doc
                .select("meta[property='og:image']")
                .attr("content")
                .or_else(|| {
                    let img = doc.select(".poster img, .Image img, figure img").first();
                    img.attr("data-src").or_else(|| img.attr("src"))
                })
                .map(|s| s.to_string());
            let thumb = artwork_url(&self.base, raw_thumb.as_deref())
                .or_else(|| artwork_url(&self.base, e.preview["thumbnailRef"].as_str()));
            let raw_backdrop = doc
                .select("[data-backdrop]")
                .first()
                .attr("data-backdrop")
                .or_else(|| doc.select("meta[name='twitter:image']").attr("content"))
                .map(|s| s.to_string());
            let backdrop = artwork_url(&self.base, raw_backdrop.as_deref())
                .or_else(|| artwork_url(&self.base, e.preview["backdropRef"].as_str()));
            let mut seen = HashSet::new();
            let genres: Vec<_> = links(&self.base, &doc)
                .into_iter()
                .filter(|l| {
                    l.url.path().contains("/genero/")
                        && !l.label.is_empty()
                        && seen.insert(l.label.clone())
                })
                .map(|l| l.label.trim_end_matches(',').to_owned())
                .collect();
            json!({
                "name": name,
                "mediaType": "movie",
                "subtype": sub,
                "originalLanguage": "es",
                "description": desc,
                "thumbnailRef": thumb,
                "backdropRef": backdrop,
                "genres": genres,
                "releaseYear": e.preview["releaseYear"]
            })
        };
        if sub == "movie" {
            item["units"] = json!([episode(1, 0, item["name"].as_str())])
        } else {
            let seasons = self.cinema_seasons(site, route, &html, None).await?;
            item["seasons"] = json!(seasons
                .into_iter()
                .map(|(n, eps)| {
                    json!({"number":n,"sortOrder":n,"displayName":format!("Temporada {n}"),"units":eps.units})
                })
                .collect::<Vec<_>>())
        }
        Ok(item)
    }
    pub async fn cinema_streams(
        &mut self,
        route: &str,
        season: Option<u32>,
        number: &str,
    ) -> Result<Vec<Value>> {
        let Site::Cinema(site) = self.site else {
            return Err(invalid());
        };
        let sub = kind(route).ok_or_else(invalid)?;
        let target = canonical_number(number);
        if target.is_empty() {
            return Err(invalid());
        }
        let mut url = self.base.join(route).map_err(|_| invalid())?;
        let mut html = self.html(route).await?;
        if sub == "series" {
            let s = season.ok_or_else(invalid)?;
            let mut list = self.cinema_seasons(site, route, &html, Some(s)).await?;
            let path = list
                .get_mut(&s)
                .and_then(|e| e.routes.remove(&target))
                .filter(|r| !r.is_empty())
                .ok_or_else(invalid)?;
            url = self.base.join(&path).map_err(|_| invalid())?;
            html = self.html(&path).await?;
        } else if season.is_some() || target != "1" {
            return Err(invalid());
        }
        cinema_video_links(Some(site), &url, &html)
    }
    pub(super) async fn public_video(&mut self, route: &str) -> Result<Vec<Value>> {
        let url = self.base.join(route).map_err(|_| invalid())?;
        let html = self.html(route).await?;
        public_video_links(&url, &html)
    }
}

fn is_static_asset(url: &Url) -> bool {
    let path = url.path().to_ascii_lowercase();
    [
        ".jpg", ".jpeg", ".png", ".webp", ".gif", ".svg", ".ico", ".css", ".js", ".woff", ".woff2",
    ]
    .iter()
    .any(|ext| path.ends_with(ext))
}

fn decode_server_value(site: Option<Cinema>, attr: &str, raw: &str) -> String {
    let trimmed = raw.trim();
    if let Some(decoded) = decode_base64_utf8(trimmed) {
        let d = decoded.trim();
        if d.starts_with("https://") || d.starts_with("http://") || d.starts_with("//") {
            return d.to_string();
        }
    }
    if site == Some(Cinema::TioPlus)
        && attr == "data-server"
        && !trimmed.is_empty()
        && !trimmed.starts_with("http://")
        && !trimmed.starts_with("https://")
        && !trimmed.starts_with("//")
        && !trimmed.starts_with('/')
    {
        return format!("/player/{}", encode_base64(trimmed.as_bytes()));
    }
    trimmed.to_string()
}

fn collect_next_data_embeds(
    v: &Value,
    base: &Url,
    depth: usize,
    out: &mut Vec<(Url, Option<String>)>,
) {
    if depth > 16 || out.len() >= 100 {
        return;
    }
    match v {
        Value::Object(map) => {
            let label = string(&v["cyberlocker"])
                .or_else(|| string(&v["server"]))
                .or_else(|| string(&v["name"]));
            for key in ["result", "link", "embed", "embedUrl"] {
                if let Some(raw) = map.get(key).and_then(Value::as_str) {
                    let candidate = decode_server_value(None, key, raw);
                    if let Some(u) = base.join(&candidate).ok().filter(|u| {
                        u.scheme() == "https"
                            && umd_core::public_url::web_url(u)
                            && !is_static_asset(u)
                    }) {
                        out.push((u, label.clone()));
                    }
                }
            }
            for (k, child) in map {
                if matches!(
                    k.as_str(),
                    "props"
                        | "pageProps"
                        | "thisMovie"
                        | "thisSerie"
                        | "episode"
                        | "videos"
                        | "embeds"
                        | "players"
                        | "servers"
                        | "latin"
                        | "spanish"
                        | "english"
                        | "subtitled"
                ) || child.is_array()
                {
                    collect_next_data_embeds(child, base, depth + 1, out);
                }
            }
        }
        Value::Array(arr) => {
            for item in arr.iter().take(200) {
                collect_next_data_embeds(item, base, depth + 1, out);
            }
        }
        _ => {}
    }
}

pub(super) fn public_video_links(base: &Url, html: &str) -> Result<Vec<Value>> {
    cinema_video_links(None, base, html)
}

pub(crate) fn cinema_video_links(
    site: Option<Cinema>,
    base: &Url,
    html: &str,
) -> Result<Vec<Value>> {
    let doc = Document::from(html);
    let mut seen = HashSet::new();
    let mut out = Vec::new();
    let mut push = |url: Url, embed: bool, custom_label: Option<String>| {
        if out.len() >= 100 || !seen.insert(url.clone()) {
            return;
        }
        let player_page = super::streamtape_player_page(&url);
        let hls = !player_page && url.path().ends_with(".m3u8");
        let label = custom_label
            .filter(|l| !l.is_empty() && l.len() <= 80)
            .unwrap_or_else(|| format!("Servidor {}", out.len() + 1));
        out.push(json!({
            "url": url.to_string(),
            "hls": hls,
            "embed": player_page || (embed && !hls && !url.path().ends_with(".mp4")),
            "label": label
        }));
    };

    for (selector, attr, embed, decode_b64) in [
        ("video[src],video source[src]", "src", false, false),
        ("iframe[src]", "src", true, false),
        ("iframe[data-src]", "data-src", true, true),
        ("[data-url]", "data-url", true, true),
        ("[data-server]", "data-server", true, true),
        ("[data-tr]", "data-tr", true, true),
        ("[data-embed]", "data-embed", true, true),
        ("[data-video]", "data-video", true, true),
        ("[data-src]:not(img)", "data-src", true, true),
    ] {
        for node in doc.select(selector).iter().take(100) {
            let Some(raw) = node.attr(attr) else {
                continue;
            };
            let resolved = if decode_b64 {
                decode_server_value(site, attr, &raw)
            } else {
                raw.trim().to_string()
            };
            let Some(url) = base.join(&resolved).ok().filter(|u| {
                u.scheme() == "https"
                    && umd_core::public_url::web_url(u)
                    && (!decode_b64 || !is_static_asset(u))
            }) else {
                continue;
            };
            let raw_label = tidy(&node.text());
            let clean_label = raw_label
                .strip_suffix("Reproducir")
                .or_else(|| raw_label.strip_suffix("reproducir"))
                .unwrap_or(&raw_label)
                .trim()
                .to_string();
            push(url, embed, (!clean_label.is_empty()).then_some(clean_label));
        }
    }

    // PoseidonHD2 / Next.js __NEXT_DATA__ embedded server URLs
    let next_data = doc.select("script#__NEXT_DATA__").text();
    if !next_data.trim().is_empty() {
        if let Ok(v) = serde_json::from_str::<Value>(&next_data) {
            let mut embeds = Vec::new();
            collect_next_data_embeds(&v, base, 0, &mut embeds);
            for (url, label) in embeds {
                push(url, true, label);
            }
        }
    }

    // Inline script JSON "link"/"result" server URLs
    for script in doc.select("script:not([src])").iter() {
        let text = script.text();
        if text.is_empty() {
            continue;
        }
        for cap in JSON_SERVER.captures_iter(&text) {
            let raw = cap[1].replace("\\/", "/");
            if let Some(url) = base.join(raw.trim()).ok().filter(|u| {
                u.scheme() == "https" && umd_core::public_url::web_url(u) && !is_static_asset(u)
            }) {
                push(url, true, None);
            }
        }
    }

    if base.scheme() == "https" {
        out.push(super::site_player(base.clone()));
    }
    if out.is_empty() {
        return Err(Failure::new("unsupported"));
    }
    Ok(out)
}

#[cfg(test)]
mod directory_tests {
    use super::*;
    #[test]
    fn legacy_directory_links_keep_their_canonical_title_ids() {
        let page = Document::from(
            r#"<a href="https://seriesflixhd.boats/serie/test/">Serie</a><a href="https://unrelated.example/serie/test/">Other</a>"#,
        );
        let rows = links(&Url::parse(Cinema::Seriesflix.base()).unwrap(), &page);
        assert_eq!(rows.len(), 1);
        assert_eq!(rows[0].url.as_str(), "https://seriesflixhd.cfd/serie/test/");
        let page = Document::from(r#"<a href="/index%2ephp/pelicula/test">Film</a>"#);
        let rows = links(&Url::parse(Cinema::TioPlus.base()).unwrap(), &page);
        assert_eq!(
            cards(&rows, Cinema::TioPlus)[0].external_id,
            "pelicula/test"
        );
    }
}
