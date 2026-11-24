use crate::{work, CommandResult};
use serde_json::Value;
use tauri::State;
use umd_core::{CatalogItem, CatalogStore};
use umd_providers::{Entry, Page, Provider, Site};
use uuid::Uuid;

fn explain(err: umd_providers::Failure) -> String {
    match err.kind {
        "throttled" => format!("El proveedor pidió esperar {} segundos.", err.retry),
        "not_found" => "El título ya no está disponible.".into(),
        "network" => "No se pudo conectar con el proveedor.".into(),
        "challenge_or_access_denied" => "El proveedor denegó el acceso.".into(),
        "unsupported" => "El proveedor no ofreció una fuente compatible.".into(),
        _ => "La respuesta del proveedor tiene un formato inesperado.".into(),
    }
}
/// A provider that is reused between commands. It keeps its connection,
/// cookies, request pacing and, for the sites that publish one, the catalogue
/// index: a search used to download that 4 to 16 MiB index again every time.
struct Cached {
    provider: std::sync::Arc<tokio::sync::Mutex<Provider>>,
    /// The verified browser session the provider was built with, if any.
    session: Option<Uuid>,
    created: std::time::Instant,
    used: std::time::Instant,
}
/// Idle providers are dropped so an index does not stay in memory, and every
/// provider is rebuilt now and then so its pinned addresses follow the site.
const PROVIDER_IDLE: std::time::Duration = std::time::Duration::from_secs(10 * 60);
const PROVIDER_LIFETIME: std::time::Duration = std::time::Duration::from_secs(30 * 60);
static PROVIDERS: std::sync::LazyLock<std::sync::Mutex<std::collections::HashMap<String, Cached>>> =
    std::sync::LazyLock::new(Default::default);

impl Cached {
    fn live(&self) -> bool {
        self.used.elapsed() < PROVIDER_IDLE && self.created.elapsed() < PROVIDER_LIFETIME
    }
}
/// Frees idle providers even when no other provider command arrives: an index
/// of several megabytes must not stay resident for the rest of a film. The
/// task ends when the cache is empty and starts again with the next provider.
static SWEEPING: std::sync::atomic::AtomicBool = std::sync::atomic::AtomicBool::new(false);
async fn sweep() {
    loop {
        tokio::time::sleep(std::time::Duration::from_secs(60)).await;
        let Ok(mut providers) = PROVIDERS.lock() else {
            SWEEPING.store(false, std::sync::atomic::Ordering::Release);
            return;
        };
        providers.retain(|_, entry| entry.live());
        if providers.is_empty() {
            SWEEPING.store(false, std::sync::atomic::Ordering::Release);
            return;
        }
    }
}

#[derive(Clone, serde::Serialize, serde::Deserialize)]
pub struct SourceConfig {
    id: String,
    name: String,
    base: String,
}
static CONFIG: std::sync::LazyLock<std::sync::Mutex<Vec<SourceConfig>>> =
    std::sync::LazyLock::new(Default::default);
pub fn setup_sources(store: &CatalogStore) -> CommandResult<()> {
    if let Some(text) = store
        .setting("provider-locations")
        .map_err(|e| e.to_string())?
    {
        *CONFIG.lock().map_err(|_| "Configuración ocupada.")? =
            serde_json::from_str(&text).map_err(|_| "Configuración de proveedores inválida.")?;
    }
    Ok(())
}
#[tauri::command]
pub async fn provider_locations(
    store: State<'_, CatalogStore>,
    edit: Option<SourceConfig>,
    remove: Option<String>,
) -> CommandResult<Vec<SourceConfig>> {
    let store = store.inner().clone();
    work(move || update_provider_locations(&store, edit, remove)).await
}

fn update_provider_locations(
    store: &CatalogStore,
    edit: Option<SourceConfig>,
    remove: Option<String>,
) -> CommandResult<Vec<SourceConfig>> {
    let mut saved = CONFIG.lock().map_err(|_| "Configuración ocupada.")?;
    if edit.is_some() || remove.is_some() {
        let mut next = saved.clone();
        if let Some(id) = &remove {
            next.retain(|s| &s.id != id);
        }
        if let Some(mut item) = edit {
            if item.id.is_empty() {
                item.id = format!("json-{}", Uuid::new_v4());
            }
            if !(Site::parse(&item.id).is_some()
                || item
                    .id
                    .strip_prefix("json-")
                    .and_then(|s| Uuid::parse_str(s).ok())
                    .is_some())
            {
                return Err("Proveedor desconocido.".into());
            }
            item.name = item.name.trim().to_owned();
            if item.name.is_empty()
                || item.name.len() > 128
                || item.name.chars().any(char::is_control)
            {
                return Err("Nombre de proveedor inválido.".into());
            }
            let mut base =
                reqwest::Url::parse(item.base.trim()).map_err(|_| "Dirección inválida.")?;
            if !umd_core::public_url::web_url(&base)
                || base.query().is_some()
                || base.fragment().is_some()
            {
                return Err("Ingresá una dirección base pública.".into());
            }
            if !base.path().ends_with('/') {
                base.path_segments_mut()
                    .map_err(|_| "Ruta inválida.")?
                    .push("");
            }
            item.base = base.into();
            next.retain(|s| s.id != item.id);
            next.push(item);
        }
        if next.len() > 64 {
            return Err("Se alcanzó el límite de proveedores.".into());
        }
        store
            .set_setting(
                "provider-locations",
                &serde_json::to_string(&next).map_err(|e| e.to_string())?,
            )
            .map_err(|e| e.to_string())?;
        *saved = next;
        PROVIDERS.lock().map_err(|_| "Proveedor ocupado.")?.clear();
    }
    Ok(saved.clone())
}
fn location(id: &str) -> Option<reqwest::Url> {
    CONFIG
        .lock()
        .ok()?
        .iter()
        .find(|s| s.id == id)
        .and_then(|s| reqwest::Url::parse(&s.base).ok())
}
pub(crate) fn provider_referer(id: &str) -> Option<reqwest::Url> {
    if let Some(url) = location(id) {
        return Some(url);
    }
    if id == "capibaratraductor" {
        return reqwest::Url::parse("https://capibaratraductor.com/").ok();
    }
    Site::parse(id).and_then(|site| reqwest::Url::parse(site.base()).ok())
}
#[tauri::command]
pub async fn json_provider_search(
    site: String,
    query: String,
    cursor: Option<String>,
) -> CommandResult<(Vec<CatalogItem>, Option<String>, bool)> {
    let root = location(&site)
        .filter(|_| site.starts_with("json-"))
        .ok_or("Proveedor desconocido.")?;
    let mut url = root.join("search").map_err(|e| e.to_string())?;
    url.query_pairs_mut()
        .append_pair("q", &query)
        .append_pair("limit", "25");
    if let Some(cursor) = cursor {
        url.query_pairs_mut().append_pair("cursor", &cursor);
    }
    let response = crate::streams::get_public(url, std::time::Duration::from_secs(20)).await?;
    let next = response
        .headers()
        .get("x-umd-next-cursor")
        .and_then(|s| s.to_str().ok())
        .map(str::to_owned);
    let summary = response
        .headers()
        .get("x-umd-summary")
        .and_then(|v| v.to_str().ok())
        .is_some_and(|v| v.eq_ignore_ascii_case("true"));
    let text = crate::streams::bounded_text(response, 8 << 20).await?;
    let mut doc = umd_core::CatalogDocument::parse(text.as_bytes()).map_err(|e| e.to_string())?;
    doc.items
        .retain(|i| !i.deleted && (!summary || i.sync_id.is_some()));
    for item in &mut doc.items {
        item.sync_id = Some(item.id().map_err(|e| e.to_string())?);
    }
    Ok((doc.items, next, summary))
}
#[tauri::command]
pub async fn json_provider_add(
    site: String,
    id: Uuid,
    preview: Option<CatalogItem>,
    hide_adult: bool,
    store: State<'_, CatalogStore>,
) -> CommandResult<CatalogItem> {
    let root = location(&site)
        .filter(|_| site.starts_with("json-"))
        .ok_or("Proveedor desconocido.")?;
    let mut item = if let Some(item) = preview {
        item
    } else {
        let url = root
            .join(&format!("items/{}", id.simple()))
            .map_err(|e| e.to_string())?;
        let response = crate::streams::get_public(url, std::time::Duration::from_secs(20)).await?;
        let data = crate::streams::bounded_text(response, 8 << 20).await?;
        umd_core::CatalogDocument::parse(data.as_bytes())
            .map_err(|e| e.to_string())?
            .items
            .into_iter()
            .find(|i| i.id().ok() == Some(id))
            .ok_or("Título no disponible.")?
    };
    if item.deleted || item.id().map_err(|e| e.to_string())? != id || hide_adult && item.adult() {
        return Err("Título no disponible.".into());
    }
    item.sync_id = Some(id);

    let store = store.inner().clone();
    crate::work(move || {
        store
            .import(&umd_core::CatalogDocument::new(vec![item.clone()]), false)
            .map_err(|e| e.to_string())?;
        Ok(item)
    })
    .await
}
async fn open(id: &str) -> CommandResult<tokio::sync::OwnedMutexGuard<Provider>> {
    let site = Site::parse(id).ok_or("Proveedor desconocido.")?;
    let base = location(id);
    let verified = if base.is_some() { None } else { verified(id) };
    let session = verified.as_ref().map(|(_, session, _)| *session);
    let cached = {
        let mut providers = PROVIDERS.lock().map_err(|e| e.to_string())?;
        providers.retain(|_, entry| entry.live());
        providers
            .get_mut(id)
            .filter(|entry| entry.session == session)
            .map(|entry| {
                entry.used = std::time::Instant::now();
                entry.provider.clone()
            })
    };
    let provider = match cached {
        Some(provider) => provider,
        None => {
            let mut provider = match base {
                Some(url) => Provider::open_at(site, url).await,
                None => Provider::open(site).await,
            }
            .map_err(explain)?;
            if let Some((app, session, origin)) = verified {
                provider.set_public_reader(std::sync::Arc::new(move |url, limit| {
                    let app = app.clone();
                    let origin = origin.clone();
                    Box::pin(async move {
                        crate::ad_filter::verified_read(&app, session, &origin, &url, limit)
                            .await
                            .ok()
                    })
                }));
            }
            let provider = std::sync::Arc::new(tokio::sync::Mutex::new(provider));
            let now = std::time::Instant::now();
            let mut providers = PROVIDERS.lock().map_err(|e| e.to_string())?;
            providers.insert(
                id.to_owned(),
                Cached {
                    provider: provider.clone(),
                    session,
                    created: now,
                    used: now,
                },
            );
            // The flag changes only under the cache lock, so an entry is never
            // left without a sweeper.
            if !SWEEPING.swap(true, std::sync::atomic::Ordering::AcqRel) {
                tauri::async_runtime::spawn(sweep());
            }
            drop(providers);
            provider
        }
    };
    // One request at a time per site, which is also what its pacing expects.
    Ok(provider.lock_owned().await)
}

type Verified = (tauri::AppHandle, Uuid, reqwest::Url);
static VERIFIED: std::sync::LazyLock<
    std::sync::Mutex<std::collections::HashMap<String, Verified>>,
> = std::sync::LazyLock::new(Default::default);
pub(super) fn verified(id: &str) -> Option<Verified> {
    VERIFIED.lock().ok()?.get(id).cloned()
}
#[tauri::command]
pub async fn provider_interactive(
    app: tauri::AppHandle,
    site: String,
    language: Option<String>,
) -> CommandResult<Uuid> {
    let page = match site.as_str() {
        "capibaratraductor" => "https://capibaratraductor.com/",
        "framextv" => "https://framextv.xyz/",
        _ => Site::parse(&site).ok_or("Proveedor desconocido.")?.base(),
    };
    let session =
        crate::ad_filter::open_filtered_player(app.clone(), page.into(), language, Some(true))
            .await?;
    VERIFIED.lock().map_err(|e| e.to_string())?.insert(
        site,
        (
            app,
            session,
            reqwest::Url::parse(page).map_err(|e| e.to_string())?,
        ),
    );
    Ok(session)
}

#[tauri::command]
pub async fn provider_search(site: String, query: String, page: u32) -> CommandResult<Page> {
    open(&site)
        .await?
        .search(&query, page)
        .await
        .map_err(explain)
}
#[tauri::command]
pub async fn provider_updates(
    site: String,
    store: State<'_, CatalogStore>,
) -> CommandResult<Vec<Entry>> {
    let key = format!("daily-list-{site}");
    let now = chrono::Utc::now();
    if let Some((date, rows)) = store
        .setting(&key)
        .map_err(|e| e.to_string())?
        .and_then(|raw| {
            serde_json::from_str::<(chrono::DateTime<chrono::Utc>, Vec<Entry>)>(&raw).ok()
        })
    {
        if (0..86400).contains(&(now - date).num_seconds()) {
            return Ok(rows);
        }
    }
    let mut provider = open(&site).await?;
    let mut rows = provider.updates().await.map_err(explain)?;
    rows.truncate(100);
    let encoded = serde_json::to_string(&(now, &rows)).map_err(|e| e.to_string())?;
    store
        .set_setting(&key, &encoded)
        .map_err(|e| e.to_string())?;
    Ok(rows)
}
#[tauri::command]
pub async fn refresh_saved_editions(
    store: State<'_, CatalogStore>,
    capibara: State<'_, crate::capibara::Capibara>,
    sites: Vec<String>,
) -> CommandResult<usize> {
    static REFRESH: tokio::sync::Mutex<()> = tokio::sync::Mutex::const_new(());
    let Ok(_guard) = REFRESH.try_lock() else {
        return Ok(0);
    };
    let db = store.inner().clone();
    let editions = work(move || db.saved_editions().map_err(|e| e.to_string())).await?;
    let mut updated = 0;
    for (id, name, site, external) in editions {
        if !sites.contains(&site) {
            continue;
        }
        let stamp = format!("edition-refresh-{site}-{id}");
        let now = chrono::Utc::now().timestamp();
        if store
            .setting(&stamp)
            .map_err(|e| e.to_string())?
            .and_then(|s| s.parse::<i64>().ok())
            .is_some_and(|next| next > now && next <= now + 86400)
        {
            continue;
        }
        let library = store.inner().clone();
        let item = match work(move || library.item(id).map_err(|e| e.to_string()))
            .await?
            .and_then(|d| d.items.into_iter().next())
        {
            Some(i) => i,
            None => continue,
        };
        let key = item.key();
        let result: CommandResult<CatalogItem> = async {
            if site == "capibaratraductor" {
                let edition = if let Some(e) = item
                    .metadata
                    .get("capibaraEdition")
                    .and_then(|v| serde_json::from_value(v.clone()).ok())
                {
                    e
                } else {
                    let target = umd_core::capibara::edition_id(id)
                        .or_else(|| external.parse().ok())
                        .ok_or("Edición desconocida.")?;
                    capibara
                        .search(&name, 1)
                        .await?
                        .items
                        .into_iter()
                        .map(|h| h.edition)
                        .find(|e| e.id == target)
                        .ok_or("Edición desconocida.")?
                };
                let mut fresh = capibara.edition_detail(&edition).await?;
                fresh.metadata.insert(
                    "capibaraEdition".into(),
                    serde_json::to_value(edition).map_err(|e| e.to_string())?,
                );
                Ok(fresh)
            } else {
                open(&site)
                    .await?
                    .detail(&Entry {
                        external_id: external.clone(),
                        preview: serde_json::json!({"name":name}),
                    })
                    .await
                    .map_err(explain)
            }
        }
        .await;
        let success = match result {
            Ok(new) => {
                let library = store.inner().clone();
                let (key, site, external) = (key.clone(), site.clone(), external.clone());
                work(move || {
                    Ok(library
                        .link_provider_edition(id, &key, &site, &external, new)
                        .is_ok())
                })
                .await
                .unwrap_or(false)
            }
            Err(_) => false,
        };
        store
            .set_setting(
                &stamp,
                &(now + if success { 86400 } else { 3600 }).to_string(),
            )
            .map_err(|e| e.to_string())?;
        updated += usize::from(success);
        tokio::time::sleep(std::time::Duration::from_millis(500)).await;
    }
    Ok(updated)
}

#[derive(serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RefreshItemResult {
    pub item: CatalogItem,
    pub changed: bool,
    pub sources_checked: Vec<String>,
    pub errors: Vec<String>,
}

async fn fetch_linked_provider_detail(
    id: Uuid,
    item: &CatalogItem,
    site: &str,
    external: &str,
    capibara: &crate::capibara::Capibara,
) -> CommandResult<CatalogItem> {
    if site == "capibaratraductor" {
        let edition = if let Some(e) = item
            .metadata
            .get("capibaraEdition")
            .and_then(|v| serde_json::from_value(v.clone()).ok())
        {
            e
        } else {
            let target = umd_core::capibara::edition_id(id)
                .or_else(|| external.parse().ok())
                .ok_or("Edición desconocida.")?;
            capibara
                .search(&item.name, 1)
                .await?
                .items
                .into_iter()
                .map(|h| h.edition)
                .find(|e| e.id == target)
                .ok_or("Edición desconocida.")?
        };
        let mut fresh = capibara.edition_detail(&edition).await?;
        fresh.metadata.insert(
            "capibaraEdition".into(),
            serde_json::to_value(edition).map_err(|e| e.to_string())?,
        );
        Ok(fresh)
    } else if site.starts_with("json-") {
        let root = location(site).ok_or("Proveedor desconocido.")?;
        let url = root
            .join(&format!("items/{id}"))
            .map_err(|e| e.to_string())?;
        let response = crate::streams::get_public(url, std::time::Duration::from_secs(20)).await?;
        let text = crate::streams::bounded_text(response, 8 << 20).await?;
        let doc = umd_core::CatalogDocument::parse(text.as_bytes()).map_err(|e| e.to_string())?;
        doc.items
            .into_iter()
            .next()
            .ok_or("El proveedor devolvió una ficha vacía.".into())
    } else {
        open(site)
            .await?
            .detail(&Entry {
                external_id: external.to_owned(),
                preview: serde_json::json!({"name": item.name}),
            })
            .await
            .map_err(explain)
    }
}

#[tauri::command]
pub async fn refresh_item(
    id: Uuid,
    hide_adult: Option<bool>,
    discover: Option<bool>,
    store: State<'_, CatalogStore>,
    capibara: State<'_, crate::capibara::Capibara>,
) -> CommandResult<RefreshItemResult> {
    let hide_adult = hide_adult.unwrap_or(false);
    let discover = discover.unwrap_or(false);
    let db = store.inner().clone();
    let mut item = work(move || {
        db.item(id)
            .map_err(|e| e.to_string())?
            .and_then(|d| d.items.into_iter().next())
            .ok_or("El título ya no está en la biblioteca.".into())
    })
    .await?;
    let initial_snapshot = serde_json::to_value(&item).map_err(|e| e.to_string())?;
    let mut sources_checked = Vec::new();
    let mut errors = Vec::new();

    // 1. Refresh from connected UMD server if configured.
    if store
        .setting("server_url")
        .ok()
        .flatten()
        .is_some_and(|s| !s.trim().is_empty())
    {
        match crate::remote_item_document(&store, id, hide_adult, None).await {
            Ok(doc) => {
                if let Some(incoming) = doc.items.into_iter().next() {
                    let db = store.inner().clone();
                    match work(move || db.merge_remote_item(id, incoming).map_err(|e| e.to_string()))
                        .await
                    {
                        Ok((merged, _)) => {
                            item = merged;
                            sources_checked.push("server".into());
                        }
                        Err(err) => errors.push(format!("server: {err}")),
                    }
                }
            }
            Err(err) => errors.push(format!("server: {err}")),
        }
    }

    // 2. Collect all linked providers (onlineProviders metadata + crawl bindings + capibara).
    let mut linked: Vec<(String, String)> = Vec::new();
    if let Some(map) = item.metadata.get("onlineProviders").and_then(Value::as_object) {
        for (site, ext) in map {
            if let Some(ext_str) = ext.as_str().filter(|s| !s.trim().is_empty()) {
                linked.push((site.clone(), ext_str.to_owned()));
            }
        }
    }
    let db_bindings = store.inner().clone();
    if let Ok(bindings) =
        work(move || db_bindings.item_bindings(id).map_err(|e| e.to_string())).await
    {
        for (site, ext) in bindings {
            if !linked.iter().any(|(s, _)| s == &site) {
                linked.push((site, ext));
            }
        }
    }
    if !linked.iter().any(|(s, _)| s == "capibaratraductor") {
        if let Some(cap_id) = umd_core::capibara::edition_id(id) {
            linked.push(("capibaratraductor".into(), cap_id.to_string()));
        }
    }

    // 3. Refresh each linked provider edition.
    for (site, external) in &linked {
        if site != "capibaratraductor"
            && !site.starts_with("json-")
            && Site::parse(site).is_none()
        {
            continue;
        }
        match fetch_linked_provider_detail(id, &item, site, external, &capibara).await {
            Ok(fresh) => {
                if hide_adult && fresh.adult() {
                    continue;
                }
                let db = store.inner().clone();
                let key = item.key();
                let (site_c, ext_c) = (site.clone(), external.clone());
                if let Ok(Some(updated)) = work(move || {
                    db.link_provider_edition(id, &key, &site_c, &ext_c, fresh)
                        .map_err(|e| e.to_string())?;
                    Ok(db
                        .item(id)
                        .map_err(|e| e.to_string())?
                        .and_then(|d| d.items.into_iter().next()))
                })
                .await
                {
                    item = updated;
                    sources_checked.push(site.clone());
                }
            }
            Err(err) => errors.push(format!("{site}: {err}")),
        }
    }

    // 4. If manually requested (or still missing artwork with no linked providers),
    //    try a strict high-confidence provider match to populate chapters and cover.
    let missing_artwork = item
        .metadata
        .get("thumbnailRef")
        .and_then(Value::as_str)
        .is_none_or(|s| s.trim().is_empty());
    if (discover && linked.is_empty()) || (discover && missing_artwork && sources_checked.is_empty())
    {
        if let Some((site, external, fresh)) =
            crate::chapter_lookup::discover_best_match(&item, &capibara).await
        {
            if !(hide_adult && fresh.adult()) {
                let db = store.inner().clone();
                let key = item.key();
                let site_owned = site.to_owned();
                if let Ok(Some(updated)) = work(move || {
                    db.link_provider_edition(id, &key, &site_owned, &external, fresh)
                        .map_err(|e| e.to_string())?;
                    Ok(db
                        .item(id)
                        .map_err(|e| e.to_string())?
                        .and_then(|d| d.items.into_iter().next()))
                })
                .await
                {
                    item = updated;
                    sources_checked.push(site.to_owned());
                }
            }
        }
    }

    let final_snapshot = serde_json::to_value(&item).map_err(|e| e.to_string())?;
    Ok(RefreshItemResult {
        item,
        changed: final_snapshot != initial_snapshot,
        sources_checked,
        errors,
    })
}

#[tauri::command]
pub async fn provider_add(
    site: String,
    entry: Entry,
    hide_adult: Option<bool>,
    store: State<'_, CatalogStore>,
) -> CommandResult<CatalogItem> {
    let item = open(&site).await?.detail(&entry).await.map_err(explain)?;
    if hide_adult == Some(true) && item.adult() {
        return Err("El filtro +18 ocultó esta ficha.".into());
    }
    let store = store.inner().clone();
    work(move || {
        store
            .add_provider_title(&site, &entry.external_id, item)
            .map_err(|e| e.to_string())
    })
    .await
}
#[tauri::command]
pub async fn provider_unit(
    site: String,
    id: Uuid,
    number: String,
    season: Option<u32>,
    store: State<'_, CatalogStore>,
) -> CommandResult<Value> {
    let store = store.inner().clone();
    let item = work(move || {
        store
            .item(id)
            .map_err(|e| e.to_string())?
            .and_then(|doc| doc.items.into_iter().next())
            .ok_or("Título desconocido.".into())
    })
    .await?;
    let external = item
        .metadata
        .get("onlineProviders")
        .and_then(|links| links.get(&site))
        .and_then(Value::as_str)
        .ok_or("Título sin vínculo con el proveedor.")?;
    let entry = Entry {
        external_id: external.into(),
        preview: serde_json::json!({"name":item.name}),
    };
    let mut provider = open(&site).await?;
    match site.as_str() {
        "jkanime" if season.is_none() => Ok(Value::Array(
            provider
                .jk_streams(external, &number)
                .await
                .map_err(explain)?,
        )),
        "doramasflix" => Ok(Value::Array(
            provider
                .doramas_streams(external, season, &number)
                .await
                .map_err(explain)?,
        )),
        "manhwaweb" if season.is_none() => Ok(serde_json::json!(provider
            .manhwa_images(external, &number)
            .await
            .map_err(explain)?)),
        "olympusxyz" if season.is_none() => Ok(serde_json::json!(provider
            .reader_pages(&entry, &number)
            .await
            .map_err(explain)?)),
        "framextv" => Ok(Value::Array(
            provider
                .framex_streams(
                    external,
                    season,
                    number.parse().map_err(|_| "Episodio inválido.")?,
                )
                .await
                .map_err(explain)?,
        )),
        "animetmo" if season.is_none() => Ok(Value::Array(
            provider
                .anime_streams(external, &number)
                .await
                .map_err(explain)?,
        )),
        _ if matches!(Site::parse(&site), Some(Site::Cinema(_))) => Ok(Value::Array(
            provider
                .cinema_streams(external, season, &number)
                .await
                .map_err(explain)?,
        )),
        "animeav1" if season.is_none() => Ok(Value::Array(
            provider
                .av1_streams(external, &number)
                .await
                .map_err(explain)?,
        )),
        _ if season.is_none() && matches!(Site::parse(&site), Some(Site::Manga(_))) => {
            let Some(Site::Manga(manga)) = Site::parse(&site) else {
                unreachable!()
            };
            Ok(serde_json::json!(provider
                .manga_images(manga, external, &number)
                .await
                .map_err(explain)?))
        }
        _ => Err("Unidad no disponible para este proveedor.".into()),
    }
}

#[tauri::command]
pub fn open_provider_player(app: tauri::AppHandle, address: String) -> CommandResult<()> {
    use tauri_plugin_opener::OpenerExt;
    let url = reqwest::Url::parse(&address)
        .ok()
        .filter(|u| u.scheme() == "https" && umd_core::public_url::web_url(u))
        .ok_or("Dirección de reproducción inválida.")?;
    app.opener()
        .open_url(url.as_str(), None::<String>)
        .map_err(|_| "No se pudo abrir el navegador.".into())
}
