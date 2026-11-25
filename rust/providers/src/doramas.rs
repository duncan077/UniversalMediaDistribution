use super::*;
use dom_query::Document;
use regex::Regex;
use std::{collections::HashMap, sync::LazyLock, time::Instant};

const FIELDS: &str = "_id slug name name_es poster_path poster";
static SERIES_ID: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r#"serie_id\\?["']?\s*:\s*\\?["']([a-zA-Z0-9_-]{1,128})\\?["']"#).unwrap()
});
pub(super) struct EpisodeRoutes {
    series: String,
    since: Instant,
    episodes: HashMap<(u32, String), Box<str>>,
}

fn doramas_artwork(base: &Url, raw: Option<&str>, size: &str) -> Option<String> {
    let raw = raw?.trim();
    if raw.is_empty() {
        return None;
    }
    if raw.starts_with('/') && !raw.starts_with("//") {
        let full = format!("https://image.tmdb.org/t/p/{size}{raw}");
        artwork_url(base, Some(&full))
    } else {
        artwork_url(base, Some(raw))
    }
}

fn entry(base: &Url, v: &Value, movie: bool) -> Option<Entry> {
    let slug = v["slug"].as_str()?;
    if !segment(slug, 512) {
        return None;
    }
    let name = string(&v["name_es"]).or_else(|| string(&v["name"]))?;
    let section = if movie {
        "peliculas-online"
    } else {
        "doramas-online"
    };
    let thumb = doramas_artwork(
        base,
        v["poster_path"].as_str().or_else(|| v["poster"].as_str()),
        "w500",
    );
    let backdrop = doramas_artwork(
        base,
        v["backdrop_path"]
            .as_str()
            .or_else(|| v["backdrop"].as_str()),
        "w1280",
    );
    Some(Entry {
        external_id: format!("{section}/{slug}"),
        preview: json!({
            "name": name,
            "originalName": v["name"],
            "mediaType": "movie",
            "subtype": if movie { "movie" } else { "series" },
            "thumbnailRef": thumb,
            "backdropRef": backdrop,
            "providerSeriesId": v["_id"]
        }),
    })
}

fn media(route: &str) -> Result<bool> {
    let (section, slug) = route.split_once('/').ok_or_else(invalid)?;
    if !segment(slug, 512) {
        return Err(invalid());
    }
    match section {
        "doramas-online" => Ok(false),
        "peliculas-online" => Ok(true),
        _ => Err(invalid()),
    }
}

impl Provider {
    async fn graph(&mut self, op: &str, query: &str, variables: Value) -> Result<Value> {
        if self.api.is_none() {
            let url = if self.base.host_str() == Some("doramasflix.in") {
                Url::parse("https://sv1.fluxcedene.net/api/gql")
            } else {
                self.base.join("api/gql")
            }
            .map_err(|_| invalid())?;
            let client = if url.origin() == self.base.origin() {
                self.client.clone()
            } else {
                pinned(&url, self.cookies.as_ref()).await?
            };
            self.api = Some((url, client));
        }
        let (url, client) = self.api.as_ref().ok_or_else(invalid)?;
        let req = client
            .post(url.clone())
            .header("origin", self.base.origin().ascii_serialization())
            .header("referer", self.base.as_str())
            .json(&json!({"operationName":op,"query":query,"variables":variables}));
        let req = if url.as_str() == "https://sv1.fluxcedene.net/api/gql" {
            let mut request = req.header("platform", "doramasflix");
            if let Ok(key) = std::env::var("UMD_DORAMASFLIX_PLATFORM_KEY") {
                let mut key = reqwest::header::HeaderValue::from_str(key.trim())
                    .map_err(|_| Failure::new("configuration_required"))?;
                key.set_sensitive(true);
                request = request.header("x-access-platform", key);
            }
            request
        } else {
            req
        };
        let mut reply = self.send_json(req, 8 << 20).await?;
        if reply["errors"].as_array().is_some_and(|e| !e.is_empty()) || !reply["data"].is_object() {
            return Err(Failure::new("upstream_response"));
        }
        Ok(reply["data"].take())
    }

    async fn graph_batch(
        &mut self,
        op: &str,
        query: &str,
        variables: Value,
    ) -> Result<Option<Value>> {
        if !self.doramas_batches {
            return Ok(None);
        }
        match self.graph(op, query, variables).await {
            Ok(data) => Ok(Some(data)),
            Err(error)
                if matches!(error.kind, "upstream_response" | "parse_or_storage_error")
                    || matches!(error.status, Some(400 | 404 | 405 | 422)) =>
            {
                self.doramas_batches = false;
                Ok(None)
            }
            Err(error) => Err(error),
        }
    }
    pub(super) async fn doramas_page(&mut self, cursor: Option<&str>) -> Result<Page> {
        let (section, n) = cursor
            .unwrap_or("series:1")
            .split_once(':')
            .ok_or_else(invalid)?;
        let page = page_number(Some(n))?;
        if self.public_doramas() {
            return self.public_doramas_page(section, page).await;
        }
        let (op, field) = match section {
            "movies" => ("listMovies", "paginationMovie"),
            "series" => ("listDoramas", "paginationDorama"),
            _ => return Err(invalid()),
        };
        let query=format!("query {op}($page:Int,$perPage:Int){{ {field}(page:$page,perPage:$perPage,sort:UPDATEDAT_DESC,filter:{{}}){{items{{{FIELDS}}} pageInfo{{hasNextPage}}}}}}");
        let v = self
            .graph(op, &query, json!({"page":page,"perPage":50}))
            .await?;
        let items = dedup(
            rows(&v[field]["items"])?
                .iter()
                .filter_map(|e| entry(&self.base, e, section == "movies")),
        );
        let next = if v[field]["pageInfo"]["hasNextPage"]
            .as_bool()
            .ok_or_else(invalid)?
        {
            Some(format!("{section}:{}", page + 1))
        } else {
            (section == "series").then(|| "movies:1".into())
        };
        Page::directory(items, next)
    }
    pub(super) async fn doramas_search(&mut self, q: &str) -> Result<Page> {
        if self.public_doramas() {
            let query = url::form_urlencoded::Serializer::new(String::new())
                .append_pair("q", q)
                .finish();
            return Ok(Page {
                items: self
                    .public_doramas_cards(&format!("buscar?{query}"))
                    .await?,
                next: None,
            });
        }
        let v=self.graph("searchAll",&format!("query searchAll($input:String!){{searchDorama(input:$input,limit:32){{{FIELDS}}} searchMovie(input:$input,limit:32){{{FIELDS}}}}}"),json!({"input":q})).await?;
        let mut items = Vec::new();
        for (key, movie) in [("searchDorama", false), ("searchMovie", true)] {
            items.extend(
                rows(&v[key])?
                    .iter()
                    .filter_map(|e| entry(&self.base, e, movie)),
            )
        }
        Ok(Page {
            items: dedup(items),
            next: None,
        })
    }
    pub(super) async fn doramas_updates(&mut self) -> Result<Vec<Entry>> {
        if self.public_doramas() {
            return self.public_doramas_cards("").await;
        }
        let query=format!("query latestTitles($size:Int){{series:paginationDorama(page:1,perPage:$size,sort:UPDATEDAT_DESC,filter:{{}}){{items{{{FIELDS}}}}} movies:paginationMovie(page:1,perPage:$size,sort:UPDATEDAT_DESC,filter:{{}}){{items{{{FIELDS}}}}}}}");
        if let Some(data) = self
            .graph_batch("latestTitles", &query, json!({"size":50}))
            .await?
        {
            if let (Some(series), Some(movies)) = (
                data["series"]["items"].as_array(),
                data["movies"]["items"].as_array(),
            ) {
                return Ok(dedup(
                    series
                        .iter()
                        .filter_map(|row| entry(&self.base, row, false))
                        .chain(movies.iter().filter_map(|row| entry(&self.base, row, true))),
                ));
            }
            self.doramas_batches = false;
        }
        let mut items = self.doramas_page(None).await?.items;
        items.extend(self.doramas_page(Some("movies:1")).await?.items);
        Ok(items)
    }
    async fn doramas_episodes(&mut self, id: &str, season: u32) -> Result<Vec<Value>> {
        let mut v=self.graph("listEpisodes","query listEpisodes($season_number:Float!,$serie_id:MongoID!){listEpisodes(sort:NUMBER_ASC,filter:{type_serie:\"dorama\",serie_id:$serie_id,season_number:$season_number}){slug name episode_number season_number}}",json!({"serie_id":id,"season_number":season})).await?;
        let Value::Array(list) = v["listEpisodes"].take() else {
            return Err(invalid());
        };
        if list.len() > 20000 {
            return Err(invalid());
        }
        Ok(list)
    }

    async fn season_batch(&mut self, id: &str, seasons: &[u32]) -> Result<Vec<Vec<Value>>> {
        if seasons.len() > 1 && self.doramas_batches {
            let mut vars = json!({"serie_id":id});
            let mut definitions = String::from("$serie_id:MongoID!");
            let mut fields = String::new();
            use std::fmt::Write as _;
            for (idx, number) in seasons.iter().enumerate() {
                vars[format!("n{idx}")] = json!(number);
                let _ = write!(definitions, ",$n{idx}:Float!");
                let _ = write!(fields, "s{idx}:listEpisodes(sort:NUMBER_ASC,filter:{{type_serie:\"dorama\",serie_id:$serie_id,season_number:$n{idx}}}){{slug name episode_number season_number}} ");
            }
            let query = format!("query seasonEpisodes({definitions}){{{fields}}}");
            if let Some(mut data) = self.graph_batch("seasonEpisodes", &query, vars).await? {
                let mut result = Vec::with_capacity(seasons.len());
                for idx in 0..seasons.len() {
                    match data[format!("s{idx}")].take() {
                        Value::Array(list) if list.len() <= 20_000 => result.push(list),
                        _ => break,
                    }
                }
                if result.len() == seasons.len() {
                    return Ok(result);
                }
                self.doramas_batches = false;
            }
        }
        let mut result = Vec::with_capacity(seasons.len());
        for &season in seasons {
            result.push(self.doramas_episodes(id, season).await?);
        }
        Ok(result)
    }
    pub(super) async fn doramas_detail(&mut self, e: &Entry) -> Result<Value> {
        let movie = media(&e.external_id)?;
        let html = self.html(&e.external_id).await?;
        let mut item = {
            let doc = Document::from(html.as_str());
            let heading = doc.select("h1").first().text().trim().to_string();
            let name = if heading.is_empty() {
                string(&e.preview["name"]).ok_or_else(invalid)?
            } else {
                heading
            };
            let raw_thumb = doc
                .select("meta[property='og:image']")
                .attr("content")
                .map(|s| s.to_string());
            let thumb = artwork_url(&self.base, raw_thumb.as_deref())
                .or_else(|| artwork_url(&self.base, e.preview["thumbnailRef"].as_str()));
            let raw_backdrop = doc
                .select("meta[name='twitter:image']")
                .attr("content")
                .map(|s| s.to_string());
            let backdrop = artwork_url(&self.base, raw_backdrop.as_deref())
                .or_else(|| artwork_url(&self.base, e.preview["backdropRef"].as_str()));
            json!({
                "name": name,
                "originalName": e.preview["originalName"],
                "mediaType": "movie",
                "subtype": if movie { "movie" } else { "series" },
                "thumbnailRef": thumb,
                "backdropRef": backdrop,
                "description": doc
                    .select("meta[name=description],meta[property='og:description']")
                    .attr("content")
                    .map(|s| s.to_string())
            })
        };
        if movie {
            item["units"] = json!([episode(1, 0, item["name"].as_str())]);
            return Ok(item);
        }
        if self.public_doramas() {
            item["seasons"] = json!(
                self.public_seasons(e.external_id.split_once('/').ok_or_else(invalid)?.1)
                    .await?
            );
            return Ok(item);
        }
        let id = series_id(&html)
            .or_else(|| string(&e.preview["providerSeriesId"]))
            .ok_or_else(invalid)?;
        let data=self.graph("listSeasons","query listSeasons($serie_id:MongoID!){listSeasons(sort:NUMBER_ASC,filter:{serie_id:$serie_id}){season_number}}",json!({"serie_id":id})).await?;
        let mut numbers = Vec::new();
        let mut seen = HashSet::new();
        for s in rows(&data["listSeasons"])? {
            let Some(n) = integer(&s["season_number"])
                .and_then(|n| u32::try_from(n).ok())
                .filter(|n| seen.insert(*n))
            else {
                continue;
            };
            if numbers.len() >= 512 {
                return Err(invalid());
            }
            numbers.push(n);
        }
        let mut seasons = Vec::with_capacity(numbers.len());
        let mut routes = HashMap::new();
        for batch in numbers.chunks(16) {
            let lists = self.season_batch(&id, batch).await?;
            for (&season, list) in batch.iter().zip(lists) {
                let units: Vec<_> = list
                    .into_iter()
                    .filter_map(|row| {
                        let number = string(&row["episode_number"])?;
                        if routes.len() < 4096 {
                            if let Some(slug) =
                                row["slug"].as_str().filter(|slug| segment(slug, 512))
                            {
                                routes
                                    .entry((season, canonical_number(&number)))
                                    .or_insert_with(|| slug.into());
                            }
                        }
                        Some((number, row))
                    })
                    .enumerate()
                    .map(|(i, (number, row))| episode(number, i, row["name"].as_str()))
                    .collect();
                seasons.push(json!({"number":season,"sortOrder":season,"displayName":format!("Temporada {season}"),"units":units}));
            }
        }
        self.doramas_routes = Some(EpisodeRoutes {
            series: e.external_id.clone(),
            since: Instant::now(),
            episodes: routes,
        });
        item["seasons"] = json!(seasons);
        Ok(item)
    }
    pub async fn doramas_streams(
        &mut self,
        route: &str,
        season: Option<u32>,
        number: &str,
    ) -> Result<Vec<Value>> {
        if self.site != Site::Doramas {
            return Err(invalid());
        }
        let target = canonical_number(number);
        if target.is_empty() {
            return Err(invalid());
        }
        if media(route)? {
            if season.is_some() || target != "1" {
                return Err(invalid());
            }
            return self.public_video(route).await;
        }
        let season = season.ok_or_else(invalid)?;
        if self.public_doramas() {
            return self
                .public_dorama_episode(
                    route.split_once('/').ok_or_else(invalid)?.1,
                    season,
                    &target,
                )
                .await;
        }
        if let Some(slug) = self
            .doramas_routes
            .as_ref()
            .filter(|cached| {
                cached.series == route && cached.since.elapsed() < Duration::from_secs(120)
            })
            .and_then(|cached| cached.episodes.get(&(season, target.clone())))
        {
            return self.public_video(&format!("episodios/{slug}")).await;
        }
        let html = self.html(route).await?;
        let id = series_id(&html).ok_or_else(invalid)?;
        let list = self.doramas_episodes(&id, season).await?;
        let slug = list
            .iter()
            .find(|e| {
                string(&e["episode_number"]).is_some_and(|ep| canonical_number(&ep) == target)
            })
            .and_then(|e| e["slug"].as_str())
            .filter(|s| segment(s, 512))
            .ok_or_else(invalid)?;
        self.public_video(&format!("episodios/{slug}")).await
    }
}

pub(crate) fn series_id(html: &str) -> Option<String> {
    SERIES_ID.captures(html).map(|c| c[1].to_string())
}
