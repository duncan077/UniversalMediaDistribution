use crate::{work, CommandResult};
use reqwest::{Client, Url};
use serde::Serialize;
use serde_json::{json, Value};
use std::{collections::HashSet, time::Duration};
use tauri::{Manager, State};
use umd_core::{CatalogItem, CatalogStore};
use uuid::Uuid;

#[derive(serde::Serialize, serde::Deserialize, Default)]
struct Preferences {
    tmdb: String,
    mal: String,
    #[serde(default)]
    language: Option<String>,
}
#[tauri::command]
pub async fn metadata_preferences(
    profile_id: Uuid,
    app: tauri::AppHandle,
    tmdb: Option<String>,
    mal: Option<String>,
    language: Option<String>,
) -> CommandResult<Value> {
    work(move || {
        metadata_preferences_io(
            profile_id,
            &app.state::<crate::CatalogReads>().0.get(),
            &app.state::<crate::private_files::PrivateFiles>(),
            tmdb,
            mal,
            language,
        )
    }).await
}

fn metadata_preferences_io(
    profile_id: Uuid,
    store: &CatalogStore,
    files: &crate::private_files::PrivateFiles,
    tmdb: Option<String>,
    mal: Option<String>,
    language: Option<String>,
) -> CommandResult<Value> {
    store.for_profile(profile_id).map_err(|e| e.to_string())?;
    static UPDATE: std::sync::Mutex<()> = std::sync::Mutex::new(());
    let _guard = UPDATE.lock().map_err(|_| "Configuración no disponible.")?;
    let mut prefs = files
        .read::<Preferences>(profile_id, "metadata")?
        .unwrap_or_default();
    if tmdb.is_some() || mal.is_some() || language.is_some() {
        if let Some(lang) = language {
            if !lang.is_empty()
                && (lang.len() > 35 || !lang.split('-').all(|part| {
                    !part.is_empty() && part.len() <= 8 && part.bytes().all(|c| c.is_ascii_alphanumeric())
                }))
            {
                return Err("Idioma no compatible.".into());
            }
            prefs.language = Some(lang);
        }
        for (target, value) in [(&mut prefs.tmdb, tmdb), (&mut prefs.mal, mal)] {
            if let Some(v) = value {
                if v.len() > 4096 || v.chars().any(char::is_control) {
                    return Err("Credencial inválida.".into());
                }
                *target = v.trim().to_owned();
            }
        }
        files.save(profile_id, "metadata", &prefs)?;
    }
    Ok(json!({"tmdb":!prefs.tmdb.is_empty(),"mal":!prefs.mal.is_empty(),"language":prefs.language}))
}
#[derive(Clone, Copy, PartialEq)]
enum Api {
    TmdbMovie,
    TmdbSeries,
    MalAnime,
    MalManga,
}
impl Api {
    fn for_title(item: &CatalogItem) -> CommandResult<Self> {
        let code = item.subtype.rsplit('.').next().unwrap_or("").to_lowercase();
        match (item.media_type.to_lowercase().as_str(), code.as_str()) {
            ("movie", "anime_series" | "anime_movie") => Ok(Self::MalAnime),
            ("book", "manga" | "web_novel") => Ok(Self::MalManga),
            ("movie", "series") => Ok(Self::TmdbSeries),
            ("movie", _) => Ok(Self::TmdbMovie),
            _ => Err("Este tipo de título no tiene proveedor de metadatos.".into()),
        }
    }
    fn spec(self) -> (&'static str, &'static str, &'static str) {
        match self {
            Self::TmdbMovie => ("https://api.themoviedb.org/3/", "movie", "UMD_TMDB_API_KEY"),
            Self::TmdbSeries => ("https://api.themoviedb.org/3/", "tv", "UMD_TMDB_API_KEY"),
            Self::MalAnime => (
                "https://api.myanimelist.net/v2/",
                "anime",
                "UMD_MAL_CLIENT_ID",
            ),
            Self::MalManga => (
                "https://api.myanimelist.net/v2/",
                "manga",
                "UMD_MAL_CLIENT_ID",
            ),
        }
    }
    fn mal(self) -> bool {
        matches!(self, Self::MalAnime | Self::MalManga)
    }
    async fn request(
        self,
        credential: &str,
        language: &str,
        route: &str,
        params: &[(&str, &str)],
    ) -> CommandResult<Value> {
        let (root, _, var) = self.spec();
        let key = if credential.trim().is_empty() {
            std::env::var(var).unwrap_or_default()
        } else {
            credential.trim().into()
        };
        if key.is_empty() {
            return Err(format!(
                "Ingresá la credencial del proveedor o configurá {var}."
            ));
        }
        let client = crate::direct_client().map_err(|_| "No se pudo iniciar la consulta.")?;
        let url = Url::parse(root)
            .and_then(|u| u.join(route))
            .map_err(|_| "Ruta de metadatos inválida.")?;
        self.request_url(client, url, &key, language, params).await
    }
    async fn request_url(
        self,
        client: Client,
        url: Url,
        key: &str,
        language: &str,
        params: &[(&str, &str)],
    ) -> CommandResult<Value> {
        let req = client
            .get(url)
            .timeout(Duration::from_secs(20))
            .query(params);
        let req = if self.mal() {
            req.header("X-MAL-CLIENT-ID", key)
        } else if key.contains('.') {
            req.bearer_auth(key).query(&[("language", language)])
        } else {
            req.query(&[("api_key", key), ("language", language)])
        };
        let mut res = req
            .send()
            .await
            .map_err(|_| "No se pudo conectar con el proveedor de metadatos.")?;
        if !res.status().is_success() {
            return Err(format!(
                "El proveedor de metadatos respondió HTTP {}.",
                res.status().as_u16()
            ));
        }
        let mut bytes = Vec::new();
        while let Some(chunk) = res
            .chunk()
            .await
            .map_err(|_| "La respuesta quedó incompleta.")?
        {
            if bytes.len() + chunk.len() > 2 << 20 {
                return Err("La respuesta de metadatos supera el límite.".into());
            }
            bytes.extend_from_slice(&chunk);
        }
        serde_json::from_slice(&bytes).map_err(|_| "Respuesta de metadatos inválida.".into())
    }
}
fn text<'a>(row: &'a Value, keys: &[&str]) -> Option<&'a str> {
    keys.iter()
        .find_map(|k| row[*k].as_str().map(str::trim).filter(|s| !s.is_empty()))
}
fn date(row: &Value) -> Option<&str> {
    text(row, &["release_date", "first_air_date", "start_date"])
}
fn compatible(api: Api, item: &CatalogItem, row: &Value) -> bool {
    let format = row["media_type"].as_str().unwrap_or("");
    match api {
        Api::MalAnime => {
            format.is_empty() || (item.subtype.ends_with("anime_movie") == (format == "movie"))
        }
        Api::MalManga if item.subtype.ends_with("web_novel") => {
            matches!(format, "" | "novel" | "light_novel")
        }
        Api::MalManga => !matches!(format, "novel" | "light_novel"),
        _ => true,
    }
}
async fn title(store: &CatalogStore, id: Uuid) -> CommandResult<CatalogItem> {
    let store = store.clone();
    work(move || {
        store
            .item(id)
            .map_err(|e| e.to_string())?
            .and_then(|doc| doc.items.into_iter().next())
            .ok_or("Título inexistente.".into())
    })
    .await
}
#[derive(Serialize)]
pub struct Hit {
    id: u64,
    name: String,
    year: Option<i32>,
}
#[tauri::command]
pub async fn metadata_search(
    profile_id: Option<Uuid>,
    files: State<'_, crate::private_files::PrivateFiles>,
    id: Uuid,
    credential: String,
    language: Option<String>,
    query: String,
    store: State<'_, CatalogStore>,
) -> CommandResult<Vec<Hit>> {
    let prefs = profile_id
        .map(|id| files.read::<Preferences>(id, "metadata"))
        .transpose()?
        .flatten()
        .unwrap_or_default();
    let language = prefs
        .language
        .as_deref()
        .filter(|s| !s.is_empty())
        .or(language.as_deref())
        .filter(|s| {
            s.len() <= 35
                && !s.is_empty()
                && s.bytes().all(|c| c.is_ascii_alphanumeric() || c == b'-')
        })
        .unwrap_or("es-AR");
    let item = title(&store, id).await?;
    let api = Api::for_title(&item)?;
    let credential = if credential.is_empty() {
        if api.mal() {
            prefs.mal
        } else {
            prefs.tmdb
        }
    } else {
        credential
    };
    let (_, resource, _) = api.spec();
    let q = query.trim();
    if q.is_empty() || q.len() > 512 {
        return Err("Ingresá un título para buscar.".into());
    }
    let result = if api.mal() {
        api.request(
            &credential,
            language,
            resource,
            &[
                ("q", q),
                ("limit", "15"),
                ("fields", "title,start_date,media_type"),
            ],
        )
        .await?
    } else {
        api.request(
            &credential,
            language,
            &format!("search/{resource}"),
            &[("query", q)],
        )
        .await?
    };
    let list = result[if api.mal() { "data" } else { "results" }]
        .as_array()
        .ok_or("El proveedor omitió los resultados.")?;
    Ok(list
        .iter()
        .filter_map(|v| {
            let r = v.get("node").unwrap_or(v);
            if !compatible(api, &item, r) {
                return None;
            }
            Some(Hit {
                id: r["id"].as_u64()?,
                name: text(r, &["title", "name"])?.into(),
                year: date(r).and_then(|s| s.get(..4)?.parse().ok()),
            })
        })
        .collect())
}
#[tauri::command]
pub async fn metadata_apply(
    profile_id: Option<Uuid>,
    files: State<'_, crate::private_files::PrivateFiles>,
    id: Uuid,
    external: u64,
    credential: String,
    language: Option<String>,
    store: State<'_, CatalogStore>,
) -> CommandResult<CatalogItem> {
    let prefs = profile_id
        .map(|id| files.read::<Preferences>(id, "metadata"))
        .transpose()?
        .flatten()
        .unwrap_or_default();
    let language = prefs
        .language
        .as_deref()
        .filter(|s| !s.is_empty())
        .or(language.as_deref())
        .filter(|s| {
            s.len() <= 35
                && !s.is_empty()
                && s.bytes().all(|c| c.is_ascii_alphanumeric() || c == b'-')
        })
        .unwrap_or("es-AR");
    let item = title(&store, id).await?;
    let api = Api::for_title(&item)?;
    let credential = if credential.is_empty() {
        if api.mal() {
            prefs.mal
        } else {
            prefs.tmdb
        }
    } else {
        credential
    };
    let (_, resource, _) = api.spec();
    let fields = if api.mal() {
        vec![(
            "fields",
            "title,main_picture,alternative_titles,synopsis,start_date,media_type,genres",
        )]
    } else {
        vec![]
    };
    let row = api
        .request(
            &credential,
            language,
            &format!("{resource}/{external}"),
            &fields,
        )
        .await?;
    if row["id"].as_u64() != Some(external) || !compatible(api, &item, &row) {
        return Err("La edición no corresponde al tipo de título.".into());
    }
    let mut patch = serde_json::Map::new();
    for (k, v) in [
        ("description", text(&row, &["overview", "synopsis"])),
        (
            "originalName",
            text(&row, &["original_title", "original_name", "title"]),
        ),
        (
            "releaseDate",
            date(&row).filter(|d| chrono::NaiveDate::parse_from_str(d, "%Y-%m-%d").is_ok()),
        ),
    ] {
        if let Some(s) = v {
            patch.insert(k.into(), json!(s));
        }
    }
    if let Some(y) = date(&row).and_then(|s| s.get(..4)?.parse::<u16>().ok()) {
        patch.insert("releaseYear".into(), json!(y));
    }
    let mut seen = HashSet::new();
    let mut aliases = vec![];
    for name in [
        text(&row, &["title", "name"]),
        text(&row["alternative_titles"], &["en"]),
        text(&row["alternative_titles"], &["ja"]),
    ]
    .into_iter()
    .flatten()
    .chain(
        row["alternative_titles"]["synonyms"]
            .as_array()
            .into_iter()
            .flatten()
            .filter_map(Value::as_str),
    ) {
        if !name.trim().is_empty() && name != item.name && seen.insert(umd_core::normalize(name)) {
            aliases.push(json!({"name":name,"aliasType":"other"}))
        }
    }
    if !aliases.is_empty() {
        patch.insert("alternativeNames".into(), json!(aliases));
    }
    let genres: Vec<_> = row["genres"]
        .as_array()
        .into_iter()
        .flatten()
        .filter_map(|v| text(v, &["name"]))
        .collect();
    if !genres.is_empty() {
        patch.insert("genres".into(), json!(genres));
    }
    let cover = if api.mal() {
        text(&row["main_picture"], &["large", "medium"])
            .and_then(|s| Url::parse(s).ok())
            .filter(|u| {
                u.scheme() == "https"
                    && u.host_str() == Some("cdn.myanimelist.net")
                    && umd_core::public_url::web_url(u)
            })
            .map(String::from)
    } else {
        text(&row, &["poster_path"])
            .and_then(|s| s.strip_prefix('/'))
            .filter(|s| {
                !s.is_empty()
                    && s.bytes()
                        .all(|c| c.is_ascii_alphanumeric() || b"._-".contains(&c))
            })
            .map(|f| format!("https://image.tmdb.org/t/p/w500/{f}"))
    };
    if let Some(cover) = cover {
        patch.insert("thumbnailRef".into(), json!(cover));
    }
    let store = store.inner().clone();
    work(move || {
        store
            .update_title_metadata(id, &item.key(), patch)
            .map_err(|e| e.to_string())
    })
    .await
}

#[cfg(test)]
mod parity_tests {
    use super::*;
    use axum::{extract::Query, http::HeaderMap, routing::get, Json, Router};
    #[tokio::test]
    async fn metadata_uses_the_chosen_language_and_provider_credentials() {
        async fn mock(
            h: HeaderMap,
            Query(q): Query<std::collections::HashMap<String, String>>,
        ) -> Json<Value> {
            if h.contains_key("x-mal-client-id") {
                assert_eq!(h["x-mal-client-id"], "fixture-mal");
                assert!(!q.contains_key("language"));
                return Json(
                    serde_json::json!({"data":[{"node":{"id":2,"title":"Fixture manga"}}]}),
                );
            }
            assert!(
                q.get("api_key").is_some_and(|s| s == "fixture-tmdb")
                    || h.get("authorization")
                        .is_some_and(|s| s == "Bearer fixture.bearer")
            );
            Json(
                serde_json::json!({"results":[{"id":1,"title":if q["language"]=="en-GB"{"English title"}else{"Título en español"}}]}),
            )
        }
        let server = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let url = Url::parse(&format!("http://{}/metadata", server.local_addr().unwrap())).unwrap();
        let task = tokio::spawn(async move {
            axum::serve(server, Router::new().route("/metadata", get(mock)))
                .await
                .unwrap();
        });
        for (key, language, title) in [
            ("fixture-tmdb", "en-GB", "English title"),
            ("fixture.bearer", "es-AR", "Título en español"),
        ] {
            let response = Api::TmdbMovie
                .request_url(
                    Client::new(),
                    url.clone(),
                    key,
                    language,
                    &[("query", "movie")],
                )
                .await
                .unwrap();
            assert_eq!(response["results"][0]["title"], title);
        }
        let response = Api::MalManga
            .request_url(
                Client::new(),
                url,
                "fixture-mal",
                "en-GB",
                &[("q", "manga")],
            )
            .await
            .unwrap();
        assert_eq!(response["data"][0]["node"]["title"], "Fixture manga");
        task.abort();
    }
}
