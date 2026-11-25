use super::*;
fn identity(id: &str) -> Result<(&str, u32)> {
    let (kind, n) = id.split_once('/').ok_or_else(invalid)?;
    let n = n
        .parse::<u32>()
        .ok()
        .filter(|n| (1..=100_000_000).contains(n))
        .ok_or_else(invalid)?;
    if !matches!(kind, "tv" | "movie") {
        return Err(invalid());
    }
    Ok((kind, n))
}
fn framex_artwork(base: &Url, raw: Option<&str>, size: &str) -> Option<String> {
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
pub(super) fn framex_stream_suffix(
    kind: &str,
    season: Option<u32>,
    episode: u32,
) -> Result<String> {
    match (kind, season, episode) {
        ("tv", Some(s), e) if e > 0 => Ok(format!("&season={s}&episode={e}")),
        ("movie", None, 1) => Ok(String::new()),
        _ => Err(invalid()),
    }
}
fn map(base: &Url, v: &Value) -> Option<Entry> {
    let id = integer(&v["id"]).filter(|n| (1..=100_000_000).contains(n))?;
    let kind = v["type"].as_str()?;
    let subtype = match kind {
        "tv" => "series",
        "movie" => "movie",
        _ => return None,
    };
    let name = string(&v["title"])?;
    let thumbnail = framex_artwork(
        base,
        v["poster"]
            .as_str()
            .or_else(|| v["poster_path"].as_str())
            .or_else(|| v["image"].as_str()),
        "w500",
    );
    let backdrop = framex_artwork(
        base,
        v["backdrop"]
            .as_str()
            .or_else(|| v["backdrop_path"].as_str()),
        "w1280",
    );
    let mut seen = HashSet::new();
    let genres: Vec<_> = v["genres"]
        .as_array()
        .into_iter()
        .flatten()
        .filter_map(|g| string(g).or_else(|| string(&g["name"])))
        .filter(|g| seen.insert(umd_core::normalize(g)))
        .collect();
    seen.clear();
    let cast: Vec<_> = v["cast"]
        .as_array()
        .into_iter()
        .flatten()
        .take(100)
        .filter_map(|c| {
            let name = string(&c["name"])?;
            seen.insert(umd_core::normalize(&name)).then_some((
                name,
                &c["character"],
                framex_artwork(
                    base,
                    c["profile"]
                        .as_str()
                        .or_else(|| c["profile_path"].as_str()),
                    "w185",
                ),
            ))
        })
        .enumerate()
        .map(|(i, (n, c, thumb))| {
            json!({"name":n,"kind":"person","thumbnailRef":thumb,"credits":[{"role":"actor","characterName":c,"sortOrder":i}]})
        })
        .collect();
    Some(Entry {
        external_id: format!("{kind}/{id}"),
        preview: json!({"name":name,"subtype":subtype,"mediaType":"movie","description":v["overview"],"originalName":string(&v["original_title"]).or_else(||string(&v["original_name"])),"thumbnailRef":thumbnail,"backdropRef":backdrop,"releaseYear":integer(&v["year"]).filter(|y|(1800..=2200).contains(y)),"releaseDate":date(&v["release_date"]),"runtimeSeconds":integer(&v["runtime"]).filter(|n|(1..10000).contains(n)).map(|n|n*60),"genres":genres,"contributors":cast}),
    })
}
impl Provider {
    async fn framex_json(&mut self, path: &str) -> Result<Value> {
        const HOSTS: [&str; 3] = ["api.framextv.tech", "api.framextv.xyz", "api.framextv.cc"];
        let initial = HOSTS.iter().position(|h| Some(*h) == self.base.host_str());
        let count = if initial.is_some() { 3 } else { 1 };
        let mut last = Failure::new("network");
        for i in 0..count {
            if i > 0 {
                let host = HOSTS[(initial.unwrap() + i) % HOSTS.len()];
                let url = Url::parse(&format!("https://{host}/")).map_err(|_| invalid())?;
                match pinned(&url, self.cookies.as_ref()).await {
                    Ok(c) => {
                        self.base = url;
                        self.client = c;
                    }
                    Err(e) => {
                        last = e;
                        continue;
                    }
                }
            }
            match self.json(path, 4).await {
                Ok(v) if v["success"] == true => return Ok(v),
                Ok(_) => return Err(invalid()),
                Err(e) if e.kind == "network" && e.status.is_none_or(|s| s >= 500) => last = e,
                Err(e) => return Err(e),
            }
        }
        Err(last)
    }
    pub(super) async fn framex_search(&mut self, q: &str, page: u32) -> Result<Page> {
        let query = url::form_urlencoded::Serializer::new(String::new())
            .append_pair("q", q)
            .finish();
        let v = self
            .framex_json(&format!("api/search?{query}&type=multi&page={page}"))
            .await?;
        Ok(Page {
            items: dedup(
                rows(&v["results"])?
                    .iter()
                    .filter_map(|r| map(&self.base, r)),
            ),
            next: (i64::from(page) < integer(&v["totalPages"]).unwrap_or(1))
                .then(|| (page + 1).to_string()),
        })
    }
    pub(super) async fn framex_page(&mut self, cursor: Option<&str>) -> Result<Page> {
        let (kind, n) = cursor
            .unwrap_or("movie:1")
            .split_once(':')
            .ok_or_else(invalid)?;
        let page = n
            .parse::<u32>()
            .ok()
            .filter(|v| (1..=100000).contains(v))
            .ok_or_else(invalid)?;
        if !matches!(kind, "movie" | "tv") {
            return Err(invalid());
        }
        let v = self
            .framex_json(&format!("api/discover?type={kind}&page={page}"))
            .await?;
        let total = integer(&v["totalPages"])
            .filter(|v| (1..=100000).contains(v))
            .ok_or_else(invalid)?;
        let next = if i64::from(page) < total {
            Some(format!("{kind}:{}", page + 1))
        } else {
            (kind == "movie").then(|| "tv:1".into())
        };
        Page::directory(
            dedup(
                rows(&v["results"])?
                    .iter()
                    .filter_map(|r| map(&self.base, r)),
            ),
            next,
        )
    }
    pub(super) async fn framex_updates(&mut self) -> Result<Vec<Entry>> {
        let v = self.framex_json("api/home").await?;
        let mut items: Vec<_> = map(&self.base, &v["hero"]).into_iter().collect();
        for rail in v["rails"]
            .as_object()
            .into_iter()
            .flat_map(|r| r.values())
            .take(30)
        {
            items.extend(
                rail["results"]
                    .as_array()
                    .into_iter()
                    .flatten()
                    .filter_map(|r| map(&self.base, r)),
            )
        }
        Ok(dedup(items).into_iter().take(100).collect())
    }
    pub(super) async fn framex_detail(&mut self, e: &Entry) -> Result<Value> {
        let (kind, id) = identity(&e.external_id)?;
        let v = self
            .framex_json(&format!("api/details?id={id}&type={kind}"))
            .await?;
        let mapped = map(&self.base, &v)
            .filter(|m| m.external_id == e.external_id)
            .ok_or_else(invalid)?;
        let mut item = mapped.preview;
        if kind == "movie" {
            item["units"] = json!([episode(1, 0, item["name"].as_str())]);
            return Ok(item);
        }
        let seasons = rows(&v["seasons"])?;
        if seasons.len() > 100 {
            return Err(invalid());
        }
        let mut seen = HashSet::new();
        let seasons: Vec<_> = seasons
            .iter()
            .filter_map(|season| {
                let number = integer(&season["season_number"])
                    .or_else(|| integer(&season["number"]))
                    .filter(|n| *n >= 0 && seen.insert(*n))?;
                Some((number, season))
            })
            .collect();
        let mut output = Vec::new();
        let mut count = 0;
        for batch in seasons.chunks(3) {
            let values = self
                .request_batch(
                    batch
                        .iter()
                        .map(|(n, _)| format!("api/season?id={id}&season={n}"))
                        .collect(),
                    |mut worker, path: String| async move {
                        let value = worker.framex_json(&path).await?;
                        Ok((value, worker.base))
                    },
                )
                .await?;
            for (&(n, season), (v, base)) in batch.iter().zip(values) {
                if integer(&v["season_number"]).or_else(|| integer(&v["number"])) != Some(n) {
                    if n == 0 {
                        continue;
                    }
                    return Err(invalid());
                }
                let episodes = rows(&v["episodes"])?;
                count += episodes.len();
                if count > 20000 {
                    return Err(invalid());
                }
                let mut units = Vec::new();
                let mut seen = HashSet::new();
                for (i, row) in episodes.iter().enumerate() {
                    let number = integer(&row["episode_number"])
                        .or_else(|| integer(&row["number"]))
                        .filter(|n| *n > 0)
                        .ok_or_else(invalid)?;
                    if integer(&row["season_number"]).is_some_and(|actual| actual != n) {
                        return Err(invalid());
                    }
                    if seen.insert(number) {
                        let mut unit = episode(number, i, row["name"].as_str());
                        unit["releaseDate"] = json!(date(&row["air_date"]));
                        unit["thumbnailRef"] = json!(framex_artwork(
                            &base,
                            row["still"].as_str().or_else(|| row["still_path"].as_str()),
                            "w500",
                        ));
                        units.push(unit);
                    }
                }
                let mut entry = json!({"number":n,"sortOrder":n,"units":units});
                if let Some(display_name) = string(&v["name"]).or_else(|| string(&season["name"])) {
                    entry["displayName"] = json!(display_name);
                }
                output.push(entry);
            }
        }
        item["seasons"] = json!(output);
        Ok(item)
    }
    pub async fn framex_streams(
        &mut self,
        external: &str,
        season: Option<u32>,
        episode: u32,
    ) -> Result<Vec<Value>> {
        if self.site != Site::Framex {
            return Err(invalid());
        }
        let (kind, id) = identity(external)?;
        let suffix = framex_stream_suffix(kind, season, episode)?;
        let embed = match season {
            Some(s) => format!("embed/tv/{id}/{s}/{episode}"),
            None => format!("embed/movie/{id}"),
        };
        let site = if self
            .base
            .host_str()
            .is_some_and(|h| h.starts_with("api.framextv."))
        {
            Url::parse("https://framextv.xyz/").map_err(|_| invalid())?
        } else {
            self.base.clone()
        };
        let web = super::site_player(site.join(&embed).map_err(|_| invalid())?);
        let v = match self
            .framex_json(&format!("api/stream?type={kind}&id={id}{suffix}"))
            .await
        {
            Ok(v) => v,
            Err(e) if e.kind == "parse_or_storage_error" => return Ok(vec![web]),
            Err(e) => return Err(e),
        };
        let body = v.get("detail").filter(|v| v.is_object()).unwrap_or(&v);
        let mut common = subtitles(body);
        if common.is_empty() {
            if let Ok(tracks) = self
                .framex_json(&format!("api/subtitles?type={kind}&id={id}{suffix}"))
                .await
            {
                common = subtitles(&tracks);
            }
        }
        let mut options = Vec::new();
        let mut seen = HashSet::new();
        for row in body["sources"].as_array().into_iter().flatten().take(100) {
            let Some(url) = row["url"]
                .as_str()
                .and_then(|s| Url::parse(s).ok())
                .filter(|u| u.scheme() == "https" && umd_core::public_url::web_url(u))
            else {
                continue;
            };
            let hls = matches!(row["type"].as_str(), Some("hls" | "m3u8"))
                || url.path().ends_with(".m3u8");
            if !seen.insert(url.clone()) {
                continue;
            }
            let dash = matches!(row["type"].as_str(), Some("dash" | "mpd"))
                || url.path().ends_with(".mpd");
            if !dash && !hls && row["type"] != "mp4" && !url.path().ends_with(".mp4") {
                continue;
            }
            let mut track_urls = HashSet::new();
            let tracks: Vec<_> = subtitles(row)
                .into_iter()
                .chain(common.iter().cloned())
                .filter(|t| {
                    t["url"]
                        .as_str()
                        .is_some_and(|u| track_urls.insert(u.to_owned()))
                })
                .take(100)
                .collect();
            options.push(json!({"url":url.to_string(),"dash":dash,"tracks":tracks,"hls":hls,"label":format!("{} · {}",row["server"].as_str().unwrap_or("FramexTV"),row["quality"].as_str().unwrap_or("Auto"))}));
        }
        options.push(web);
        Ok(options)
    }
}

fn subtitles(v: &Value) -> Vec<Value> {
    v["subtitles"].as_array().into_iter().flatten().take(100).filter_map(|r|{
        let url=Url::parse(r["url"].as_str()?).ok().filter(|u|u.scheme()=="https"&&umd_core::public_url::web_url(u))?;
        let language=r["lang"].as_str().or(r["language"].as_str()).filter(|s|s.len()<=80).unwrap_or("und");
        let label=r["label"].as_str().unwrap_or(language).chars().take(200).collect::<String>();
        Some(json!({"url":url.to_string(),"language":language,"label":label,"default":r["isDefault"]==true}))
    }).collect()
}
