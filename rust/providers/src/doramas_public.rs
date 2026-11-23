use super::*;
use dom_query::Document;
use std::collections::BTreeMap;

// URLs are read from the same public sitemaps the website advertises in robots.txt.
// Associate an episode only when the complete series slug and both coordinates match.
pub(super) type Episodes = BTreeMap<String, BTreeMap<u32, BTreeMap<String, String>>>;

const MAX_EPISODE_SITEMAPS: usize = 16;
const MAX_SITEMAP_BYTES: usize = 8 << 20;
const MAX_TOTAL_SITEMAP_BYTES: usize = 64 << 20;

pub(crate) fn unescape_xml(raw: &str) -> String {
    if !raw.contains('&') {
        return raw.to_owned();
    }
    raw.replace("&amp;", "&")
        .replace("&lt;", "<")
        .replace("&gt;", ">")
        .replace("&quot;", "\"")
        .replace("&apos;", "'")
}

pub(crate) fn parse_episode_coords(route: &str) -> Option<(&str, u32, String)> {
    let (slug, coords) = route.rsplit_once('-')?;
    let (s, n) = coords.split_once('x').or_else(|| coords.split_once('X'))?;
    let season = s.parse::<u32>().ok()?;
    let canon = canonical_number(n);
    if canon.is_empty()
        || canon.len() > 16
        || !canon.bytes().all(|b| b.is_ascii_digit() || b == b'.')
        || !canon
            .parse::<f64>()
            .is_ok_and(|v| v.is_finite() && (0.0..=100_000.0).contains(&v))
    {
        return None;
    }
    if !segment(slug, 512) {
        return None;
    }
    Some((slug, season, canon))
}

pub(crate) fn locations<'a>(base: &Url, xml: &'a str) -> impl Iterator<Item = Url> + 'a {
    let origin = base.origin();
    xml.split("<loc>").skip(1).filter_map(move |s| {
        let text = unescape_xml(s.split_once("</loc>")?.0.trim());
        Url::parse(&text)
            .ok()
            .filter(|u| u.origin() == origin && u.query().is_none() && u.fragment().is_none())
    })
}

fn preview(path: &str, name: &str, thumb: Option<String>) -> Option<Entry> {
    let (section, slug) = path.split_once('/')?;
    if !segment(slug, 512) || name.trim().is_empty() {
        return None;
    }
    let sub = match section {
        "doramas-online" => "series",
        "peliculas-online" => "movie",
        _ => return None,
    };
    Some(Entry {
        external_id: path.into(),
        preview: json!({
            "name": name.trim(),
            "mediaType": "movie",
            "subtype": sub,
            "thumbnailRef": thumb
        }),
    })
}

fn cards(base: &Url, html: &str) -> Vec<Entry> {
    let doc = Document::from(html);
    let main = doc.select("main a[href]");
    let list = if main.is_empty() {
        doc.select("a[href]")
    } else {
        main
    };
    dedup(list.iter().filter_map(|a| {
        let url = base
            .join(&a.attr("href")?)
            .ok()
            .filter(|u| u.origin() == base.origin())?;
        let path = url.path().trim_matches('/');
        let img = a.select("img").first();
        let name = img
            .attr("alt")
            .or_else(|| a.attr("title"))
            .map(|s| s.to_string())
            .unwrap_or_else(|| a.text().trim().to_string());
        let raw_thumb = img
            .attr("data-src")
            .or_else(|| img.attr("src"))
            .map(|s| s.to_string());
        let thumb = artwork_url(base, raw_thumb.as_deref());
        preview(path, &name, thumb)
    }))
}

impl Provider {
    pub(super) fn public_doramas(&self) -> bool {
        self.base.host_str() == Some("doramasflix.in")
            && std::env::var("UMD_DORAMASFLIX_PLATFORM_KEY").map_or(true, |s| s.trim().is_empty())
    }
    pub(super) async fn public_doramas_page(&mut self, section: &str, page: u32) -> Result<Page> {
        let (file, prefix) = match section {
            "series" => ("doramas", "doramas-online/"),
            "movies" => ("movies", "peliculas-online/"),
            _ => return Err(invalid()),
        };
        if !self.doramas_dirs.contains_key(section) {
            let xml = String::from_utf8(
                self.bytes(
                    &format!("sitemaps/{file}.xml"),
                    "application/xml",
                    MAX_SITEMAP_BYTES,
                )
                .await?,
            )
            .map_err(|_| invalid())?;
            let items = dedup(locations(&self.base, &xml).filter_map(|url| {
                let path = url.path().trim_matches('/');
                let slug = path.strip_prefix(prefix)?;
                preview(path, &slug.replace('-', " "), None)
            }));
            if items.is_empty() {
                return Err(invalid());
            }
            self.doramas_dirs.insert(section.into(), items);
        }
        let items = &self.doramas_dirs[section];
        let offset = (page as usize - 1) * 50;
        let slice = items.get(offset..).ok_or_else(invalid)?;
        let next = if slice.len() > 50 {
            Some(format!("{section}:{}", page + 1))
        } else {
            (section == "series").then(|| "movies:1".into())
        };
        Page::directory(slice.iter().take(50).cloned().collect(), next)
    }
    pub(super) async fn public_doramas_cards(&mut self, route: &str) -> Result<Vec<Entry>> {
        let html = self.html(route).await?;
        Ok(cards(&self.base, &html))
    }
    async fn public_episodes(&mut self) -> Result<&Episodes> {
        if self.doramas_eps.is_none() {
            let index = String::from_utf8(
                self.bytes("sitemap.xml", "application/xml", 1 << 20)
                    .await?,
            )
            .map_err(|_| invalid())?;
            let maps: Vec<_> = locations(&self.base, &index)
                .filter(|u| {
                    u.path()
                        .strip_prefix("/sitemaps/episodes-")
                        .and_then(|p| p.strip_suffix(".xml"))
                        .is_some_and(|n| n.parse::<u32>().is_ok())
                })
                .take(MAX_EPISODE_SITEMAPS)
                .collect();
            if maps.is_empty() {
                return Err(invalid());
            }
            let mut series = Episodes::new();
            let mut count = 0usize;
            let mut total_bytes = 0usize;
            for map in maps {
                let remaining = MAX_TOTAL_SITEMAP_BYTES.saturating_sub(total_bytes);
                if remaining == 0 {
                    break;
                }
                let limit = remaining.min(MAX_SITEMAP_BYTES);
                let raw = self.bytes(map.as_str(), "application/xml", limit).await?;
                total_bytes += raw.len();
                let xml = String::from_utf8(raw).map_err(|_| invalid())?;
                for url in locations(&self.base, &xml) {
                    let Some(route) = url.path().strip_prefix("/episodios/") else {
                        continue;
                    };
                    let Some((slug, s, n)) = parse_episode_coords(route) else {
                        continue;
                    };
                    count += 1;
                    if count > 500000 {
                        return Err(invalid());
                    }
                    series
                        .entry(slug.into())
                        .or_default()
                        .entry(s)
                        .or_default()
                        .insert(n, route.into());
                }
            }
            if series.is_empty() {
                return Err(invalid());
            }
            self.doramas_eps = Some(series);
        }
        self.doramas_eps.as_ref().ok_or_else(invalid)
    }
    pub(super) async fn public_seasons(&mut self, slug: &str) -> Result<Vec<Value>> {
        Ok(self
            .public_episodes()
            .await?
            .get(slug)
            .into_iter()
            .flat_map(|seasons| seasons.iter())
            .map(|(s, rows)| {
                let mut nums: Vec<&String> = rows.keys().collect();
                nums.sort_by(|a, b| {
                    let num = |v: &str| {
                        v.parse::<f64>()
                            .ok()
                            .filter(|x| x.is_finite())
                            .unwrap_or(f64::INFINITY)
                    };
                    num(a).total_cmp(&num(b)).then_with(|| a.cmp(b))
                });
                let units: Vec<_> = nums
                    .into_iter()
                    .enumerate()
                    .map(|(i, n)| episode(n, i, None))
                    .collect();
                json!({"number":s,"sortOrder":s,"displayName":format!("Temporada {s}"),"units":units})
            })
            .collect())
    }
    pub(super) async fn public_dorama_episode(
        &mut self,
        slug: &str,
        season: u32,
        n: &str,
    ) -> Result<Vec<Value>> {
        let key = canonical_number(n);
        if key.is_empty() {
            return Err(invalid());
        }
        let route = self
            .public_episodes()
            .await?
            .get(slug)
            .and_then(|s| s.get(&season))
            .and_then(|e| e.get(&key))
            .ok_or_else(invalid)?
            .clone();
        self.public_video(&format!("episodios/{route}")).await
    }
}
