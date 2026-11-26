use super::*;
use dom_query::Document;
use regex::Regex;
use std::{collections::BTreeMap, sync::LazyLock};
use umd_core::normalize_genre;

type Genres = BTreeMap<String, String>;

pub(super) fn matches_genre(labels: &Value, wanted: &str) -> bool {
    labels.as_array().into_iter().flatten().any(|label| {
        label
            .as_str()
            .or_else(|| label["name"].as_str())
            .is_some_and(|label| normalize_genre(label) == wanted)
    })
}

fn release_year(preview: &Value) -> Option<u16> {
    integer(&preview["releaseYear"])
        .and_then(|year| u16::try_from(year).ok())
        .or_else(|| preview["releaseDate"].as_str()?.get(..4)?.parse().ok())
}

pub(super) fn script_genres(script: &str) -> Genres {
    static OBJECT: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"\{[^{}]{1,512}\}").unwrap());
    static ID: LazyLock<Regex> = LazyLock::new(|| {
        Regex::new(r#"(?:\{|,)\s*(?:id|["']id["'])\s*:\s*([0-9]{1,8})\b"#).unwrap()
    });
    static NAME: LazyLock<Regex> = LazyLock::new(|| {
        Regex::new(r#"(?:\{|,)\s*(?:nombre|["']nombre["'])\s*:\s*["']([^"']{1,100})["']"#).unwrap()
    });
    OBJECT
        .find_iter(script)
        .filter_map(|object| {
            let token = ID.captures(object.as_str())?.get(1)?.as_str();
            let name = NAME.captures(object.as_str())?.get(1)?.as_str();
            Some((normalize_genre(name), token.to_owned()))
        })
        .collect()
}

fn html_genres(site: Site, base: &Url, doc: &Document) -> Genres {
    if site == Site::Jk {
        return doc
            .select("select[name=genero] option[value]")
            .iter()
            .filter_map(|choice| {
                Some((
                    normalize_genre(&choice.text()),
                    choice.attr("value")?.to_string(),
                ))
            })
            .filter(|(name, token)| !name.is_empty() && segment(token, 100))
            .collect();
    }
    if site == Site::Manga(Manga::Zona) {
        let labels: BTreeMap<_, _> = doc
            .select("label[for]")
            .iter()
            .filter_map(|label| {
                Some((
                    label.attr("for")?.to_string(),
                    normalize_genre(&label.text()),
                ))
            })
            .collect();
        return doc
            .select(r#"input[name="genders[]"][value][id]"#)
            .iter()
            .filter_map(|input| {
                let token = input.attr("value")?;
                if !token.parse::<u32>().is_ok_and(|n| n > 0) {
                    return None;
                }
                let label = labels.get(input.attr("id")?.as_ref())?;
                Some((label.clone(), token.to_string()))
            })
            .collect();
    }
    doc.select("a[href]")
        .iter()
        .filter_map(|anchor| {
            let url = base.join(&anchor.attr("href")?).ok()?;
            if url.origin() != base.origin() {
                return None;
            }
            let path = url.path().trim_matches('/');
            let token = if site == Site::Manga(Manga::Leer) {
                url.query_pairs()
                    .find(|(key, _)| key == "genre")
                    .map(|(_, v)| v.into_owned())
                    .or_else(|| path.strip_prefix("genre/").map(str::to_owned))
            } else {
                path.strip_prefix("genero/").map(str::to_owned)
            }
            .filter(|token| segment(token, 100))?;
            let name = normalize_genre(&anchor.text());
            Some((
                if name.is_empty() {
                    normalize_genre(&token)
                } else {
                    name
                },
                token,
            ))
        })
        .collect()
}

impl Site {
    pub fn supports_metadata_search(self, year: bool) -> bool {
        match self {
            Self::AnimeTmo | Self::Jk | Self::Doramas | Self::Cinema(_) => true,
            Self::Manhwa | Self::Manga(Manga::Leer | Manga::Zona) => !year,
            _ => false,
        }
    }
}

impl Provider {
    async fn genre_token(&mut self, genre: &str) -> Result<Option<String>> {
        if self.site == Site::Manhwa {
            self.prepare_manhwa_api().await?;
        } else if self.filter_genres.is_none() {
            let route = match self.site {
                Site::Jk => "directorio",
                Site::Manga(Manga::Zona) => "biblioteca",
                Site::Manga(Manga::Leer) => "manga/",
                _ => "",
            };
            let html = self.html(route).await?;
            self.filter_genres = Some(html_genres(self.site, &self.base, &Document::from(html)));
        }
        Ok(self
            .filter_genres
            .as_ref()
            .and_then(|genres| genres.get(genre).cloned()))
    }

    pub async fn search_metadata(
        &mut self,
        query: &str,
        genre: Option<&str>,
        year: Option<u16>,
        cursor: Option<&str>,
    ) -> Result<Page> {
        let empty = || Page {
            items: vec![],
            next: None,
        };
        if query.len() > 512
            || genre.is_some_and(|value| value.len() > 100)
            || year.is_some_and(|value| !(1800..=2200).contains(&value))
        {
            return Err(invalid());
        }
        if !self.site.supports_metadata_search(year.is_some()) || genre.is_none() && year.is_none()
        {
            return Ok(empty());
        }
        let genre = genre.map(normalize_genre);
        let genre = genre.as_deref();
        let q = umd_core::normalize(query);
        if self.site == Site::Doramas {
            if self.public_doramas() {
                return Ok(empty());
            }
            let mut result = self.doramas_filtered(genre, year, cursor).await?;
            result.items.retain(|entry| name_matches(entry, &q));
            return Ok(result);
        }
        let page = page_number(cursor)?;
        if self.site == Site::AnimeTmo {
            let rows = self.index().await?;
            let mut matching = rows
                .iter()
                .filter(|row| row.normalized.contains(&q) && row.matches_metadata(genre, year))
                .skip((page as usize - 1) * 50);
            let items = matching
                .by_ref()
                .take(50)
                .map(indexed::CompactEntry::entry)
                .collect();
            return Ok(Page {
                items,
                next: matching.next().map(|_| (page + 1).to_string()),
            });
        }
        let token = if let Some(genre) = genre {
            let Some(token) = self.genre_token(genre).await? else {
                return Ok(empty());
            };
            Some(token)
        } else {
            None
        };
        let mut result = match self.site {
            Site::Jk => {
                let mut route = format!("directorio?p={page}");
                if let Some(token) = token {
                    route.push_str(&format!("&genero={token}"));
                }
                if let Some(year) = year {
                    route.push_str(&format!("&fecha={year}"));
                }
                self.jk_directory(&route, page, true).await?
            }
            Site::Cinema(site) => {
                if let Some(token) = token {
                    self.cinema_genre(site, &token, page).await?
                } else if let Some(year) = year.filter(|_| page == 1) {
                    let term = if q.is_empty() {
                        year.to_string()
                    } else {
                        query.trim().to_owned()
                    };
                    self.cinema_search(site, &term).await?
                } else {
                    return Ok(empty());
                }
            }
            Site::Manhwa => {
                self.manhwa_library(
                    page - 1,
                    (!q.is_empty()).then_some(query.trim()),
                    token.as_deref(),
                )
                .await?
            }
            Site::Manga(site) => {
                let token = token.as_deref().ok_or_else(invalid)?;
                let encoded: String =
                    url::form_urlencoded::byte_serialize(query.trim().as_bytes()).collect();
                if site == Manga::Leer {
                    let root = format!("manga/?page={page}&genre={token}&q={encoded}");
                    match self.manga_directory(site, &root, page, true).await {
                        Err(failure) if failure.status == Some(404) => {
                            self.manga_directory(
                                site,
                                &format!("genre/{token}/?page={page}"),
                                page,
                                true,
                            )
                            .await?
                        }
                        result => result?,
                    }
                } else {
                    let route = format!("biblioteca?page={page}&genders%5B%5D={token}&filter_by=title&title={encoded}");
                    self.manga_directory(site, &route, page, true).await?
                }
            }
            _ => return Ok(empty()),
        };
        result.items.retain(|entry| {
            name_matches(entry, &q)
                && (!matches!(self.site, Site::Cinema(_))
                    || year.is_none_or(|wanted| release_year(&entry.preview) == Some(wanted)))
        });
        for entry in &mut result.items {
            if let Some(genre) = genre.filter(|g| !matches_genre(&entry.preview["genres"], g)) {
                if !entry.preview["genres"].is_array() {
                    entry.preview["genres"] = json!([]);
                }
                if let Some(labels) = entry.preview["genres"].as_array_mut() {
                    labels.push(json!(genre));
                }
            }
            if let Some(year) =
                year.filter(|_| self.site == Site::Jk && release_year(&entry.preview).is_none())
            {
                entry.preview["releaseYear"] = json!(year);
            }
        }
        Ok(result)
    }
}

pub(super) fn name_matches(entry: &Entry, q: &str) -> bool {
    q.is_empty()
        || ["name", "originalName"].iter().any(|field| {
            entry.preview[*field]
                .as_str()
                .is_some_and(|s| umd_core::normalize(s).contains(q))
        })
        || entry.preview["alternativeNames"]
            .as_array()
            .into_iter()
            .flatten()
            .any(|entry| {
                entry["name"]
                    .as_str()
                    .is_some_and(|s| umd_core::normalize(s).contains(q))
            })
}
