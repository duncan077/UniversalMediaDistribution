use crate::{work, CommandResult};

use reqwest::Client;
#[cfg(test)]
use serde_json::json;
use serde_json::Value;
use std::{
    collections::{BTreeMap, HashSet},
    time::Duration,
};
use tauri::State;
use umd_core::capibara::*;
use umd_core::{CatalogDocument, CatalogItem, CatalogStore};
use uuid::Uuid;

const ROOT: &str = "https://capibaratraductor.com";
pub struct Capibara {
    root: String,
    client: Client,
}

impl Capibara {
    pub fn new() -> Result<Self, reqwest::Error> {
        let client = Client::builder()
            .timeout(Duration::from_secs(30))
            .redirect(reqwest::redirect::Policy::none())
            .build()?;
        Ok(Self {
            root: ROOT.into(),
            client,
        })
    }

    async fn envelope(
        &self,
        path: &str,
        query: &[(&str, &str)],
        scan: Option<&str>,
    ) -> CommandResult<Value> {
        let mut request = self
            .client
            .get(format!("{}/api/{path}", self.root))
            .query(query);
        if let Some(scan) = scan {
            request = request.header("x-organization", scan);
        }
        let mut response = request
            .send()
            .await
            .map_err(|e| format!("No se pudo consultar CapibaraTraductor. {e}"))?;
        let mut bytes = Vec::new();
        if matches!(response.status().as_u16(), 401 | 403) && scan.is_none() {
            let (app, id, origin) = crate::providers::verified("capibaratraductor")
                .ok_or("El proveedor denegó el acceso.")?;
            bytes =
                crate::ad_filter::verified_read(&app, id, &origin, response.url(), 4 * 1024 * 1024)
                    .await?;
        } else {
            if !response.status().is_success() {
                return Err(format!(
                    "CapibaraTraductor respondió HTTP {}.",
                    response.status()
                ));
            }
            while let Some(chunk) = response.chunk().await.map_err(|e| e.to_string())? {
                if bytes.len() + chunk.len() > 8 * 1024 * 1024 {
                    return Err("La respuesta del proveedor supera el límite de tamaño.".into());
                }
                bytes.extend_from_slice(&chunk);
            }
        }
        let value: Value = serde_json::from_slice(&bytes)
            .map_err(|_| "El proveedor devolvió una respuesta inválida.")?;
        if value["status"] != true || value["data"].is_null() {
            return Err("El contenido no está disponible en CapibaraTraductor.".into());
        }
        Ok(value)
    }

    async fn data(
        &self,
        path: &str,
        query: &[(&str, &str)],
        scan: Option<&str>,
    ) -> CommandResult<Value> {
        Ok(self.envelope(path, query, scan).await?["data"].take())
    }

    async fn joint_slug(&self, id: u32) -> CommandResult<String> {
        let mut page = 1u32;
        loop {
            let result = self
                .envelope(
                    "joint/list",
                    &[
                        ("limit", "50"),
                        ("nsfw", "false"),
                        ("page", &page.to_string()),
                    ],
                    None,
                )
                .await?;
            let rows = result["data"]
                .as_array()
                .ok_or("Lista de ediciones conjuntas inválida.")?;
            if let Some(slug) = rows
                .iter()
                .find(|r| r["id"].as_u64() == Some(u64::from(id)) && available_manga(r))
                .and_then(|r| r["slug"].as_str())
                .filter(|s| valid_slug(s))
            {
                return Ok(slug.to_owned());
            }
            if rows.is_empty()
                || u64::from(page) >= result["maxPage"].as_u64().unwrap_or(1)
                || page >= 2000
            {
                return Err("El proveedor ya no ofrece esa edición conjunta.".into());
            }
            page += 1;
        }
    }

    pub(super) async fn search(&self, query: &str, page: u32) -> CommandResult<SearchPage> {
        let query = query.trim();
        if query.is_empty() || query.chars().count() > 200 || page == 0 {
            return Err("Ingresá un título de hasta 200 caracteres y una página válida.".into());
        }
        let data = self
            .data(
                "manga-custom",
                &[
                    ("search", query),
                    ("page", &page.to_string()),
                    ("limit", "20"),
                    ("order", "latest"),
                    ("nsfw", "false"),
                ],
                None,
            )
            .await?;
        listing(&data, page)
    }

    async fn daily(&self) -> CommandResult<SearchPage> {
        let data = self
            .data(
                "manga-custom",
                &[
                    ("page", "1"),
                    ("limit", "100"),
                    ("order", "latest"),
                    ("nsfw", "false"),
                ],
                None,
            )
            .await?;
        listing(&data, 1)
    }
    pub(super) async fn edition_detail(&self, edition: &Edition) -> CommandResult<CatalogItem> {
        if !edition.valid() {
            return Err("La edición seleccionada es inválida.".into());
        }
        let row = if edition.joint {
            self.data(&format!("joint/{}", edition.slug), &[], None)
                .await?
        } else {
            self.data(
                &format!("manga-custom/{}", edition.slug),
                &[],
                Some(&edition.scan),
            )
            .await?
        };
        detail(edition, &row)
    }
    async fn add(
        &self,
        edition: Edition,
        store: CatalogStore,
        hide_adult: bool,
    ) -> CommandResult<CatalogItem> {
        let mut item = self.edition_detail(&edition).await?;
        if hide_adult && item.adult() {
            return Err("El filtro +18 ocultó esta ficha.".into());
        }
        item.metadata.insert(
            "capibaraEdition".into(),
            serde_json::to_value(&edition).map_err(|e| e.to_string())?,
        );
        work(move || {
            store
                .import(&CatalogDocument::new(vec![item.clone()]), false)
                .map_err(|e| e.to_string())?;
            Ok(item)
        })
        .await
    }

    async fn pages(
        &self,
        store: CatalogStore,
        id: Uuid,
        chapter: String,
    ) -> CommandResult<Vec<String>> {
        let number =
            chapter_number(&Value::String(chapter)).ok_or("Número de capítulo inválido.")?;
        let (edition, number) = work(move || {
            let doc = store
                .item(id)
                .map_err(|e| e.to_string())?
                .ok_or("El título ya no está en tu biblioteca.")?;
            let listed = doc
                .items
                .first()
                .and_then(|i| i.metadata.get("units"))
                .and_then(Value::as_array)
                .is_some_and(|units| {
                    units
                        .iter()
                        .any(|u| u["kind"] == "chapter" && u["displayNumber"] == number)
                });
            if !listed {
                return Err("Este capítulo no está disponible.".into());
            }
            let edition = doc.items[0]
                .metadata
                .get("onlineProviders")
                .and_then(|v| v["capibaratraductor"].as_str())
                .and_then(|s| s.parse().ok())
                .unwrap_or(id);
            Ok((edition, number))
        })
        .await?;
        self.edition_pages(edition, number).await
    }
    pub(super) async fn edition_pages(
        &self,
        id: Uuid,
        number: String,
    ) -> CommandResult<Vec<String>> {
        let external = edition_id(id).ok_or("El manga no está vinculado a CapibaraTraductor.")?;
        let data = if id.as_u128() & !u128::from(u32::MAX) == JOINT_PREFIX {
            let slug = self.joint_slug(external).await?;
            self.data(&format!("joint/{slug}/chapter/{number}/pages"), &[], None)
                .await?
        } else {
            let lookup = self
                .data(
                    "manga-custom",
                    &[
                        ("ids", &external.to_string()),
                        ("limit", "1"),
                        ("nsfw", "false"),
                    ],
                    None,
                )
                .await?;
            let edition = lookup["items"]
                .as_array()
                .into_iter()
                .flatten()
                .filter(|r| available_manga(r))
                .filter_map(Edition::read)
                .find(|e| !e.joint && e.id == external)
                .ok_or("El proveedor ya no ofrece la edición guardada.")?;
            self.data(
                &format!("manga-custom/{}/chapter/{number}/pages", edition.slug),
                &[],
                Some(&edition.scan),
            )
            .await?
        };
        let rows = data
            .as_array()
            .ok_or("El proveedor no devolvió una lista de páginas.")?;
        if rows.len() > 2000 {
            return Err("El capítulo supera las 2000 páginas.".into());
        }
        let mut pages = BTreeMap::new();
        for row in rows {
            let n = row["number"]
                .as_u64()
                .ok_or("El proveedor devolvió una página sin número.")?;
            let url = image_url(&row["imageUrl"])
                .ok_or("El proveedor devolvió una imagen incompatible.")?;
            pages.entry(n).or_insert(url);
        }
        let mut seen = HashSet::new();
        let urls: Vec<_> = pages
            .into_values()
            .filter(|url| seen.insert(url.clone()))
            .collect();
        if urls.is_empty() {
            return Err("El capítulo todavía no tiene páginas disponibles.".into());
        }
        Ok(urls)
    }
}

#[tauri::command]
pub async fn capibara_updates(provider: State<'_, Capibara>) -> CommandResult<SearchPage> {
    provider.daily().await
}
#[tauri::command]
pub async fn capibara_search(
    query: String,
    page: u32,
    provider: State<'_, Capibara>,
) -> CommandResult<SearchPage> {
    provider.search(&query, page).await
}

#[tauri::command]
pub async fn capibara_add(
    edition: Edition,
    hide_adult: Option<bool>,
    provider: State<'_, Capibara>,
    store: State<'_, CatalogStore>,
) -> CommandResult<CatalogItem> {
    provider
        .add(edition, store.inner().clone(), hide_adult.unwrap_or(false))
        .await
}

#[tauri::command]
pub fn capibara_linked(id: Uuid) -> bool {
    edition_id(id).is_some()
}

#[tauri::command]
pub async fn capibara_pages(
    id: Uuid,
    chapter: String,
    provider: State<'_, Capibara>,
    store: State<'_, CatalogStore>,
) -> CommandResult<Vec<String>> {
    provider.pages(store.inner().clone(), id, chapter).await
}

#[cfg(test)]
mod tests {
    use super::*;
    use axum::{extract::Query, http::HeaderMap, routing::get, Json, Router};
    use std::collections::HashMap;

    #[tokio::test]
    async fn search_import_refresh_and_read_after_transfer() {
        let title = json!({"id":1663,"manga":{"slug":"ao-to-haru"},
            "organization":{"slug":"kirishima","name":"Kirishima"},"title":"Ao to Haru",
            "chapters":[{"number":10},{"number":1.5},{"number":"1.50"},{"number":2},
                {"number":3,"releasedAt":"2100-01-01T00:00:00Z"},{"number":4,"isUnreleased":true}]});
        let catalog_row = title.clone();
        let app = Router::new()
            .route(
                "/api/manga-custom",
                get(move |Query(q): Query<HashMap<String, String>>| {
                    let row = catalog_row.clone();
                    async move {
                        assert_eq!(q["nsfw"], "false");
                        if q.contains_key("search") {
                            assert_eq!(q["search"], "Ao & Haru");
                            if q["page"] == "2" {
                                return Json(
                                    json!({"status":true,"data":{"maxPage":2,"items":[]}}),
                                );
                            }
                        } else {
                            assert_eq!(q["ids"], "1663");
                        }
                        let mut novel = row.clone();
                        novel["workType"] = json!("novel");
                        Json(json!({"status":true,"data":{"maxPage":2,"items":[novel,row]}}))
                    }
                }),
            )
            .route(
                "/api/manga-custom/ao-to-haru",
                get(move |headers: HeaderMap| {
                    let row = title.clone();
                    async move {
                        assert_eq!(headers["x-organization"], "kirishima");
                        Json(json!({"status":true,"data":row}))
                    }
                }),
            )
            .route(
                "/api/manga-custom/ao-to-haru/chapter/1.5/pages",
                get(|headers: HeaderMap| async move {
                    assert_eq!(headers["x-organization"], "kirishima");
                    Json(json!({"status":true,"data":[
                    {"number":2,"imageUrl":"https://r2.capibaratraductor.com/2.webp"},
                    {"number":1,"imageUrl":"https://r2.capibaratraductor.com/1.webp"}]}))
                }),
            )
            .route(
                "/api/manga-custom/ao-to-haru/chapter/10/pages",
                get(|| async { Json(json!({"status":false})) }),
            );
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let root = format!("http://{}", listener.local_addr().unwrap());
        let server = tokio::spawn(async move { axum::serve(listener, app).await.unwrap() });
        let provider = Capibara {
            root,
            ..Capibara::new().unwrap()
        };
        let store = CatalogStore::in_memory().unwrap();
        let page = provider.search("Ao & Haru", 1).await.unwrap();
        assert_eq!(page.items.len(), 1);
        assert_eq!(page.next_page, Some(2));
        assert!(provider
            .search("Ao & Haru", 2)
            .await
            .unwrap()
            .next_page
            .is_none());
        let edition = page.items[0].edition.clone();
        let item = provider
            .add(edition.clone(), store.clone(), false)
            .await
            .unwrap();
        let id = item.sync_id.unwrap();
        assert!(capibara_linked(id));
        assert!(!capibara_linked(Uuid::new_v4()));
        let units = item.metadata["units"].as_array().unwrap();
        assert_eq!(
            units
                .iter()
                .map(|u| &u["displayNumber"])
                .collect::<Vec<_>>(),
            ["1.5", "2", "10"]
        );
        store.set_favorite(id, true).unwrap();
        provider.add(edition, store.clone(), false).await.unwrap();
        let library = store.page(&Default::default()).unwrap();
        assert_eq!(library.total, 1);
        assert_eq!(library.favorite_ids, [id]);
        let other = CatalogStore::in_memory().unwrap();
        other.import(&store.export().unwrap(), true).unwrap();
        let urls = provider
            .pages(other.clone(), id, "1.5".into())
            .await
            .unwrap();
        assert_eq!(
            urls,
            [
                "https://r2.capibaratraductor.com/1.webp",
                "https://r2.capibaratraductor.com/2.webp"
            ]
        );
        assert!(provider.pages(other.clone(), id, "4".into()).await.is_err());
        assert!(provider.pages(other, id, "10".into()).await.is_err());
        server.abort();
    }

    #[tokio::test]
    #[ignore = "requiere el catálogo público de CapibaraTraductor"]
    async fn live_search_add_and_open_first_image() {
        let provider = Capibara::new().unwrap();
        let store = CatalogStore::in_memory().unwrap();
        let hit = provider
            .search("Ao to Haru", 1)
            .await
            .unwrap()
            .items
            .into_iter()
            .find(|h| h.edition.id == 1663)
            .unwrap();
        let item = provider
            .add(hit.edition, store.clone(), false)
            .await
            .unwrap();
        let urls = provider
            .pages(store, item.sync_id.unwrap(), "2".into())
            .await
            .unwrap();
        let response = provider
            .client
            .get(&urls[0])
            .send()
            .await
            .unwrap()
            .error_for_status()
            .unwrap();
        assert!(response.headers()["content-type"]
            .to_str()
            .unwrap()
            .starts_with("image/"));
        assert!(!response.bytes().await.unwrap().is_empty());
    }

    #[tokio::test]
    #[ignore = "requiere una edición conjunta pública"]
    async fn live_joint_survives_transfer_and_opens_chapter() {
        let provider = Capibara::new().unwrap();
        let hit = provider
            .search("Hanaori", 1)
            .await
            .unwrap()
            .items
            .into_iter()
            .find(|h| h.edition.joint && h.edition.id == 106)
            .unwrap();
        assert!(hit.group.contains(" + "));
        let item = provider
            .add(hit.edition, CatalogStore::in_memory().unwrap(), false)
            .await
            .unwrap();
        let id = item.sync_id.unwrap();
        assert_ne!(id, Uuid::from_u128(EDITION_PREFIX | 106));
        let other = CatalogStore::in_memory().unwrap();
        other
            .import(&CatalogDocument::new(vec![item]), false)
            .unwrap();
        let urls = provider.pages(other, id, "1".into()).await.unwrap();
        let response = provider
            .client
            .get(&urls[0])
            .send()
            .await
            .unwrap()
            .error_for_status()
            .unwrap();
        assert!(response.headers()["content-type"]
            .to_str()
            .unwrap()
            .starts_with("image/"));
    }
}
