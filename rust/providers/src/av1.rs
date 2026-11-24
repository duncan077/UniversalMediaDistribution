use super::*;

// devalue stores object fields and array members as indexes. Resolve only the
// fields used by UMD, so cyclic or shared references never expand recursively.
#[derive(Clone, Copy)]
struct Node<'a> {
    table: &'a [Value],
    value: &'a Value,
}
impl<'a> Node<'a> {
    fn root(v: &'a Value, key: &str) -> Result<Self> {
        for node in rows(&v["nodes"])? {
            if let Some(table) = node["data"].as_array().filter(|t| t.len() <= 200000) {
                if let Some(first) = table.first().filter(|first| first.get(key).is_some()) {
                    return Ok(Self {
                        table,
                        value: first,
                    });
                }
            }
        }
        Err(invalid())
    }
    fn at(self, reference: &Value) -> Result<Self> {
        let value = match reference.as_i64() {
            Some(-1) => &Value::Null,
            Some(n) if n >= 0 => self.table.get(n as usize).ok_or_else(invalid)?,
            _ => return Err(invalid()),
        };
        Ok(Self { value, ..self })
    }
    fn field(self, key: &str) -> Result<Self> {
        match self.value.get(key) {
            Some(v) => self.at(v),
            None => Ok(Self {
                value: &Value::Null,
                ..self
            }),
        }
    }
    fn field_text(self, key: &str) -> Result<Option<String>> {
        Ok(string(self.field(key)?.value))
    }
    fn items(self) -> impl Iterator<Item = Result<Self>> + 'a {
        self.value
            .as_array()
            .into_iter()
            .flatten()
            .map(move |r| self.at(r))
    }
    fn props(self) -> impl Iterator<Item = Result<(&'a str, Self)>> + 'a {
        self.value
            .as_object()
            .into_iter()
            .flatten()
            .map(move |(k, r)| Ok((k.as_str(), self.at(r)?)))
    }
}

fn slug(s: &str) -> bool {
    segment(s, 250)
        && s.bytes()
            .all(|b| b.is_ascii_lowercase() || b.is_ascii_digit() || b == b'-')
}

fn av1_artwork(id: Option<&str>, folder: &str, suffix: &str) -> Option<String> {
    let numeric_id = id?.trim().parse::<u64>().ok().filter(|n| *n > 0)?;
    let cdn = Url::parse("https://cdn.animeav1.com/").ok()?;
    artwork_url(&cdn, Some(&format!("{folder}/{numeric_id}{suffix}.jpg")))
}

fn map(base: &Url, media: Node<'_>) -> Result<Entry> {
    let external_id = media
        .field_text("slug")?
        .filter(|s| slug(s))
        .ok_or_else(invalid)?;
    let title = media.field_text("title")?.ok_or_else(invalid)?;
    let media_id = media.field_text("id")?;
    let cat = media.field("category")?;
    let aka = media.field("aka")?;
    let mut categories = vec!["Anime".to_owned()];
    categories.extend(cat.field_text("name")?);
    let mut aliases = Vec::new();
    for prop in aka.props() {
        let (lang, v) = prop?;
        if let Some(name) = string(v.value) {
            aliases.push(json!({"name":name,"language":lang,"aliasType":"other"}))
        }
    }
    let mut genres = Vec::new();
    for g in media.field("genres")?.items() {
        genres.extend(g?.field_text("name")?)
    }
    let thumb = av1_artwork(media_id.as_deref(), "covers", "").or_else(|| {
        artwork_url(
            base,
            media
                .field_text("poster")
                .ok()
                .flatten()
                .or_else(|| media.field_text("cover").ok().flatten())
                .as_deref(),
        )
    });
    let backdrop = av1_artwork(media_id.as_deref(), "backdrops", "")
        .or_else(|| artwork_url(base, media.field_text("backdrop").ok().flatten().as_deref()));
    Ok(Entry {
        external_id,
        preview: json!({
            "name": title,
            "mediaType": "movie",
            "subtype": if integer(cat.field("id")?.value) == Some(2) { "anime_movie" } else { "anime_series" },
            "originalName": aka.field_text("ja-jp")?,
            "description": media.field_text("synopsis")?,
            "thumbnailRef": thumb,
            "backdropRef": backdrop,
            "alternativeNames": aliases,
            "releaseDate": date(media.field("startDate")?.value),
            "categories": categories,
            "genres": genres
        }),
    })
}

fn parse_number(s: &str) -> Result<(f64, String)> {
    let n = canonical_number(s);
    if n.is_empty() || n.len() > 16 || !n.bytes().all(|b| b.is_ascii_digit() || b == b'.') {
        return Err(invalid());
    }
    let val = n
        .parse::<f64>()
        .ok()
        .filter(|v| v.is_finite() && (0.0..=100000.).contains(v))
        .ok_or_else(invalid)?;
    Ok((val, n))
}

impl Provider {
    pub(super) async fn av1_page(&mut self, page: u32, query: Option<&str>) -> Result<Page> {
        let mut route = format!("catalogo/__data.json?page={page}");
        if let Some(q) = query {
            route.push_str("&search=");
            route.extend(url::form_urlencoded::byte_serialize(q.as_bytes()))
        }
        let v = self.json(&route, 4).await?;
        let data = Node::root(&v, "results")?;
        let items = data
            .field("results")?
            .items()
            .map(|m| map(&self.base, m?))
            .collect::<Result<Vec<_>>>()?;
        let pagination = data.field("pagination")?;
        let total = integer(pagination.field("totalPages")?.value)
            .filter(|v| *v > 0)
            .or_else(|| {
                integer(pagination.field("totalRecords").ok()?.value)?
                    .checked_add(19)
                    .map(|n| n / 20)
            })
            .ok_or_else(invalid)?;
        let next = (i64::from(page) < total).then(|| (page + 1).to_string());
        if query.is_some() {
            Ok(Page { items, next })
        } else {
            Page::directory(items, next)
        }
    }
    pub(super) async fn av1_updates(&mut self) -> Result<Vec<Entry>> {
        let v = self.json("__data.json", 4).await?;
        let mut items = vec![];
        for e in Node::root(&v, "latestEpisodes")?
            .field("latestEpisodes")?
            .items()
            .take(100)
        {
            items.push(map(&self.base, e?.field("media")?)?)
        }
        Ok(dedup(items).into_iter().take(24).collect())
    }
    pub(super) async fn av1_detail(&mut self, e: &Entry) -> Result<Value> {
        if !slug(&e.external_id) {
            return Err(invalid());
        }
        let v = self
            .json(&format!("media/{}/__data.json", e.external_id), 4)
            .await?;
        let media = Node::root(&v, "media")?.field("media")?;
        let media_id = media.field_text("id")?;
        let title = map(&self.base, media)?;
        if title.external_id != e.external_id {
            return Err(invalid());
        }
        let mut item = title.preview;
        let mut eps = Vec::new();
        let mut seen = HashSet::new();
        for raw in media.field("episodes")?.items() {
            let ep = raw?;
            let (ord, n) = parse_number(&ep.field_text("number")?.ok_or_else(invalid)?)?;
            if seen.insert(n.clone()) {
                eps.push((ord, n, ep.field_text("title")?));
            }
            if eps.len() > 20000 {
                return Err(invalid());
            }
        }
        eps.sort_by(|a, b| a.0.total_cmp(&b.0).then_with(|| a.1.cmp(&b.1)));
        item["units"] = json!(eps
            .iter()
            .enumerate()
            .map(|(i, (_, n, t))| {
                let mut u = episode(n, i, t.as_deref());
                if let Some(thumb) =
                    av1_artwork(media_id.as_deref(), "screenshots", &format!("/{n}"))
                {
                    u["thumbnailRef"] = json!(thumb);
                }
                u
            })
            .collect::<Vec<_>>());
        Ok(item)
    }
    pub async fn av1_streams(&mut self, id: &str, n: &str) -> Result<Vec<Value>> {
        if self.site != Site::AnimeAv1 || !slug(id) {
            return Err(invalid());
        }
        let (_, n) = parse_number(n)?;
        let v = self.json(&format!("media/{id}/{n}/__data.json"), 4).await?;
        let data = Node::root(&v, "embeds")?;
        if data.field("media")?.field_text("slug")?.as_deref() != Some(id)
            || parse_number(
                &data
                    .field("episode")?
                    .field_text("number")?
                    .ok_or_else(invalid)?,
            )?
            .1 != n
        {
            return Err(invalid());
        }
        let mut out = vec![];
        let mut seen = HashSet::new();
        for lang in data.field("embeds")?.props().take(10) {
            let (lang, list) = lang?;
            for r in list.items().take(20) {
                let row = r?;
                let Some(u) = row
                    .field_text("url")?
                    .and_then(|s| Url::parse(&s).ok())
                    .filter(|u| {
                        u.scheme() == "https"
                            && umd_core::public_url::web_url(u)
                            && !u.path().contains("embed-undef")
                    })
                else {
                    continue;
                };
                if seen.insert((lang, u.clone())) {
                    let player_page = super::streamtape_player_page(&u);
                    let hls = !player_page && u.path().ends_with(".m3u8");
                    let embed = player_page || (!hls && !u.path().ends_with(".mp4"));
                    out.push(json!({"url":u.to_string(),"hls":hls,"embed":embed,"label":format!("{} · {lang}",row.field_text("server")?.unwrap_or_else(||u.host_str().unwrap_or("").into()))}))
                }
            }
        }
        if out.is_empty() {
            return Err(Failure::new("unsupported"));
        }
        Ok(out)
    }
}
