use super::*;
use dom_query::Document;
use regex::Regex;
use std::{collections::BTreeMap, sync::LazyLock};

static NUMBER: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"[0-9]+(?:[.,][0-9]+)?").unwrap());
static PART: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r"(?i)\b(?:parte|part|pt\.?)\s*([0-9]+(?:[.,][0-9]+)?)\b").unwrap()
});
static REVISION: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"-[0-9]{8}-[0-9]{9}$").unwrap());
static YEAR: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"\b(?:18|19|20)[0-9]{2}\b").unwrap());

fn movie(s: &str) -> bool {
    let v = umd_core::normalize(s);
    v.contains("pelicula") || v == "movie"
}

fn clean(s: &str) -> String {
    s.split_whitespace().collect::<Vec<_>>().join(" ")
}

fn text(doc: &Document, selector: &str) -> String {
    clean(&doc.select(selector).first().text())
}

fn book(slug: &str, name: &str, thumb: Option<String>) -> Entry {
    Entry {
        external_id: slug.into(),
        preview: json!({
            "mediaType": "book",
            "subtype": "manga",
            "name": name,
            "thumbnailRef": thumb,
            "categories": ["Manga"]
        }),
    }
}

pub(crate) fn clean_number(s: &str) -> String {
    let Some(found) = NUMBER.find(s) else {
        return clean(s);
    };
    let main = canonical_number(found.as_str());
    if let Some(cap) = PART.captures(&s[found.end()..]) {
        let sub = canonical_number(&cap[1]);
        if !sub.is_empty() {
            return format!("{main} - Parte {sub}");
        }
    }
    main
}

pub(super) fn is_finite_number(s: &str) -> bool {
    s.parse::<f64>().is_ok_and(|v| v.is_finite())
}

fn numeric_sort(a: &str, b: &str) -> std::cmp::Ordering {
    let key = |s: &str| {
        let primary = NUMBER
            .find(s)
            .and_then(|m| m.as_str().replace(',', ".").parse::<f64>().ok())
            .filter(|v| v.is_finite())
            .unwrap_or(f64::INFINITY);
        let secondary = PART
            .captures(s)
            .and_then(|c| c[1].replace(',', ".").parse::<f64>().ok())
            .filter(|v| v.is_finite())
            .unwrap_or(0.0);
        (primary, secondary)
    };
    let ka = key(a);
    let kb = key(b);
    ka.0.total_cmp(&kb.0)
        .then_with(|| ka.1.total_cmp(&kb.1))
        .then_with(|| a.cmp(b))
}

pub(crate) fn map_olympus_status(raw: &str) -> &'static str {
    let status = umd_core::normalize(raw);
    [
        (
            "announced",
            &["anunci", "announc", "proxim", "upcoming"][..],
        ),
        ("ongoing", &["activ", "public", "ongoing"]),
        ("completed", &["final", "complet"]),
        ("cancelled", &["cancel"]),
        ("paused", &["hiatus", "paus"]),
    ]
    .into_iter()
    .find(|(_, words)| words.iter().any(|w| status.contains(w)))
    .map_or("unknown", |(s, _)| s)
}

pub(crate) fn extract_anime_streams(base: &Url, html: &str) -> Result<Vec<Value>> {
    let doc = Document::from(html);
    let mut options = Vec::new();
    let mut seen = HashSet::new();
    for (selector, attr) in [
        ("button.az-srv-btn[data-url]", "data-url"),
        ("iframe[src]", "src"),
    ] {
        for node in doc.select(selector).iter().take(100) {
            let Some(url) = node
                .attr(attr)
                .and_then(|s| base.join(s.trim()).ok())
                .filter(|u| u.scheme() == "https" && umd_core::public_url::web_url(u))
            else {
                continue;
            };
            if !seen.insert(url.clone()) {
                continue;
            }
            let hls = url.path().ends_with(".m3u8");
            let label = clean(&node.text());
            let embed = !hls && !url.path().ends_with(".mp4");
            options.push(json!({
                "url": url.to_string(),
                "hls": hls,
                "embed": embed,
                "label": if label.is_empty() {
                    format!("Servidor {}", options.len() + 1)
                } else {
                    label
                }
            }));
        }
        if !options.is_empty() {
            return Ok(options);
        }
    }
    Err(Failure::new("unsupported"))
}

impl Provider {
    pub(super) async fn index(&mut self) -> Result<&[Entry]> {
        if self.index.is_none() {
            let mut entries = match self.site {
                Site::Olympus => {
                    let v = self.json("api/series/list", 16).await?;
                    dedup(rows(&v["data"])?.iter().filter_map(|r| {
                        let slug = r["slug"].as_str()?;
                        let name = string(&r["name"])?;
                        let thumb = artwork_url(&self.base, r["cover"].as_str());
                        (segment(slug, 240)
                            && r["type"]
                                .as_str()
                                .is_none_or(|t| t.eq_ignore_ascii_case("comic")))
                        .then(|| book(slug, &name, thumb))
                    }))
                }
                Site::AnimeTmo => {
                    let v = self.json("wp-content/uploads/az-catalog.json", 4).await?;
                    let entries = rows(&v)?;
                    if entries.len() > 20000 {
                        return Err(invalid());
                    }
                    dedup(entries.iter().filter_map(|r| {
                        let id = r["id"].as_i64().filter(|v| *v > 0)?;
                        let slug = r["slug"].as_str()?;
                        if !segment(slug, 200) || slug.contains('_') {
                            return None;
                        }
                        let name = string(&r["title"])?;
                        let label = string(&r["type_name"])
                            .or_else(|| string(&r["type_slug"]))
                            .unwrap_or_default();
                        let subtype =
                            if movie(&label) || r["type_slug"].as_str().is_some_and(movie) {
                                "anime_movie"
                            } else {
                                "anime_series"
                            };
                        let mut categories = vec!["Anime"];
                        if !label.is_empty() {
                            categories.push(&label);
                        }
                        let thumb = artwork_url(
                            &self.base,
                            r["cover"]
                                .as_str()
                                .or_else(|| r["poster"].as_str())
                                .or_else(|| r["image"].as_str())
                                .or_else(|| r["img"].as_str())
                                .or_else(|| r["thumbnail"].as_str()),
                        );
                        Some(Entry {
                            external_id: format!("{id}/{slug}"),
                            preview: json!({
                                "name": name,
                                "mediaType": "movie",
                                "subtype": subtype,
                                "thumbnailRef": thumb,
                                "releaseYear": integer(&r["year"]).filter(|y| (1800..=2200).contains(y)),
                                "genres": r["genres"],
                                "publicationStatus": r["stat_name"],
                                "categories": categories
                            }),
                        })
                    }))
                }
                _ => return Err(invalid()),
            };
            if entries.is_empty() {
                return Err(invalid());
            }
            entries.sort_by(|a, b| a.external_id.cmp(&b.external_id));
            self.index = Some(entries);
        }
        Ok(self.index.as_deref().unwrap_or_default())
    }
    pub(super) async fn index_page(&mut self, cursor: Option<&str>) -> Result<Page> {
        let offset = cursor
            .unwrap_or("0")
            .parse::<usize>()
            .map_err(|_| invalid())?;
        let sorted = self.index().await?;
        let page = sorted.get(offset..).ok_or_else(invalid)?;
        Page::directory(
            page.iter().take(500).cloned().collect(),
            (page.len() > 500).then(|| (offset + 500).to_string()),
        )
    }
    pub(super) async fn olympus_updates(&mut self) -> Result<Vec<Entry>> {
        let doc = Document::from(self.html("").await?);
        for section in doc.select("section").iter() {
            if !section.select("h1,h2,h3,h4,h5,h6").iter().any(|h| {
                matches!(
                    umd_core::normalize(&h.text()).as_str(),
                    "nuevos capitulos" | "nuevos lanzamientos"
                )
            }) {
                continue;
            }
            let mut entries = Vec::new();
            for fig in section.select("figure").iter() {
                let img = fig.select("img").first();
                let raw_thumb = img
                    .attr("data-src")
                    .or_else(|| img.attr("src"))
                    .map(|s| s.to_string());
                let thumb = artwork_url(&self.base, raw_thumb.as_deref());
                for a in fig.select("a[href]").iter() {
                    let Some(url) = a.attr("href").and_then(|s| self.base.join(&s).ok()) else {
                        continue;
                    };
                    let Some(slug) = url
                        .path()
                        .trim_end_matches('/')
                        .strip_prefix("/series/comic-")
                    else {
                        continue;
                    };
                    if url.origin() != self.base.origin() || !segment(slug, 240) {
                        continue;
                    }
                    let title = clean(&a.attr("title").unwrap_or_else(|| a.text()));
                    if !title.is_empty() {
                        entries.push(book(slug, &title, thumb.clone()))
                    }
                }
            }
            return Ok(dedup(entries).into_iter().take(24).collect());
        }
        Err(invalid())
    }
    async fn olympus_series(&mut self, e: &Entry) -> Result<(String, Value)> {
        let slug = &e.external_id;
        if !segment(slug, 240) {
            return Err(invalid());
        }
        let url = |s: &str| format!("api/series/{s}?type=comic");
        match self.json(&url(slug), 4).await {
            Ok(v) => Ok((slug.clone(), v)),
            Err(err) if err.status == Some(404) => {
                let wanted = umd_core::normalize(e.preview["name"].as_str().unwrap_or_default());
                let stable = REVISION.replace(slug, "");
                self.index = None;
                let mut choices = self.index().await?.iter().filter(|c| {
                    REVISION
                        .replace(&c.external_id, "")
                        .eq_ignore_ascii_case(&stable)
                        && umd_core::normalize(c.preview["name"].as_str().unwrap_or_default())
                            == wanted
                });
                let Some(first) = choices.next() else {
                    return Err(err);
                };
                let new = first.external_id.clone();
                if choices.next().is_some() || new == *slug {
                    return Err(err);
                }
                let v = self.json(&url(&new), 4).await?;
                if v["data"]["slug"] != new
                    || v["data"]["type"] != "comic"
                    || umd_core::normalize(v["data"]["name"].as_str().unwrap_or_default()) != wanted
                {
                    return Err(invalid());
                }
                Ok((new, v))
            }
            Err(e) => Err(e),
        }
    }
    async fn chapters(
        &mut self,
        slug: &str,
        target: Option<&str>,
    ) -> Result<BTreeMap<String, (String, Value)>> {
        let mut chapters = BTreeMap::new();
        let mut page = 1;
        loop {
            let v = self
                .json(
                    &format!("api/series/{slug}/chapters?page={page}&direction=asc&type=comic"),
                    4,
                )
                .await?;
            for row in rows(&v["data"])? {
                let (Some(id), Some(name)) = (string(&row["id"]), string(&row["name"])) else {
                    continue;
                };
                if !segment(&id, 128) {
                    continue;
                }
                let n = clean_number(&name);
                if !n.is_empty() {
                    chapters
                        .entry(n)
                        .or_insert_with(|| (id, json!(date(&row["published_at"]))));
                }
                if chapters.len() > 20000 {
                    return Err(invalid());
                }
            }
            let last = integer(&v["meta"]["last_page"]).unwrap_or(1);
            if last > 500 {
                return Err(invalid());
            }
            if target.is_some_and(|n| chapters.contains_key(n)) || page >= last {
                break;
            }
            page += 1;
        }
        Ok(chapters)
    }
    pub(super) async fn olympus_detail(&mut self, e: &Entry) -> Result<Value> {
        let (slug, v) = self.olympus_series(e).await?;
        let detail = &v["data"];
        let title = string(&detail["name"]).ok_or_else(invalid)?;
        let status = map_olympus_status(detail["status"]["name"].as_str().unwrap_or(""));
        let thumb = artwork_url(&self.base, detail["cover"].as_str())
            .or_else(|| artwork_url(&self.base, e.preview["thumbnailRef"].as_str()));
        let genres: Vec<_> = detail["genres"]
            .as_array()
            .into_iter()
            .flatten()
            .filter_map(|r| string(&r["name"]))
            .collect();
        let mut chapters: Vec<_> = self.chapters(&slug, None).await?.into_iter().collect();
        chapters.sort_by(|a, b| numeric_sort(&a.0, &b.0));
        let units: Vec<_> = chapters
            .into_iter()
            .enumerate()
            .map(|(i, (n, (_, d)))| {
                json!({"kind":"chapter","displayNumber":n,"sortOrder":i,"releaseDate":d})
            })
            .collect();
        Ok(json!({
            "mediaType": "book",
            "subtype": "manga",
            "name": title,
            "description": string(&detail["summary"]),
            "thumbnailRef": thumb,
            "genres": genres,
            "categories": ["Manga"],
            "publicationStatus": status,
            "units": units
        }))
    }
    pub async fn reader_pages(&mut self, e: &Entry, chapter: &str) -> Result<Vec<String>> {
        if self.site != Site::Olympus || chapter.trim().is_empty() || chapter.len() > 240 {
            return Err(invalid());
        }
        let (slug, _) = self.olympus_series(e).await?;
        let n = clean_number(chapter);
        let (id, _) = self
            .chapters(&slug, Some(&n))
            .await?
            .remove(&n)
            .ok_or_else(invalid)?;
        let route = |r: &str| format!("api/capitulo/{slug}/{r}?type=comic");
        let key = if is_finite_number(&n) { &n } else { &id };
        let v = match self.json(&route(key), 24).await {
            Err(e) if e.status == Some(404) && key != &id => self.json(&route(&id), 24).await?,
            other => other?,
        };
        let row = v.get("chapter").unwrap_or(&v);
        if string(&row["name"]).is_some_and(|s| clean_number(&s) != n) {
            return Err(invalid());
        }
        let pages = rows(&row["pages"])?;
        if pages.is_empty() || pages.len() > 2000 {
            return Err(invalid());
        }
        pages
            .iter()
            .map(|p| {
                let url = Url::parse(p.as_str().ok_or_else(invalid)?).map_err(|_| invalid())?;
                if url.scheme() != "https" || !umd_core::public_url::web_url(&url) {
                    return Err(invalid());
                }
                Ok(url.to_string())
            })
            .collect()
    }
    pub async fn anime_streams(&mut self, external: &str, number: &str) -> Result<Vec<Value>> {
        let (_, slug) = external.split_once('/').ok_or_else(invalid)?;
        if self.site != Site::AnimeTmo
            || !segment(slug, 200)
            || number.is_empty()
            || number.len() > 80
            || !number.starts_with(|c: char| c.is_ascii_alphanumeric())
            || !number
                .bytes()
                .all(|b| b.is_ascii_alphanumeric() || b".-_".contains(&b))
        {
            return Err(invalid());
        }
        let html = self.html(&format!("ver/{slug}/{number}/")).await?;
        extract_anime_streams(&self.base, &html)
    }
    pub(super) async fn anime_detail(&mut self, e: &Entry) -> Result<Value> {
        let (id, slug) = e.external_id.split_once('/').ok_or_else(invalid)?;
        if id.parse::<u64>().ok().is_none_or(|n| n == 0) || !segment(slug, 200) {
            return Err(invalid());
        }
        let doc = Document::from(self.html(&format!("anime/{slug}/")).await?);
        let title = text(&doc, "h1.element-title");
        if title.is_empty() {
            return Err(invalid());
        }
        let mut item = e.preview.clone();
        item["name"] = json!(title);
        item["mediaType"] = json!("movie");
        let label = text(&doc, "h1.book-type");
        item["subtype"] = json!(if movie(&label) || item["subtype"] == "anime_movie" {
            "anime_movie"
        } else {
            "anime_series"
        });
        let desc = text(&doc, "p.element-description");
        item["description"] = if desc.is_empty() {
            json!(doc
                .select("meta[property='og:description']")
                .attr("content")
                .map(|s| s.to_string()))
        } else {
            json!(desc)
        };
        let raw_thumb = doc
            .select("meta[property='og:image']")
            .attr("content")
            .or_else(|| {
                let img = doc
                    .select(".book-thumbnail img,.element-image img,.poster img")
                    .first();
                img.attr("data-src").or_else(|| img.attr("src"))
            })
            .map(|s| s.to_string());
        if let Some(thumb) = artwork_url(&self.base, raw_thumb.as_deref())
            .or_else(|| artwork_url(&self.base, e.preview["thumbnailRef"].as_str()))
        {
            item["thumbnailRef"] = json!(thumb);
        }
        let status = text(&doc, "span.book-status");
        if !status.is_empty() {
            item["publicationStatus"] = json!(status)
        }
        if let Some(year) = YEAR
            .find(&text(&doc, ".manga-meta-bar"))
            .and_then(|m| m.as_str().parse::<u32>().ok())
        {
            item["releaseYear"] = json!(year)
        }
        if !label.is_empty() {
            item["categories"] = json!(["Anime", label])
        }
        let genres: Vec<_> = doc
            .select("a[href*='genre=']")
            .iter()
            .map(|a| clean(&a.text()))
            .filter(|v| !v.is_empty())
            .collect();
        if !genres.is_empty() {
            item["genres"] = json!(genres)
        }
        let mut aliases = Vec::new();
        let mut seen = HashSet::from([umd_core::normalize(&title)]);
        for tag in doc.select("span.syn-badge").iter() {
            for name in tag.text().split(';').map(str::trim) {
                if !name.is_empty() && seen.insert(umd_core::normalize(name)) {
                    aliases.push(json!({"name":name,"aliasType":"other"}))
                }
            }
        }
        item["alternativeNames"] = json!(aliases);
        let prefix = format!("/ver/{slug}/");
        let mut entries = BTreeMap::new();
        for a in doc.select("a.az-ep-card[href]").iter() {
            let Some(u) = a.attr("href").and_then(|s| self.base.join(&s).ok()) else {
                continue;
            };
            if u.origin() != self.base.origin() {
                continue;
            }
            let Some(n) = u.path().trim_end_matches('/').strip_prefix(&prefix) else {
                continue;
            };
            if !n.is_empty()
                && n.len() <= 80
                && n.bytes()
                    .all(|b| b.is_ascii_alphanumeric() || b".-_".contains(&b))
            {
                entries
                    .entry(if is_finite_number(n) {
                        clean_number(n)
                    } else {
                        n.to_lowercase()
                    })
                    .or_insert(n.to_owned());
            }
            if entries.len() > 20000 {
                return Err(invalid());
            }
        }
        let mut nums: Vec<_> = entries.into_values().collect();
        nums.sort_by(|a, b| numeric_sort(a, b));
        item["units"] = json!(nums
            .iter()
            .enumerate()
            .map(|(i, n)| episode(n, i, Some(&format!("Episodio {n}"))))
            .collect::<Vec<_>>());
        Ok(item)
    }
}
