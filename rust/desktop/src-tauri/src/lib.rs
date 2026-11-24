// Tauri links its Windows manifest to the application binary. The library's
// standalone test executable also invokes native dialogs that use Common Controls v6.
#[cfg(all(test, windows, target_env = "msvc"))]
#[link(name = "resource.lib", kind = "dylib", modifiers = "+verbatim")]
unsafe extern "C" {}

mod ad_filter;
mod artwork;
mod capibara;
mod chapter_lookup;
mod database;
mod downloads;
mod epub;
mod hls_proxy;
mod media;
mod metadata;
mod native_player;
mod private_files;
mod providers;
mod remote;
mod streams;
mod torrent_manifest;
mod torrent_stream;
#[cfg(any(windows, target_os = "linux", target_os = "android"))]
mod updates;
use media::{get_progress, open_media, save_progress};
use remote::{account_backup_info, account_login, account_logout, account_restore, account_upload};

use std::io::{Read, Write};
use tauri::{Manager, State};
use tauri_plugin_dialog::DialogExt;
use tauri_plugin_fs::{FilePath, FsExt, OpenOptions};
use umd_core::{CatalogDocument, CatalogQuery, CatalogStore, LibraryPage, MAX_DOCUMENT_BYTES};
use uuid::Uuid;

type CommandResult<T> = Result<T, String>;

fn with_document<T>(
    app: &tauri::AppHandle,
    path: FilePath,
    options: OpenOptions,
    operation: impl FnOnce(std::fs::File) -> CommandResult<T>,
) -> CommandResult<T> {
    let file = app
        .fs()
        .open(path.clone(), options)
        .map_err(|e| e.to_string())?;
    let result = operation(file);
    #[cfg(target_os = "ios")]
    if matches!(&path, FilePath::Url(u) if u.scheme() == "file") {
        let _ = app.fs().stop_accessing_security_scoped_resource(path);
    }
    result
}

/// One connection pool for requests to the person's own server and to the
/// metadata services, instead of a new client and TLS setup per command.
/// Redirects are never followed and each request sets its own timeout.
/// Requests to arbitrary public hosts keep their own address-pinned clients.
pub(crate) fn direct_client() -> CommandResult<reqwest::Client> {
    static CLIENT: std::sync::OnceLock<reqwest::Client> = std::sync::OnceLock::new();
    if let Some(client) = CLIENT.get() {
        return Ok(client.clone());
    }
    let client = reqwest::Client::builder()
        .redirect(reqwest::redirect::Policy::none())
        .build()
        .map_err(|e| e.to_string())?;
    Ok(CLIENT.get_or_init(|| client).clone())
}

async fn work<T: Send + 'static>(
    task: impl FnOnce() -> CommandResult<T> + Send + 'static,
) -> CommandResult<T> {
    tauri::async_runtime::spawn_blocking(task)
        .await
        .map_err(|error| error.to_string())?
}

pub(crate) struct CatalogReads(CatalogStore);

#[derive(Default)]
struct LibraryStartup(tokio::sync::OnceCell<CommandResult<()>>);

#[tauri::command]
async fn initialize_library(
    app: tauri::AppHandle,
    startup: State<'_, LibraryStartup>,
) -> CommandResult<()> {
    startup
        .0
        .get_or_init(|| work(move || open_library(&app)))
        .await
        .clone()
}

fn open_library(app: &tauri::AppHandle) -> CommandResult<()> {
    let directory = match std::env::var_os("UMD_RUST_DESKTOP_DATA_DIRECTORY") {
        Some(path) => path.into(),
        None => app.path().app_data_dir().map_err(|e| e.to_string())?,
    };
    std::fs::create_dir_all(&directory).map_err(|e| e.to_string())?;
    let (store, db) = database::LocalDatabase::open(directory.clone())?;
    let reader = db.open_reader()?;
    let capibara = capibara::Capibara::new().map_err(|e| e.to_string())?;
    providers::setup_sources(&store)?;
    ad_filter::setup(app, &directory, &store).map_err(|e| e.to_string())?;
    let downloads = downloads::Downloads::new(directory.join("downloads"));
    downloads.fast_resume.store(
        store.setting("torrent-fast-resume").map_err(|e| e.to_string())?.as_deref() != Some("off"),
        std::sync::atomic::Ordering::Relaxed,
    );
    app.manage(private_files::PrivateFiles(directory.join("private-profiles")));
    app.manage(downloads);
    app.manage(capibara);
    app.manage(CatalogReads(reader));
    app.manage(store);
    app.manage(db);
    Ok(())
}

#[tauri::command]
async fn profiles(reader: State<'_, CatalogReads>) -> CommandResult<umd_core::Profiles> {
    let store = reader.0.clone();
    work(move || store.profiles().map_err(|e| e.to_string())).await
}

#[tauri::command]
async fn resume_item(
    profile_id: Uuid,
    hide_adult: bool,
    reader: State<'_, CatalogReads>,
) -> CommandResult<Option<serde_json::Value>> {
    let store = reader.0.clone();
    work(move || {
        store
            .for_profile(profile_id).map_err(|e| e.to_string())?
            .continue_reading_or_watching(hide_adult)
            .map_err(|e| e.to_string())
    })
    .await
}

#[tauri::command]
async fn continue_items(
    profile_id: Uuid,
    hide_adult: bool,
    limit: Option<usize>,
    reader: State<'_, CatalogReads>,
) -> CommandResult<Vec<serde_json::Value>> {
    let store = reader.0.clone();
    let limit = limit.unwrap_or(12).clamp(1, 40);
    work(move || {
        store
            .for_profile(profile_id).map_err(|e| e.to_string())?
            .continue_items(hide_adult, limit)
            .map_err(|e| e.to_string())
    })
    .await
}

#[tauri::command]
async fn shelf_units(
    ids: Vec<Uuid>,
    profile_id: Uuid,
    reader: State<'_, CatalogReads>,
) -> CommandResult<std::collections::HashMap<String, serde_json::Value>> {
    let store = reader.0.clone();
    work(move || store.for_profile(profile_id).and_then(|store| store.shelf_units(&ids)).map_err(|e| e.to_string())).await
}
#[tauri::command]
async fn delete_unit(
    target: umd_core::ProgressTarget,
    store: State<'_, CatalogStore>,
) -> CommandResult<umd_core::CatalogItem> {
    let store = store.inner().clone();
    work(move || store.delete_unit(&target).map_err(|e| e.to_string())).await
}
#[tauri::command]
async fn filter_choices(reader: State<'_, CatalogReads>) -> CommandResult<serde_json::Value> {
    let store = reader.0.clone();
    work(move || store.filter_choices().map_err(|e| e.to_string())).await
}
#[tauri::command]
async fn home_recent(reader: State<'_, CatalogReads>) -> CommandResult<serde_json::Value> {
    let store = reader.0.clone();
    work(move || store.home_recent().map_err(|e| e.to_string())).await
}
#[tauri::command]
async fn delete_catalog_item(id: Uuid, store: State<'_, CatalogStore>) -> CommandResult<bool> {
    let store = store.inner().clone();
    work(move || store.delete_item(id).map_err(|e| e.to_string())).await
}

#[tauri::command]
async fn library(
    query: CatalogQuery,
    hide_adult: Option<bool>,
    preferred_providers: Option<Vec<String>>,
    profile_id: uuid::Uuid,
    reader: State<'_, CatalogReads>,
) -> CommandResult<LibraryPage> {
    let store = reader.0.clone();
    work(move || {
        store
            .for_profile(profile_id).map_err(|e| e.to_string())?
            .display_page(
                &query,
                hide_adult.unwrap_or(false),
                preferred_providers.as_deref().unwrap_or_default(),
            )
            .map_err(|error| error.to_string())
    })
    .await
}

#[tauri::command]
async fn favorite(
    id: Uuid,
    enabled: bool,
    profile_id: uuid::Uuid,
    store: State<'_, CatalogStore>,
) -> CommandResult<()> {
    let store = store.for_profile(profile_id).map_err(|e| e.to_string())?;
    work(move || {
        store
            .set_favorite(id, enabled)
            .map_err(|error| error.to_string())
    })
    .await
}

#[tauri::command]
async fn reader_mode(
    id: Uuid,
    mode: Option<String>,
    profile_id: uuid::Uuid,
    store: State<'_, CatalogStore>,
) -> CommandResult<String> {
    let store = store.for_profile(profile_id).map_err(|e| e.to_string())?;
    work(move || {
        store
            .reader_mode(id, mode.as_deref())
            .map_err(|e| e.to_string())
    })
    .await
}

#[tauri::command]
async fn personal_lists(
    profile_id: uuid::Uuid,
    reader: State<'_, CatalogReads>,
) -> CommandResult<Vec<serde_json::Value>> {
    let store = reader.0.clone();
    work(move || {
        store.for_profile(profile_id)
            .and_then(|store| store.personal_lists())
            .map_err(|e| e.to_string())
    }).await
}

#[tauri::command]
async fn edit_list(
    edit: umd_core::ListEdit,
    profile_id: uuid::Uuid,
    store: State<'_, CatalogStore>,
) -> CommandResult<()> {
    let store = store.for_profile(profile_id).map_err(|e| e.to_string())?;
    work(move || store.edit_list(edit).map_err(|e| e.to_string())).await
}

#[tauri::command]
async fn party_titles(
    media_type: String,
    name: String,
    store: State<'_, CatalogStore>,
) -> CommandResult<Vec<Uuid>> {
    let store = store.inner().clone();
    work(move || {
        store
            .party_titles(&media_type, &name)
            .map_err(|e| e.to_string())
    })
    .await
}

#[tauri::command]
async fn library_item(
    id: Uuid,
    reader: State<'_, CatalogReads>,
) -> CommandResult<umd_core::CatalogItem> {
    let store = reader.0.clone();
    work(move || {
        store
            .item(id)
            .map_err(|e| e.to_string())?
            .and_then(|d| d.items.into_iter().next())
            .ok_or("El título ya no está en la biblioteca.".into())
    })
    .await
}

#[tauri::command]
async fn import_catalog(
    app: tauri::AppHandle,
    store: State<'_, CatalogStore>,
) -> CommandResult<Option<usize>> {
    let store = store.inner().clone();
    work(move || {
        let Some(file) = app
            .dialog()
            .file()
            .add_filter("UMD catalog", &["json"])
            .blocking_pick_file()
        else {
            return Ok(None);
        };
        let mut bytes = Vec::new();
        let mut opts = OpenOptions::new();
        opts.read(true);
        with_document(&app, file, opts, |f| {
            f.take(MAX_DOCUMENT_BYTES as u64 + 1)
                .read_to_end(&mut bytes)
                .map_err(|error| error.to_string())
        })?;
        let document = CatalogDocument::parse(&bytes).map_err(|error| error.to_string())?;
        store
            .import(&document, false)
            .map(Some)
            .map_err(|error| error.to_string())
    })
    .await
}

#[tauri::command]
async fn export_catalog(
    app: tauri::AppHandle,
    store: State<'_, CatalogStore>,
) -> CommandResult<bool> {
    let store = store.inner().clone();
    work(move || {
        let Some(file) = app
            .dialog()
            .file()
            .add_filter("UMD catalog", &["json"])
            .set_file_name("umd-catalog.json")
            .blocking_save_file()
        else {
            return Ok(false);
        };
        let document = store.export().map_err(|error| error.to_string())?;
        let bytes = serde_json::to_vec_pretty(&document).map_err(|error| error.to_string())?;
        if bytes.len() > MAX_DOCUMENT_BYTES {
            return Err("Catalog exceeds the 32 MiB JSON transfer limit".into());
        }
        #[cfg(mobile)]
        {
            let mut opts = OpenOptions::new();
            opts.write(true).truncate(true);
            with_document(&app, file, opts, |mut f| {
                f.write_all(&bytes)
                    .and_then(|_| f.flush())
                    .map_err(|e| e.to_string())
            })?;
        }
        #[cfg(desktop)]
        {
            let path = file.into_path().map_err(|error| error.to_string())?;
            let parent = path.parent().ok_or("Export directory is unavailable")?;
            let mut temporary =
                tempfile::NamedTempFile::new_in(parent).map_err(|error| error.to_string())?;
            temporary
                .write_all(&bytes)
                .map_err(|error| error.to_string())?;
            temporary
                .as_file()
                .sync_all()
                .map_err(|error| error.to_string())?;
            temporary.persist(path).map_err(|error| error.to_string())?;
        }
        Ok(true)
    })
    .await
}

#[tauri::command]
async fn server_url(reader: State<'_, CatalogReads>) -> CommandResult<String> {
    let store = reader.0.clone();
    work(move || {
        store
            .setting("server_url")
            .map(|value| value.unwrap_or_default())
            .map_err(|error| error.to_string())
    }).await
}

fn catalog_base(store: &CatalogStore) -> CommandResult<reqwest::Url> {
    let raw = store
        .setting("server_url")
        .map_err(|e| e.to_string())?
        .ok_or("Primero conectá un servidor.")?;
    reqwest::Url::parse(&raw).map_err(|e| e.to_string())
}

async fn catalog_response(
    base: &reqwest::Url,
    route: &str,
    params: &[(&str, &str)],
) -> CommandResult<reqwest::Response> {
    let mut url = base.join(route).map_err(|e| e.to_string())?;
    url.query_pairs_mut().extend_pairs(params.iter().copied());
    direct_client()?
        .get(url)
        .timeout(std::time::Duration::from_secs(20))
        .send()
        .await
        .map_err(|e| e.to_string())
}

async fn catalog_document(
    response: reqwest::Response,
) -> CommandResult<(CatalogDocument, Option<String>)> {
    let response = response.error_for_status().map_err(|e| e.to_string())?;
    let next = response
        .headers()
        .get("x-umd-next-cursor")
        .and_then(|s| s.to_str().ok())
        .map(str::to_owned);
    let value = remote::json_response(response).await?;
    work(move || {
        let doc: CatalogDocument = serde_json::from_value(value).map_err(|e| e.to_string())?;
        doc.validate().map_err(|e| e.to_string())?;
        Ok((doc, next))
    }).await
}

#[tauri::command]
async fn search_remote_catalog(
    store: State<'_, CatalogStore>,
    query: String,
    cursor: Option<String>,
) -> CommandResult<(Vec<umd_core::CatalogItem>, Option<String>)> {
    let query = CatalogQuery {
        q: query,
        cursor,
        ..Default::default()
    };
    let page = online_page(&store, query, false, false).await?;
    Ok((page.items, page.next))
}

pub(crate) async fn remote_item_document(
    store: &CatalogStore,
    id: Uuid,
    hide_adult: bool,
    server: Option<&str>,
) -> CommandResult<CatalogDocument> {
    let base = catalog_base(store)?;
    if server.is_some_and(|expected| expected != base.as_str()) {
        return Err("Cambió el servidor. Volvé a buscar el título.".into());
    }
    let (doc, _) = catalog_document(
        catalog_response(&base, &format!("api/v1/catalog/items/{id}"), &[]).await?,
    )
    .await?;
    if doc.items.len() != 1 || doc.items[0].sync_id != Some(id) {
        return Err("El servidor devolvió otro título.".into());
    }
    if hide_adult && doc.items[0].adult() {
        return Err("El filtro +18 ocultó esta ficha.".into());
    }
    Ok(doc)
}

#[tauri::command]
async fn preview_online_item(
    store: State<'_, CatalogStore>,
    id: Uuid,
    hide_adult: bool,
    server: String,
) -> CommandResult<(umd_core::CatalogItem, bool)> {
    if let Some(item) = store
        .item(id)
        .map_err(|e| e.to_string())?
        .and_then(|d| d.items.into_iter().next())
    {
        if hide_adult && item.adult() {
            return Err("El filtro +18 ocultó esta ficha.".into());
        }
        return Ok((item, true));
    }
    let doc = remote_item_document(&store, id, hide_adult, Some(&server)).await?;
    Ok((doc.items.into_iter().next().unwrap(), false))
}

#[tauri::command]
async fn import_remote_item(
    store: State<'_, CatalogStore>,
    id: Uuid,
    hide_adult: Option<bool>,
    server: Option<String>,
) -> CommandResult<umd_core::CatalogItem> {
    let doc =
        remote_item_document(&store, id, hide_adult.unwrap_or(false), server.as_deref()).await?;
    let incoming = doc
        .items
        .into_iter()
        .next()
        .ok_or("El servidor devolvió una ficha vacía.")?;
    let store = store.inner().clone();
    work(move || {
        let (merged, _) = store
            .merge_remote_item(id, incoming)
            .map_err(|e| e.to_string())?;
        Ok(merged)
    })
    .await
}

async fn connect_catalog(store: &CatalogStore, address: &str) -> CommandResult<String> {
    let mut base = reqwest::Url::parse(address.trim()).map_err(|e| e.to_string())?;
    if !matches!(base.scheme(), "http" | "https")
        || base.host_str().is_none()
        || !base.username().is_empty()
        || base.password().is_some()
    {
        return Err("Use an HTTP or HTTPS server URL without credentials".into());
    }
    base.set_query(None);
    base.set_fragment(None);
    let path = base.path().trim_end_matches('/');
    let path = path.strip_suffix("/api/v1").unwrap_or(path);
    base.set_path(&format!("{path}/"));
    let response = catalog_response(
        &base,
        "api/v1/catalog/search",
        &[("browse", "true"), ("summary", "true"), ("limit", "1")],
    )
    .await?;
    catalog_document(response).await?;
    store
        .set_setting("server_url", base.as_str())
        .map_err(|e| e.to_string())?;
    Ok(base.to_string())
}

#[tauri::command]
async fn connect_server(store: State<'_, CatalogStore>, base_url: String) -> CommandResult<String> {
    connect_catalog(&store, &base_url).await
}

#[derive(serde::Serialize)]
#[serde(rename_all = "camelCase")]
struct OnlinePage {
    items: Vec<umd_core::CatalogItem>,
    next: Option<String>,
    recent: bool,
}

async fn online_page(
    store: &CatalogStore,
    query: CatalogQuery,
    recent: bool,
    hide_adult: bool,
) -> CommandResult<OnlinePage> {
    let reader = store.clone();
    let base = work(move || catalog_base(&reader)).await?;
    let result = if recent {
        let response = catalog_response(&base, "api/v1/catalog/recent", &[]).await?;
        if matches!(
            response.status(),
            reqwest::StatusCode::NOT_FOUND | reqwest::StatusCode::METHOD_NOT_ALLOWED
        ) {
            None
        } else {
            Some(catalog_document(response).await?)
        }
    } else {
        None
    };
    let ordered = result.is_some();
    let (doc, next) = if let Some(page) = result {
        page
    } else {
        let f = &query.filters;
        let year = f.year.map(|n| n.to_string());
        let mut params = vec![
            ("browse", "true"),
            ("summary", "true"),
            ("limit", if recent { "12" } else { "24" }),
        ];
        for (key, value) in [
            ("q", Some(query.q.as_str())),
            ("cursor", query.cursor.as_deref()),
            ("mediaType", query.media_type.as_deref()),
            ("genre", f.genre.as_deref()),
            ("year", year.as_deref()),
            ("category", f.category.as_deref()),
            ("status", f.status.as_deref()),
            ("provider", f.provider.as_deref()),
            ("subtypeGroup", f.subtype_group.as_deref()),
        ] {
            if let Some(v) = value.filter(|v| !v.is_empty()) {
                params.push((key, v));
            }
        }
        catalog_document(catalog_response(&base, "api/v1/catalog/search", &params).await?).await?
    };
    Ok(OnlinePage {
        items: doc
            .items
            .into_iter()
            .filter(|i| i.sync_id.is_some() && !(hide_adult && i.adult()))
            .collect(),
        next: if recent {
            None
        } else {
            next.filter(|c| !c.is_empty() && Some(c) != query.cursor.as_ref())
        },
        recent: ordered,
    })
}

#[tauri::command]
async fn browse_online_catalog(
    reader: State<'_, CatalogReads>,
    query: CatalogQuery,
    recent: bool,
    hide_adult: bool,
) -> CommandResult<OnlinePage> {
    online_page(&reader.0, query, recent, hide_adult).await
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    let builder = tauri::Builder::default();
    #[cfg(any(windows, target_os = "linux"))]
    let builder = if std::env::var_os("UMD_RUST_DESKTOP_DATA_DIRECTORY").is_none() {
        builder.plugin(tauri_plugin_single_instance::init(|app, _, _| {
            if let Some(window) = app.get_webview_window("main") {
                let _ = window.show();
                let _ = window.set_focus();
            }
        }))
    } else {
        builder
    };
    builder
        .plugin(tauri_plugin_deep_link::init())
        .register_asynchronous_uri_scheme_protocol("umdstream", torrent_stream::protocol)
        .manage(torrent_stream::Streams::default())
        .plugin(tauri_plugin_fs::init())
        .plugin(tauri_plugin_opener::init())
        .plugin(tauri_plugin_dialog::init())
        .manage(LibraryStartup::default())
        .setup(|app| {
            downloads::setup_magnets(app)?;
            app.manage(media::MediaState::default());
            app.manage(remote::RemoteState::default());
            #[cfg(any(windows, target_os = "linux", target_os = "android"))]
            app.manage(updates::Updates::default());
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            initialize_library,
            library,
            profiles,
            remote::edit_profile,
            remote::account_current,
            remote::account_sync,
            remote::account_set_automatic,
            remote::friends::friend_action,
            remote::party::party_action,
            remote::party::party_connect,
            remote::party::party_send,
            party_titles,
            remote::shared_lists::published_lists,
            remote::shared_lists::publish_list,
            remote::shared_lists::revoke_list,
            remote::shared_lists::open_shared_list,
            remote::shared_lists::save_shared_list,
            favorite,
            personal_lists,
            reader_mode,
            edit_list,
            library_item,
            resume_item,
            continue_items,
            delete_catalog_item,
            delete_unit,
            filter_choices,
            home_recent,
            shelf_units,
            downloads::add_download,
            downloads::list_downloads,
            downloads::pause_download,
            downloads::delete_download,
            downloads::download_files,
            torrent_manifest::import_download_manifest,
            downloads::catalog_download_files,
            downloads::choose_download_files,
            torrent_stream::stream_download,
            downloads::torrent_options,
            downloads::next_magnet,
            torrent_stream::close_book,
            downloads::register_magnets,
            downloads::open_download,
            import_catalog,
            export_catalog,
            database::database_copy,
            database::library_notice,
            metadata::metadata_preferences,
            metadata::metadata_search,
            metadata::metadata_apply,
            server_url,
            search_remote_catalog,
            import_remote_item,
            preview_online_item,
            #[cfg(any(windows, target_os = "linux", target_os = "android"))]
            updates::check_update,
            #[cfg(any(windows, target_os = "linux", target_os = "android"))]
            updates::install_update,
            connect_server,
            browse_online_catalog,
            native_player::native_playback,
            streams::resolve_video,
            streams::live_channels,
            providers::provider_locations,
            providers::json_provider_search,
            providers::json_provider_add,
            providers::provider_search,
            providers::provider_updates,
            providers::refresh_saved_editions,
            providers::refresh_item,
            artwork::fetch_artwork,
            providers::provider_interactive,
            chapter_lookup::find_chapter,
            providers::provider_add,
            providers::provider_unit,
            providers::open_provider_player,
            ad_filter::player_ad_filter,
            ad_filter::filter_status,
            ad_filter::open_filtered_player,
            ad_filter::playback::provider_playback,
            ad_filter::playback::provider_public_stream,
            capibara::capibara_search,
            capibara::capibara_updates,
            capibara::capibara_add,
            capibara::capibara_linked,
            capibara::capibara_pages,
            open_media,
            media::open_catalog_book,
            get_progress,
            media::item_progress,
            media::save_progress_batch,
            save_progress,
            account_login,
            account_logout,
            account_backup_info,
            account_upload,
            account_restore
        ])
        .build(tauri::generate_context!())
        .expect("Unable to start Universal Media Distribution")
        .run(|_app, _event| {
            #[cfg(windows)]
            if matches!(_event, tauri::RunEvent::Exit) {
                unsafe extern "C" {
                    fn umd_audio_shutdown();
                }
                unsafe {
                    umd_audio_shutdown();
                }
            }
        });
}

#[cfg(test)]
mod online_catalog_tests {
    use super::*;
    use axum::{extract::Query, routing::get, Json, Router};
    use std::{
        collections::HashMap,
        sync::{Arc, Mutex},
    };
    #[tokio::test]
    async fn connecting_and_browsing_leave_the_catalog_local() {
        let calls = Arc::new(Mutex::new(Vec::new()));
        let observed = calls.clone();
        let app=Router::new().route("/api/v1/catalog/search",get(move |Query(q): Query<HashMap<String,String>>| {
            let calls=observed.clone();
            async move {
                calls.lock().unwrap().push(q);
                ([("x-umd-next-cursor","next-page")],Json(serde_json::json!({"format":"universal-media-distribution.catalog","version":1,"items":[{"syncId":"00000000-0000-0000-0000-000000000001","name":"Demo","subtype":"movie","mediaType":"movie"}]})))
            }
        }));
        let socket = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let base = format!("http://{}/", socket.local_addr().unwrap());
        let server = tokio::spawn(async move { axum::serve(socket, app).await.unwrap() });
        let store = CatalogStore::in_memory().unwrap();
        assert_eq!(
            connect_catalog(&store, &format!("{base}api/v1"))
                .await
                .unwrap(),
            base
        );
        assert_eq!(calls.lock().unwrap().len(), 1);
        assert_eq!(calls.lock().unwrap()[0]["limit"], "1");
        let fallback = online_page(&store, Default::default(), true, false)
            .await
            .unwrap();
        assert!(!fallback.recent && fallback.next.is_none());
        let query = CatalogQuery {
            q: "Demo".into(),
            media_type: Some("movie".into()),
            ..Default::default()
        };
        let page = online_page(&store, query, false, false).await.unwrap();
        assert_eq!(page.items.len(), 1);
        assert_eq!(page.next.as_deref(), Some("next-page"));
        assert_eq!(calls.lock().unwrap().len(), 3);
        assert_eq!(calls.lock().unwrap()[2]["q"], "Demo");
        assert_eq!(store.page(&Default::default()).unwrap().total, 0);
        assert!(connect_catalog(&store, &format!("{base}missing/"))
            .await
            .is_err());
        assert_eq!(
            store.setting("server_url").unwrap().as_deref(),
            Some(base.as_str())
        );
        server.abort();
    }
}
