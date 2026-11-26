use super::*;
use dom_query::Document;
use regex::Regex;
use std::sync::LazyLock;

static ASSIGN: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"\bvar\s+animes\s*=\s*").unwrap());
static ID: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r"(?i)^(?:guardar[_-]?anime|anime)[_-]?(\d{1,32})$").unwrap());
static YEAR: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"\b((?:19|20)\d{2})\b").unwrap());
static ISO_DATE: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r"\b(\d{4}-\d{2}-\d{2})\b").unwrap());
static SERVERS_ASSIGN: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r"\bvar\s+servers\s*=\s*").unwrap());
static VIDEO_ARRAY_ASSIGN: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r"\bvar\s+video\s*=\s*").unwrap());
static VIDEO_SLOT: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(
        r#"(?i)\b(?:video\[(\d+)\]|sources)\s*=\s*(?:'((?:\\.|[^'\\])*)'|"((?:\\.|[^"\\])*)")"#,
    )
    .unwrap()
});
static ATTR_SRC: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r#"(?i)\b(?:src|data-src)\s*=\s*(?:"([^"]+)"|'([^']+)'|([^\s>"']+))"#).unwrap()
});
static ATOB_URL: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r#"(?i)\burl\s*:\s*atob\(\s*['"]([^'"]+)['"]\s*\)"#).unwrap());
static RAW_HLS: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(
        r#"(?i)\b(?:url|src|file|loadSource)\s*[:=(]\s*['"](https?://[^'"]+\.m3u8[^'"]*)['"]"#,
    )
    .unwrap()
});

static EPISODE_THUMB_BASE: LazyLock<Url> = LazyLock::new(|| {
    Url::parse("https://cdn.jkdesa.com/assets/images/animes/video/image_thumb/").unwrap()
});

pub(super) fn parse_jk_episode(raw: &str) -> Option<(f64, String)> {
    let n = canonical_number(raw);
    if n.is_empty() || n.len() > 16 || !n.bytes().all(|b| b.is_ascii_digit() || b == b'.') {
        return None;
    }
    let val = n.parse::<f64>().ok()?;
    (val.is_finite() && (0.0..=100_000.0).contains(&val)).then_some((val, n))
}

fn jk_episode_thumb(base: &Url, raw: Option<&str>) -> Option<String> {
    let raw = raw?.trim();
    if raw.len() <= 4 {
        return None;
    }
    if raw.starts_with("http://") || raw.starts_with("https://") || raw.starts_with("//") {
        artwork_url(base, Some(raw))
    } else if !raw.contains('/') && !raw.contains('\\') && !raw.contains("..") {
        artwork_url(&EPISODE_THUMB_BASE, Some(raw))
    } else {
        artwork_url(base, Some(raw))
    }
}

fn card(base: &Url, row: &Value) -> Option<Entry> {
    let slug = row["slug"].as_str()?.trim_matches('/');
    if !segment(slug, 300) {
        return None;
    }
    let name = string(&row["title"])?;
    let kind = string(&row["tipo"])
        .or_else(|| string(&row["type"]))
        .unwrap_or_default();
    let movie = ["movie", "pelicula"]
        .iter()
        .any(|v| umd_core::normalize(&kind).contains(v));
    let thumb = artwork_url(
        base,
        row["image"].as_str().or_else(|| row["thumbnail"].as_str()),
    );
    Some(Entry {
        external_id: slug.into(),
        preview: json!({
            "name": name,
            "mediaType": "movie",
            "subtype": if movie { "anime_movie" } else { "anime_series" },
            "publicationStatus": string(&row["status"]).or_else(|| string(&row["estado"])),
            "thumbnailRef": thumb,
            "categories": ["Anime"]
        }),
    })
}

fn links(base: &Url, doc: &Document, updates: bool) -> Vec<Entry> {
    dedup(doc.select("a[href]").iter().filter_map(|a| {
        let url = base.join(&a.attr("href")?).ok()?;
        if url.origin() != base.origin() {
            return None;
        }
        let parts: Vec<_> = url.path().trim_matches('/').split('/').collect();
        if updates {
            if parts.len() != 2 || parse_jk_episode(parts[1]).is_none() {
                return None;
            }
        } else if parts.len() != 1 || a.select(".card-title,.d-thumb").is_empty() {
            return None;
        }
        let title = a.select(".card-title,.title,h2,h3,h4,h5").first().text();
        let name = if title.trim().is_empty() {
            a.select("img").attr("alt")?.to_string()
        } else {
            title.trim().to_string()
        };
        let img = a
            .select("img")
            .attr("data-src")
            .or_else(|| a.select("img").attr("src"))
            .or_else(|| a.select("[data-setbg]").attr("data-setbg"))
            .map(|s| s.to_string());
        card(
            base,
            &json!({"slug": parts[0], "title": name, "image": img}),
        )
    }))
}

fn read_meta_field(doc: &Document, labels: &[&str]) -> Option<String> {
    for li in doc
        .select(".anime_data li, .anime_info li, .anime__details__widget li, li")
        .iter()
    {
        let span = li.select("span").first();
        if span.is_empty() {
            continue;
        }
        let key = umd_core::normalize(&span.text());
        if !labels.iter().any(|l| key.starts_with(l)) {
            continue;
        }
        let full = li.text().to_string();
        let span_text = span.text().to_string();
        let body = full
            .strip_prefix(span_text.as_str())
            .unwrap_or(&full)
            .trim()
            .trim_start_matches(':')
            .split_whitespace()
            .collect::<Vec<_>>()
            .join(" ");
        if !body.is_empty() {
            return Some(body);
        }
    }
    None
}

fn read_genres(doc: &Document) -> Vec<String> {
    let mut out = Vec::new();
    let mut seen = HashSet::new();
    for li in doc
        .select(".anime_data li, .anime_info li, .anime__details__widget li, li")
        .iter()
    {
        let span = li.select("span").first();
        if span.is_empty() {
            continue;
        }
        let key = umd_core::normalize(&span.text());
        if !key.starts_with("genero") {
            continue;
        }
        for a in li.select("a").iter() {
            let text = a.text().split_whitespace().collect::<Vec<_>>().join(" ");
            if !text.is_empty() && seen.insert(umd_core::normalize(&text)) {
                out.push(text);
            }
        }
        if !out.is_empty() {
            return out;
        }
    }
    for a in doc.select("a[href*='/genero/'], a[href*='genero=']").iter() {
        let text = a.text().split_whitespace().collect::<Vec<_>>().join(" ");
        if !text.is_empty() && seen.insert(umd_core::normalize(&text)) {
            out.push(text);
        }
    }
    out
}

fn unescape_js_str(raw: &str) -> String {
    raw.replace("\\/", "/")
        .replace("\\\"", "\"")
        .replace("\\'", "'")
        .replace("&amp;", "&")
}

fn extract_src_from_snippet(base: &Url, snippet: &str) -> Vec<Url> {
    let clean = unescape_js_str(snippet);
    let mut found = Vec::new();
    for cap in ATTR_SRC.captures_iter(&clean) {
        let raw = cap
            .get(1)
            .or_else(|| cap.get(2))
            .or_else(|| cap.get(3))
            .map(|m| m.as_str().trim());
        if let Some(u) = raw
            .and_then(|s| base.join(s).ok())
            .filter(|u| u.scheme() == "https" && umd_core::public_url::web_url(u))
        {
            found.push(u);
        }
    }
    if found.is_empty() {
        let trimmed = clean.trim();
        if (trimmed.starts_with("https://")
            || trimmed.starts_with("//")
            || trimmed.starts_with('/'))
            && !trimmed.contains('<')
        {
            if let Some(u) = base
                .join(trimmed)
                .ok()
                .filter(|u| u.scheme() == "https" && umd_core::public_url::web_url(u))
            {
                found.push(u);
            }
        }
    }
    found
}

pub(crate) fn jk_extract_streams(episode_url: &Url, html: &str) -> Result<Vec<Value>> {
    let doc = Document::from(html);
    let mut seen = HashSet::new();
    let mut direct = Vec::new();
    let mut embeds = Vec::new();

    let push_stream = |url: Url,
                       label: String,
                       force_hls: bool,
                       seen: &mut HashSet<Url>,
                       direct: &mut Vec<Value>,
                       embeds: &mut Vec<Value>| {
        if seen.len() >= 100 || !seen.insert(url.clone()) {
            return;
        }
        let player_page = super::streamtape_player_page(&url);
        let hls = !player_page && (force_hls || url.path().ends_with(".m3u8"));
        let mp4 = !player_page && url.path().ends_with(".mp4");
        let entry = json!({
            "url": url.to_string(),
            "hls": hls,
            "embed": !hls && !mp4,
            "label": label
        });
        if hls || mp4 {
            direct.push(entry);
        } else {
            embeds.push(entry);
        }
    };

    for script_node in doc.select("script:not([src])").iter() {
        let script = script_node.text();
        if script.is_empty() {
            continue;
        }

        // 1. DPlayer / JKPlayer atob('...') HLS URLs
        for cap in ATOB_URL.captures_iter(&script) {
            if let Some(decoded) = decode_base64_utf8(&cap[1]) {
                if let Some(url) = episode_url
                    .join(decoded.trim())
                    .ok()
                    .filter(|u| u.scheme() == "https" && umd_core::public_url::web_url(u))
                {
                    let is_hls = url.path().ends_with(".m3u8") || decoded.contains(".m3u8");
                    push_stream(
                        url,
                        "JKPlayer".into(),
                        is_hls,
                        &mut seen,
                        &mut direct,
                        &mut embeds,
                    );
                }
            }
        }

        // 2. Raw HLS URLs in inline player config
        for cap in RAW_HLS.captures_iter(&script) {
            let raw = cap[1].replace("\\/", "/");
            if let Some(url) = episode_url
                .join(raw.trim())
                .ok()
                .filter(|u| u.scheme() == "https" && umd_core::public_url::web_url(u))
            {
                push_stream(
                    url,
                    "JKPlayer".into(),
                    true,
                    &mut seen,
                    &mut direct,
                    &mut embeds,
                );
            }
        }

        // 3. video[N] = '<iframe src="...">' / sources = '...'
        for cap in VIDEO_SLOT.captures_iter(&script) {
            let slot = cap.get(1).map(|m| m.as_str());
            let snippet = cap
                .get(2)
                .or_else(|| cap.get(3))
                .map(|m| m.as_str())
                .unwrap_or("");
            for url in extract_src_from_snippet(episode_url, snippet) {
                let label = match slot {
                    Some(idx) => format!("JKPlayer {idx}"),
                    None => format!("JKPlayer {}", seen.len() + 1),
                };
                push_stream(url, label, false, &mut seen, &mut direct, &mut embeds);
            }
        }

        // 4. var video = [...]
        for m in VIDEO_ARRAY_ASSIGN.find_iter(&script) {
            let rest = script[m.end()..].trim_start();
            if let Some(Ok(Value::Array(items))) = serde_json::Deserializer::from_str(rest)
                .into_iter::<Value>()
                .next()
            {
                for (i, item) in items.iter().enumerate() {
                    if let Some(s) = item.as_str() {
                        for url in extract_src_from_snippet(episode_url, s) {
                            push_stream(
                                url,
                                format!("JKPlayer {}", i + 1),
                                false,
                                &mut seen,
                                &mut direct,
                                &mut embeds,
                            );
                        }
                    }
                }
            }
        }

        // 5. var servers = [{"server":"Streamwish","remote":"<base64>"}, ...]
        for m in SERVERS_ASSIGN.find_iter(&script) {
            let rest = script[m.end()..].trim_start();
            let parsed = serde_json::Deserializer::from_str(rest)
                .into_iter::<Value>()
                .next()
                .and_then(|r| r.ok());
            if let Some(Value::Array(list)) = parsed {
                for row in list {
                    let Some(remote) = row["remote"].as_str().map(str::trim) else {
                        continue;
                    };
                    if remote.is_empty() {
                        continue;
                    }
                    let decoded = decode_base64_utf8(remote);
                    let candidate = decoded
                        .as_deref()
                        .map(str::trim)
                        .filter(|s| {
                            s.starts_with("https://")
                                || s.starts_with("http://")
                                || s.starts_with("//")
                        })
                        .unwrap_or(remote);
                    let Some(url) = episode_url
                        .join(candidate)
                        .ok()
                        .filter(|u| u.scheme() == "https" && umd_core::public_url::web_url(u))
                    else {
                        continue;
                    };
                    let label = string(&row["server"])
                        .unwrap_or_else(|| url.host_str().unwrap_or("Servidor").to_owned());
                    push_stream(url, label, false, &mut seen, &mut direct, &mut embeds);
                }
            }
        }
    }

    let mut out = direct;
    out.extend(embeds);

    // Fallback: collect HTML video/iframe/data-url elements (avoiding duplicates)
    if let Ok(fallback) = cinema::public_video_links(episode_url, html) {
        for entry in fallback {
            if entry["external"] == true {
                continue;
            }
            if let Some(u) = entry["url"].as_str().and_then(|s| Url::parse(s).ok()) {
                if seen.len() < 100 && seen.insert(u) {
                    out.push(entry);
                }
            }
        }
    }

    if episode_url.scheme() == "https" {
        out.push(super::site_player(episode_url.clone()));
    }
    if out.is_empty() {
        return Err(Failure::new("unsupported"));
    }
    Ok(out)
}

impl Provider {
    pub(super) async fn jk_page(&mut self, page: u32) -> Result<Page> {
        self.jk_directory(&format!("directorio?p={page}"), page, false).await
    }
    pub(super) async fn jk_directory(&mut self, route: &str, page: u32, filtered: bool) -> Result<Page> {
        let html = self.html(route).await?;
        let doc = Document::from(html.as_str());
        let listing = ASSIGN
            .find_iter(&html)
            .filter_map(|m| {
                serde_json::Deserializer::from_str(&html[m.end()..])
                    .into_iter::<Value>()
                    .next()?
                    .ok()
            })
            .find(|v| v["data"].is_array());
        let items = listing
            .as_ref()
            .and_then(|v| v["data"].as_array())
            .map(|r| dedup(r.iter().filter_map(|row| card(&self.base, row))))
            .filter(|v| !v.is_empty())
            .unwrap_or_else(|| links(&self.base, &doc, false));
        let next = page + 1;
        let more = listing
            .as_ref()
            .and_then(|v| integer(&v["last_page"]))
            .is_some_and(|p| p >= i64::from(next))
            || doc
                .select("a[href]")
                .iter()
                .filter_map(|a| self.base.join(&a.attr("href")?).ok())
                .any(|u| {
                    u.origin() == self.base.origin()
                        && u.query_pairs()
                            .any(|(k, v)| k == "p" && v.parse::<u32>() == Ok(next))
                });
        let next = more.then(|| next.to_string());
        if filtered { Ok(Page { items, next }) } else { Page::directory(items, next) }
    }
    pub(super) async fn jk_updates(&mut self) -> Result<Vec<Entry>> {
        let html = self.html("").await?;
        Ok(links(&self.base, &Document::from(html), true))
    }
    async fn jk_post(&mut self, route: &str, query: Option<&str>) -> Result<Value> {
        for attempt in 0..2 {
            if self.csrf.is_none() {
                let doc = Document::from(self.html("").await?);
                self.csrf = doc
                    .select("meta[name=csrf-token]")
                    .attr("content")
                    .filter(|v| !v.is_empty() && v.len() < 4096)
                    .map(|v| v.to_string());
            }
            let token = self.csrf.as_deref().ok_or_else(invalid)?;
            let mut form = vec![("_token", token)];
            if let Some(q) = query {
                form.push(("q", q))
            }
            let req = self
                .client
                .post(self.base.join(route).map_err(|_| invalid())?)
                .header("x-requested-with", "XMLHttpRequest")
                .header("referer", self.base.as_str())
                .form(&form);
            match self.send_json(req, 8 << 20).await {
                Err(e) if attempt == 0 && matches!(e.status, Some(417 | 419)) => self.csrf = None,
                result => return result,
            }
        }
        Err(invalid())
    }
    pub(super) async fn jk_search(&mut self, q: &str) -> Result<Page> {
        let data = self.jk_post("ajax_search", Some(q)).await?;
        Ok(Page {
            items: dedup(rows(&data)?.iter().filter_map(|row| card(&self.base, row))),
            next: None,
        })
    }
    pub(super) async fn jk_detail(&mut self, entry: &Entry) -> Result<Value> {
        let slug = &entry.external_id;
        if !segment(slug, 300) {
            return Err(invalid());
        }
        let html = self.html(&format!("{slug}/")).await?;
        let (mut item, id, movie) = {
            let doc = Document::from(html);
            let name = doc
                .select(".anime_info h3,.anime_info h1,h1")
                .first()
                .text()
                .trim()
                .to_string();
            let name = if name.is_empty() {
                string(&entry.preview["name"]).ok_or_else(invalid)?
            } else {
                name
            };
            let tipo = read_meta_field(&doc, &["tipo"]);
            let movie = entry.preview["subtype"] == "anime_movie"
                || tipo.as_deref().is_some_and(|t| {
                    let norm = umd_core::normalize(t);
                    norm.contains("pelicula") || norm.contains("movie")
                })
                || umd_core::normalize(
                    &doc.select(".anime_info li,.anime__details__widget li")
                        .text(),
                )
                .contains("pelicula");
            let desc = {
                let sinopsis = doc
                    .select(".sinopsis, .anime_info .sinopsis, .anime_info p.scroll")
                    .first()
                    .text()
                    .split_whitespace()
                    .collect::<Vec<_>>()
                    .join(" ");
                if !sinopsis.is_empty() {
                    Some(sinopsis)
                } else {
                    doc.select("meta[property='og:description'],meta[name=description]")
                        .attr("content")
                        .map(|s| s.to_string())
                }
            };
            let status = read_meta_field(&doc, &["estado"])
                .or_else(|| string(&entry.preview["publicationStatus"]));
            let genres = read_genres(&doc);
            let date_raw = read_meta_field(&doc, &["estreno", "emision", "fecha"]);
            let release_date = date_raw.as_deref().and_then(|s| {
                ISO_DATE
                    .captures(s)
                    .map(|c| c[1].to_string())
                    .or_else(|| YEAR.captures(s).map(|c| c[1].to_string()))
            });
            let release_year = date_raw
                .as_deref()
                .and_then(|s| YEAR.captures(s))
                .and_then(|c| c[1].parse::<u16>().ok());
            let raw_poster = doc
                .select(".poster, .anime_pic img, .movpic img, .anime_info img")
                .first()
                .attr("data-src")
                .or_else(|| {
                    doc.select(".poster, .anime_pic img, .movpic img, .anime_info img")
                        .first()
                        .attr("src")
                })
                .or_else(|| {
                    doc.select(".poster, .anime_pic img, .movpic img, .anime_info img")
                        .first()
                        .attr("data-setbg")
                })
                .or_else(|| doc.select("meta[property='og:image']").attr("content"))
                .or_else(|| doc.select("meta[name='twitter:image']").attr("content"))
                .map(|s| s.to_string());
            let thumb = artwork_url(&self.base, raw_poster.as_deref())
                .or_else(|| artwork_url(&self.base, entry.preview["thumbnailRef"].as_str()));
            let id = doc
                .select("[data-anime]")
                .attr("data-anime")
                .map(|v| v.to_string())
                .or_else(|| {
                    doc.select("[id]")
                        .iter()
                        .find_map(|n| Some(ID.captures(&n.attr("id")?)?[1].to_string()))
                })
                .filter(|s| {
                    !s.is_empty() && s.len() <= 32 && s.bytes().all(|v| v.is_ascii_digit())
                });
            (
                json!({
                    "name": name,
                    "mediaType": "movie",
                    "subtype": if movie { "anime_movie" } else { "anime_series" },
                    "description": desc,
                    "publicationStatus": status,
                    "genres": genres,
                    "releaseDate": release_date,
                    "releaseYear": release_year,
                    "thumbnailRef": thumb,
                    "backdropRef": thumb,
                    "categories": ["Anime"]
                }),
                id,
                movie,
            )
        };
        let Some(id) = id else {
            if movie {
                let mut unit = episode("1", 0, item["name"].as_str());
                unit["thumbnailRef"] = item["thumbnailRef"].clone();
                item["units"] = json!([unit]);
                return Ok(item);
            }
            return Err(invalid());
        };
        let mut seen = HashSet::new();
        let mut episodes: Vec<(f64, String, Option<String>, Option<String>)> = Vec::new();
        let mut next = 1;
        let mut first = Some(self.jk_post(&format!("ajax/episodes/{id}/1"), None).await?);
        let mut last = integer(&first.as_ref().unwrap()["last_page"])
            .unwrap_or(1)
            .max(1);
        if last > 1000 {
            return Err(invalid());
        }
        while next <= last {
            let values = if let Some(first) = first.take() {
                vec![Ok(first)]
            } else {
                self.request_batch((next..=last.min(next + 2)).map(|p| format!("ajax/episodes/{id}/{p}")).collect(), |mut worker, route: String| async move {
                    Ok(worker.jk_post(&route, None).await)
                })
                .await?
            };
            for response in values {
                let data = response?;
                let p = next;
                next += 1;
                let before = episodes.len();
                for row in rows(&data["data"])? {
                    if let Some((ord, num)) =
                        string(&row["number"]).and_then(|s| parse_jk_episode(&s))
                    {
                        if seen.insert(num.clone()) {
                            let title = string(&row["title"]);
                            let thumb = jk_episode_thumb(&self.base, row["image"].as_str());
                            episodes.push((ord, num, title, thumb));
                        }
                    }
                }
                if episodes.len() > 20000 {
                    return Err(invalid());
                }
                last = integer(&data["last_page"]).unwrap_or(p).max(p);
                if last > 1000 {
                    return Err(invalid());
                }
                if p >= last {
                    break;
                }
                if before == episodes.len() {
                    return Err(invalid());
                }
            }
        }
        if episodes.is_empty() && movie {
            let mut unit = episode("1", 0, item["name"].as_str());
            unit["thumbnailRef"] = item["thumbnailRef"].clone();
            item["units"] = json!([unit]);
            return Ok(item);
        }
        episodes.sort_by(|a, b| a.0.total_cmp(&b.0).then_with(|| a.1.cmp(&b.1)));
        item["units"] = json!(episodes
            .into_iter()
            .enumerate()
            .map(|(i, (_, n, title, thumb))| {
                let mut u = episode(n, i, title.as_deref());
                if let Some(t) = thumb {
                    u["thumbnailRef"] = json!(t);
                }
                u
            })
            .collect::<Vec<_>>());
        Ok(item)
    }
    pub async fn jk_streams(&mut self, slug: &str, n: &str) -> Result<Vec<Value>> {
        if self.site != Site::Jk || !segment(slug, 300) {
            return Err(invalid());
        }
        let (_, n) = parse_jk_episode(n).ok_or_else(invalid)?;
        let route = format!("{slug}/{n}/");
        let episode_url = self.base.join(&route).map_err(|_| invalid())?;
        let html = self.html(&route).await?;
        jk_extract_streams(&episode_url, &html)
    }
}
