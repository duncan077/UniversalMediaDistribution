use super::*;
use dom_query::Document;
use regex::Regex;
use std::{collections::BTreeMap, sync::LazyLock};

static URL: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r#"https://[a-zA-Z0-9.-]+(?::[0-9]{1,5})?"#).unwrap());
static ASSIGN: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r#"([a-zA-Z_$][a-zA-Z0-9_$]*)\s*=\s*["'](https://[A-Za-z0-9.-]+(?::[0-9]+)?(?:/[a-zA-Z0-9/_-]*)?)["']"#).unwrap()
});

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn explicit_format_survives_a_detail_response_without_tipo() {
        let base = Url::parse("https://manhwa.example/").unwrap();
        let entry = map_item(
            &base,
            &json!({"name_esp":"Obra","_tipo":"Novela web"}),
            "obra",
            None,
        )
        .unwrap();
        let detail = map_item(
            &base,
            &json!({"name_esp":"Obra"}),
            "obra",
            entry.preview["subtype"].as_str(),
        )
        .unwrap();
        assert_eq!(detail.preview["subtype"], "web_novel");
        assert_eq!(detail.preview["categories"], json!([]));
        let comic = map_item(
            &base,
            &json!({"name_esp":"Obra","_tipo":"Cómic"}),
            "obra-2",
            None,
        )
        .unwrap();
        assert_eq!(comic.preview["subtype"], "comic");
    }
}

fn identifier(s: &str) -> bool {
    s.len() <= 500
        && s.chars().next().is_some_and(char::is_alphanumeric)
        && s.chars()
            .all(|c| c.is_alphanumeric() || matches!(c, '.' | '_' | '-'))
}

fn label(v: &Value) -> Option<String> {
    string(v).filter(|s| !matches!(s.to_lowercase().as_str(), "no" | "null" | "n/a"))
}

fn is_erotic_flag(v: &Value) -> bool {
    match v {
        Value::Bool(b) => *b,
        Value::Number(n) => n.as_i64() == Some(1),
        Value::String(s) => matches!(
            umd_core::normalize(s).as_str(),
            "si" | "yes"
                | "true"
                | "1"
                | "erotico"
                | "adulto"
                | "adult"
                | "+18"
                | "18+"
                | "nsfw"
                | "hentai"
                | "smut"
                | "porno"
                | "porn"
        ),
        _ => false,
    }
}

fn is_adult_item(v: &Value) -> bool {
    ["_erotico", "erotico", "nsfw", "demografia", "_demografi"]
        .iter()
        .any(|k| is_erotic_flag(&v[*k]))
}

pub(super) fn map_item(base: &Url, v: &Value, id: &str, fallback: Option<&str>) -> Option<Entry> {
    if !identifier(id) {
        return None;
    }
    let names: Vec<_> = ["name_esp", "the_real_name", "name_raw"]
        .into_iter()
        .filter_map(|k| label(&v[k]))
        .collect();
    let name = names.first()?;
    let kind = string(&v["_tipo"]).unwrap_or_default();
    let sub = umd_core::provider_book_subtype(&kind)
        .or_else(|| fallback.and_then(umd_core::provider_book_subtype));
    let mut seen = HashSet::from([umd_core::normalize(name)]);
    let aliases: Vec<_> = names
        .iter()
        .filter(|n| seen.insert(umd_core::normalize(n)))
        .map(|n| json!({"name":n,"aliasType":"other"}))
        .collect();
    let mut seen_genres = HashSet::new();
    let mut genres: Vec<String> = ["_generos", "genres", "generos", "_generes", "_categoris"]
        .iter()
        .flat_map(|k| v[*k].as_array().into_iter().flatten())
        .filter_map(|row| {
            label(&row["name"])
                .or_else(|| label(&row["nombre"]))
                .or_else(|| {
                    row.as_str()
                        .and_then(|s| s.parse::<i64>().is_err().then(|| s.trim().to_string()))
                        .filter(|s| !s.is_empty())
                })
        })
        .filter(|g| seen_genres.insert(umd_core::normalize(g)))
        .collect();
    let mut categories: Vec<String> = Vec::new();
    let mut seen_cats = HashSet::new();
    let mut push_cat = |c: String| {
        let trimmed = c.trim();
        if !trimmed.is_empty() && seen_cats.insert(umd_core::normalize(trimmed)) {
            categories.push(trimmed.to_string());
        }
    };
    if sub == Some("manga") {
        push_cat("Manga".into());
    }
    push_cat(kind);
    for demo_key in ["_demografi", "demografia"] {
        if let Some(demo) = label(&v[demo_key]) {
            push_cat(demo);
        }
    }
    if is_adult_item(v) {
        push_cat("Adulto".into());
        if seen_genres.insert(umd_core::normalize("Adulto")) {
            genres.push("Adulto".into());
        }
    }
    let thumb = artwork_url(
        base,
        v["_imagen"]
            .as_str()
            .or_else(|| v["img"].as_str())
            .or_else(|| v["imagen"].as_str()),
    );
    Some(Entry {
        external_id: id.into(),
        preview: json!({
            "name": name,
            "mediaType": "book",
            "subtype": sub,
            "description": v["_sinopsis"],
            "originalName": label(&v["the_real_name"]).filter(|s| s != name),
            "thumbnailRef": thumb,
            "alternativeNames": aliases,
            "genres": genres,
            "categories": categories,
            "publicationStatus": v["_status"]
        }),
    })
}

pub(super) fn select_app_script(base: &Url, doc: &Document) -> Option<Url> {
    let mut fallback = None;
    for s in doc.select("script[src]").iter() {
        let Some(src) = s.attr("src") else {
            continue;
        };
        let Some(u) = base.join(&src).ok().filter(|u| {
            u.origin() == base.origin()
                && u.path().starts_with("/assets/")
                && u.path().ends_with(".js")
        }) else {
            continue;
        };
        if u.path().starts_with("/assets/index-") {
            return Some(u);
        }
        if fallback.is_none() {
            fallback = Some(u);
        }
    }
    fallback
}

fn chapter_routes(base: &Url, v: &Value) -> Result<BTreeMap<String, (String, i64, String)>> {
    let mut chapters = BTreeMap::new();
    for e in ["chapters", "chapters_esp", "chapters_raw", "_chapters"]
        .iter()
        .flat_map(|k| v[*k].as_array().into_iter().flatten())
    {
        let Some(n) = string(&e["chapter"]) else {
            continue;
        };
        let key = canonical_number(&n);
        if key.is_empty() {
            continue;
        }
        let Some(url) = e["link"]
            .as_str()
            .and_then(|s| base.join(s).ok())
            .filter(|u| u.origin() == base.origin())
        else {
            continue;
        };
        let Some(route) = url
            .path()
            .trim_end_matches('/')
            .strip_prefix("/leer/")
            .filter(|s| identifier(s))
        else {
            continue;
        };
        let time = integer(&e["create"]).unwrap_or(0);
        let entry = chapters
            .entry(key)
            .or_insert((n.replace(',', "."), time, route.to_string()));
        if time > entry.1 {
            *entry = (n.replace(',', "."), time, route.to_string());
        }
        if chapters.len() > 20000 {
            return Err(invalid());
        }
    }
    Ok(chapters)
}

impl Provider {
    pub(super) async fn prepare_manhwa_api(&mut self) -> Result<()> {
        if self.api.is_none() {
            let path = {
                let doc = Document::from(self.html("").await?);
                select_app_script(&self.base, &doc).ok_or_else(invalid)?
            };
            let js = String::from_utf8(
                self.bytes(path.as_str(), "application/javascript", 8 << 20)
                    .await?,
            )
            .map_err(|_| invalid())?;
            self.filter_genres = Some(super::filtered::script_genres(&js));
            let mut choices = Vec::new();
            for m in ASSIGN.captures_iter(&js) {
                let var = &m[1];
                if ["manhwa/library", "manhwa/see", "chapters/see"]
                    .iter()
                    .any(|endpoint| {
                        js.contains(&format!("\u{24}{{{var}}}/{endpoint}"))
                            || js.contains(&format!("{var}+\"/{endpoint}"))
                    })
                {
                    choices.push((200, m[2].to_string()))
                }
            }
            for addr in URL.find_iter(&js) {
                let Some(host) = Url::parse(addr.as_str())
                    .ok()
                    .and_then(|u| u.host_str().map(str::to_owned))
                else {
                    continue;
                };
                if host.contains("manhwawebbackend") {
                    choices.push((100, addr.as_str().into()))
                }
            }
            let (_, addr) = choices.into_iter().max().ok_or_else(invalid)?;
            let url =
                Url::parse(&format!("{}/", addr.trim_end_matches('/'))).map_err(|_| invalid())?;
            if !umd_core::public_url::web_url(&url) {
                return Err(invalid());
            }
            let client = if url.origin() == self.base.origin() {
                self.client.clone()
            } else {
                pinned(&url, self.cookies.as_ref()).await?
            };
            self.api = Some((url, client))
        }
        Ok(())
    }
    async fn manhwa_api(&mut self, route: &str) -> Result<Value> {
        self.prepare_manhwa_api().await?;
        let (url, client) = self.api.as_ref().ok_or_else(invalid)?;
        let target = url.join(route).map_err(|_| invalid())?;
        if target.origin() != url.origin() {
            return Err(invalid());
        }
        let req = client
            .get(target)
            .header("referer", self.base.as_str())
            .header("origin", self.base.origin().ascii_serialization());
        self.send_json(req, 8 << 20).await
    }
    pub(super) async fn manhwa_page(&mut self, page: u32, q: Option<&str>) -> Result<Page> {
        self.manhwa_library(page, q, None).await
    }
    pub(super) async fn manhwa_library(&mut self, page: u32, q: Option<&str>, genre: Option<&str>) -> Result<Page> {
        let term: String =
            url::form_urlencoded::byte_serialize(q.unwrap_or("").as_bytes()).collect();
        let genre_token = genre.unwrap_or("");
        let v=self.manhwa_api(&format!("manhwa/library?buscar={term}&estado=&tipo=&erotico=&demografia=&order_item=alfabetico&order_dir=desc&page={page}&generes={genre_token}")).await?;
        let items = dedup(
            rows(&v["data"])?
                .iter()
                .filter_map(|v| map_item(&self.base, v, v["_id"].as_str()?, None)),
        );
        let filtered = q.is_some() || genre.is_some();
        let next = (v["next"] == true).then(|| (page + if filtered { 2 } else { 1 }).to_string());
        if q.is_some() || genre.is_some() {
            Ok(Page { items, next })
        } else {
            Page::directory(items, next)
        }
    }
    pub(super) async fn manhwa_updates(&mut self) -> Result<Vec<Entry>> {
        let v = self.manhwa_api("latest/new-manhwa").await?;
        Ok(
            dedup(rows(&v["manhwas"]["manhwas_esp"])?.iter().filter_map(|v| {
                let id = string(&v["id_rel"])
                    .or_else(|| string(&v["id_manhwa"]))
                    .or_else(|| string(&v["_id"]))?;
                let name = string(&v["name_manhwa"])?;
                map_item(
                    &self.base,
                    &json!({
                        "name_esp": name,
                        "img": v["img"],
                        "_tipo": v["_tipo"],
                        "_erotico": v["_erotico"],
                        "erotico": v["erotico"],
                        "_demografi": v["_demografi"]
                    }),
                    &id,
                    None,
                )
            }))
            .into_iter()
            .take(24)
            .collect(),
        )
    }
    async fn manhwa_title(&mut self, id: &str) -> Result<Value> {
        if !identifier(id) {
            return Err(invalid());
        }
        let v = self.manhwa_api(&format!("manhwa/see/{id}")).await?;
        Ok(v.get("data")
            .filter(|v| v.is_object())
            .cloned()
            .unwrap_or(v))
    }
    pub(super) async fn manhwa_detail(&mut self, e: &Entry) -> Result<Value> {
        let v = self.manhwa_title(&e.external_id).await?;
        let fallback = e.preview["subtype"].as_str().unwrap_or("manga");
        let mut item = map_item(&self.base, &v, &e.external_id, Some(fallback))
            .ok_or_else(invalid)?
            .preview;
        let mut chapters: Vec<_> = chapter_routes(&self.base, &v)?
            .into_values()
            .map(|(display, _, _)| display)
            .collect();
        chapters.sort_by(|a, b| {
            let num = |s: &str| {
                s.parse::<f64>()
                    .ok()
                    .filter(|v| v.is_finite())
                    .unwrap_or(f64::INFINITY)
            };
            num(a).total_cmp(&num(b)).then_with(|| a.cmp(b))
        });
        item["units"] = json!(chapters
            .iter()
            .enumerate()
            .map(|(i, n)| json!({"kind":"chapter","displayNumber":n,"sortOrder":i}))
            .collect::<Vec<_>>());
        Ok(item)
    }
    pub async fn manhwa_images(&mut self, id: &str, n: &str) -> Result<Vec<String>> {
        if self.site != Site::Manhwa {
            return Err(invalid());
        }
        let target = canonical_number(n);
        if target.is_empty() {
            return Err(invalid());
        }
        let v = self.manhwa_title(id).await?;
        if v["_tipo"]
            .as_str()
            .and_then(umd_core::provider_book_subtype)
            .is_some_and(|subtype| !matches!(subtype, "manga" | "comic"))
        {
            return Err(Failure::new("unsupported"));
        }
        let (_, _, route) = chapter_routes(&self.base, &v)?
            .remove(&target)
            .ok_or_else(invalid)?;
        let v = self.manhwa_api(&format!("chapters/see/{route}")).await?;
        let pages = rows(&v.get("data").unwrap_or(&v)["chapter"]["img"])?;
        if pages.is_empty() || pages.len() > 2000 {
            return Err(invalid());
        }
        pages
            .iter()
            .map(|p| {
                p.as_str()
                    .and_then(|s| Url::parse(s).ok())
                    .filter(|u| u.scheme() == "https" && umd_core::public_url::web_url(u))
                    .map(String::from)
                    .ok_or_else(invalid)
            })
            .collect()
    }
}
