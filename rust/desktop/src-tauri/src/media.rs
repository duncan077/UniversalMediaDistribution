use crate::{work, CommandResult};
use serde::Serialize;
#[cfg(target_os = "android")]
use std::io::Read;
use std::{
    cmp::Ordering,
    path::{Path, PathBuf},
    sync::Mutex,
};
use tauri::{Manager, State};
use tauri_plugin_dialog::DialogExt;
use umd_core::{CatalogStore, ProgressTarget};

#[derive(Default)]
pub struct MediaState(Mutex<ActiveMedia>);
#[derive(Default)]
struct ActiveMedia {
    request: u64,
    cache: Option<tempfile::TempDir>,
}
impl MediaState {
    pub fn begin(&self) -> CommandResult<u64> {
        let mut active = self.0.lock().map_err(|e| e.to_string())?;
        active.request = active.request.wrapping_add(1);
        Ok(active.request)
    }
    pub fn keep(&self, request: u64, cache: Option<tempfile::TempDir>) -> CommandResult<()> {
        let mut active = self.0.lock().map_err(|e| e.to_string())?;
        if active.request != request {
            return Err("Ya se abrió otro archivo.".into());
        }
        active.cache = cache;
        Ok(())
    }
}
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OpenedMedia {
    streaming: bool,
    kind: String,
    paths: Vec<String>,
}
fn extension(path: &Path) -> String {
    path.extension()
        .and_then(|v| v.to_str())
        .unwrap_or("")
        .to_ascii_lowercase()
}
fn image(path: &Path) -> bool {
    matches!(
        extension(path).as_str(),
        "png" | "jpg" | "jpeg" | "webp" | "gif" | "avif" | "bmp"
    )
}
// Compare digit runs numerically without parsing them into bounded integers.
pub(crate) fn natural(a: &str, b: &str) -> Ordering {
    let (a, b) = (a.to_lowercase(), b.to_lowercase());
    let (a, b) = (a.as_bytes(), b.as_bytes());
    let (mut i, mut j) = (0, 0);
    while i < a.len() && j < b.len() {
        if a[i].is_ascii_digit() && b[j].is_ascii_digit() {
            let (mut x, mut y) = (i, j);
            while i < a.len() && a[i].is_ascii_digit() {
                i += 1;
            }
            while j < b.len() && b[j].is_ascii_digit() {
                j += 1;
            }
            while x < i && a[x] == b'0' {
                x += 1;
            }
            while y < j && b[y] == b'0' {
                y += 1;
            }
            let order = (i - x).cmp(&(j - y)).then_with(|| a[x..i].cmp(&b[y..j]));
            if !order.is_eq() {
                return order;
            }
        } else {
            let order = a[i].cmp(&b[j]);
            if !order.is_eq() {
                return order;
            }
            i += 1;
            j += 1;
        }
    }
    a.len().cmp(&b.len()).then_with(|| a.cmp(b))
}
const MAX_COMIC_ENTRIES: usize = 10_000;
const MAX_PAGE_BYTES: u64 = 64 * 1024 * 1024;
const MAX_COMIC_BYTES: u64 = 512 * 1024 * 1024;

fn cbr_pages(path: &Path, cache: &Path) -> CommandResult<(tempfile::TempDir, Vec<PathBuf>)> {
    let options = rars::ArchiveReadOptions::default()
        .with_max_header_count(MAX_COMIC_ENTRIES as u64 + 2)
        .with_max_header_bytes(16 * 1024 * 1024)
        .with_max_member_output_bytes(MAX_PAGE_BYTES)
        .with_max_total_output_bytes(MAX_COMIC_BYTES)
        .with_rar50_dictionary_size_limit(if cfg!(target_os = "android") {
            32 << 20
        } else {
            MAX_PAGE_BYTES
        })
        .with_max_reader_workspace_bytes(if cfg!(target_os = "android") {
            32 << 20
        } else {
            128 << 20
        });
    let explain = |error: rars::Error| match error.kind() {
        rars::ErrorKind::ResourceLimit => {
            "El CBR supera los límites de tamaño del lector.".to_owned()
        }
        rars::ErrorKind::PasswordRequired | rars::ErrorKind::BadPassword => {
            "El lector necesita una copia del CBR sin contraseña.".to_owned()
        }
        _ => format!("No se pudo abrir el CBR. {error}"),
    };
    let archive = rars::ArchiveReader::read_path_with_options(path, options).map_err(explain)?;
    let temp = tempfile::tempdir_in(cache).map_err(|e| e.to_string())?;
    let mut pages = Vec::new();
    archive
        .extract_to_with_options(options, |entry| {
            let name = entry.name_lossy();
            let original = Path::new(&name);
            let special = match entry.attr_source {
                rars::AttrSource::Unix => !matches!(entry.file_attr & 0o170000, 0 | 0o100000),
                rars::AttrSource::Dos => entry.file_attr & 0x400 != 0,
                _ => false,
            };
            if entry.is_directory || special || !image(original) {
                return Ok(Box::new(std::io::sink()));
            }
            let file = temp
                .path()
                .join(format!("{:05}.{}", pages.len(), extension(original)));
            let writer = std::fs::File::create(&file)?;
            pages.push((name, file));
            Ok(Box::new(writer))
        })
        .map_err(explain)?;
    if pages.is_empty() {
        return Err("No se encontraron imágenes en el cómic.".into());
    }
    pages.sort_by(|a, b| natural(&a.0, &b.0));
    Ok((temp, pages.into_iter().map(|(_, file)| file).collect()))
}

#[derive(Clone, Copy, serde::Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum BookArchive {
    Cbr,
    Cbz,
    Epub,
}

async fn download_book(address: &str, server: Option<&str>, path: &Path) -> CommandResult<()> {
    use reqwest::{header::LOCATION, Url};
    use std::time::Duration;
    use tokio::io::AsyncWriteExt;
    let mut url = Url::parse(address).map_err(|_| "Dirección del libro inválida.")?;
    let origin = server.and_then(|s| Url::parse(s).ok()).map(|u| u.origin());
    let timeout = Duration::from_secs(120);
    let local = reqwest::Client::builder()
        .no_proxy()
        .timeout(timeout)
        .redirect(reqwest::redirect::Policy::none())
        .build()
        .map_err(|e| e.to_string())?;
    for _ in 0..6 {
        if !matches!(url.scheme(), "http" | "https")
            || url.host_str().is_none()
            || !url.username().is_empty()
            || url.password().is_some()
        {
            return Err("El libro requiere una dirección HTTP o HTTPS sin credenciales.".into());
        }
        let mut response = if origin.as_ref() == Some(&url.origin()) {
            local
                .get(url.clone())
                .send()
                .await
                .map_err(|_| "No se pudo descargar el libro del servidor.")?
        } else {
            crate::streams::get_public(url.clone(), timeout).await?
        };
        if response.status().is_redirection() {
            let location = response
                .headers()
                .get(LOCATION)
                .and_then(|v| v.to_str().ok())
                .ok_or("Redirección del libro inválida.")?;
            url = url
                .join(location)
                .map_err(|_| "Redirección del libro inválida.")?;
            continue;
        }
        if !response.status().is_success() {
            return Err(format!(
                "El servidor respondió HTTP {} al descargar el libro.",
                response.status().as_u16()
            ));
        }
        let too_large = "La descarga del libro supera 512 MiB.";
        if response
            .content_length()
            .is_some_and(|size| size > MAX_COMIC_BYTES)
        {
            return Err(too_large.into());
        }
        let mut output = tokio::fs::File::create(path)
            .await
            .map_err(|e| e.to_string())?;
        let mut received = 0u64;
        while let Some(chunk) = response
            .chunk()
            .await
            .map_err(|_| "Se interrumpió la descarga del libro.")?
        {
            received += chunk.len() as u64;
            if received > MAX_COMIC_BYTES {
                return Err(too_large.into());
            }
            output.write_all(&chunk).await.map_err(|e| e.to_string())?;
        }
        return output.flush().await.map_err(|e| e.to_string());
    }
    Err("Demasiadas redirecciones al descargar el libro.".into())
}

#[tauri::command]
pub async fn open_catalog_book(
    address: String,
    format: BookArchive,
    app: tauri::AppHandle,
    store: State<'_, CatalogStore>,
    media: State<'_, MediaState>,
) -> CommandResult<OpenedMedia> {
    static IN_FLIGHT: tokio::sync::Semaphore = tokio::sync::Semaphore::const_new(2);
    let _slot = IN_FLIGHT
        .try_acquire()
        .map_err(|_| "Ya hay dos libros abriéndose. Volvé a intentar.")?;
    let request = media.begin()?;
    let cache = app.path().app_cache_dir().map_err(|e| e.to_string())?;
    tokio::fs::create_dir_all(&cache)
        .await
        .map_err(|e| e.to_string())?;
    let download = tempfile::tempdir_in(&cache).map_err(|e| e.to_string())?;
    let path = download.path().join(match format {
        BookArchive::Cbr => "book.cbr",
        BookArchive::Cbz => "book.cbz",
        BookArchive::Epub => "book.epub",
    });
    let server = store.setting("server_url").map_err(|e| e.to_string())?;
    tokio::time::timeout(
        std::time::Duration::from_secs(180),
        download_book(&address, server.as_deref(), &path),
    )
    .await
    .map_err(|_| "Se agotó el tiempo al descargar el libro.")??;
    let (opened, extracted) = work(move || open_downloaded_file(&app, &path, &cache)).await?;
    media.keep(
        request,
        extracted.or_else(|| opened.streaming.then_some(download)),
    )?;
    Ok(opened)
}

pub(super) fn open_image_sequence(
    app: &tauri::AppHandle,
    paths: Vec<PathBuf>,
) -> CommandResult<OpenedMedia> {
    if paths.is_empty() || paths.len() > 2000 || paths.iter().any(|p| !image(p)) {
        return Err("La secuencia contiene un archivo que no es una imagen.".into());
    }
    for path in &paths {
        app.asset_protocol_scope()
            .allow_file(path)
            .map_err(|e| e.to_string())?;
    }
    Ok(OpenedMedia {
        streaming: false,
        kind: "images".into(),
        paths: paths
            .into_iter()
            .map(|p| p.to_string_lossy().into_owned())
            .collect(),
    })
}
pub fn open_downloaded_file(
    app: &tauri::AppHandle,
    path: &Path,
    cache: &Path,
) -> CommandResult<(OpenedMedia, Option<tempfile::TempDir>)> {
    std::fs::create_dir_all(cache).map_err(|e| e.to_string())?;
    let mut temp = None;
    let mut ext = extension(path);
    if ext == "zip"
        && zip::ZipArchive::new(std::fs::File::open(path).map_err(|e| e.to_string())?)
            .is_ok_and(|zip| zip.index_for_name("META-INF/container.xml").is_some())
    {
        ext = "epub".into();
    }
    let (kind, paths) = match ext.as_str() {
        "epub" | "cbz" | "zip" => {
            let epub = ext == "epub";
            let paths = crate::torrent_stream::open_local_archive(app, path, epub)?;
            return Ok((
                OpenedMedia {
                    kind: if epub { "epub" } else { "images" }.into(),
                    paths,
                    streaming: true,
                },
                None,
            ));
        }
        "cbr" | "rar" => {
            let (dir, pages) = cbr_pages(path, cache)?;
            temp = Some(dir);
            ("images", pages)
        }
        "pdf" => ("pdf", vec![path.to_owned()]),
        _ if image(path) => ("images", vec![path.to_owned()]),
        "mp3" | "m4a" | "aac" | "flac" | "wav" | "ogg" | "opus" => ("audio", vec![path.to_owned()]),
        "mp4" | "m4v" | "webm" | "mkv" => ("video", vec![path.to_owned()]),
        _ => return Err("Formato no compatible.".into()),
    };
    for path in &paths {
        app.asset_protocol_scope()
            .allow_file(path)
            .map_err(|e| e.to_string())?;
    }
    Ok((
        OpenedMedia {
            streaming: false,
            kind: kind.into(),
            paths: paths
                .into_iter()
                .map(|p| p.to_string_lossy().into_owned())
                .collect(),
        },
        temp,
    ))
}

#[tauri::command]
pub async fn open_media(
    app: tauri::AppHandle,
    folder: bool,
    media: State<'_, MediaState>,
) -> CommandResult<Option<OpenedMedia>> {
    let request = media.begin()?;
    let (result, temp) = work(move || {
        let dialog = app.dialog().file();
        let cache = app.path().app_cache_dir().map_err(|e| e.to_string())?;
        std::fs::create_dir_all(&cache).map_err(|e| e.to_string())?;
        #[cfg(desktop)]
        let temp = None;
        #[cfg(mobile)]
        let temp;
        #[cfg(desktop)]
        let path = {
            let selected = if folder {
                dialog.blocking_pick_folder()
            } else {
                dialog
                    .add_filter(
                        "Audio, video, PDF o cómic",
                        &[
                            "mp4", "m4v", "webm", "mkv", "mp3", "m4a", "aac", "flac", "wav", "ogg",
                            "opus", "pdf", "epub", "cbz", "zip", "cbr", "rar", "png", "jpg",
                            "jpeg", "webp", "avif",
                        ],
                    )
                    .blocking_pick_file()
            };
            let Some(selected) = selected else {
                return Ok((None, None));
            };
            selected
                .into_path()
                .map_err(|e| e.to_string())?
                .canonicalize()
                .map_err(|e| e.to_string())?
        };
        #[cfg(mobile)]
        let path = {
            let files = if folder {
                dialog
                    .add_filter(
                        "Imágenes",
                        &["png", "jpg", "jpeg", "webp", "gif", "avif", "bmp"],
                    )
                    .blocking_pick_files()
            } else {
                dialog.blocking_pick_file().map(|f| vec![f])
            };
            let Some(files) = files else {
                return Ok((None, None));
            };
            if files.is_empty() {
                return Ok((None, None));
            }
            if files.len() > 10_000 {
                return Err("Demasiados archivos seleccionados.".into());
            }
            let directory = tempfile::tempdir_in(&cache).map_err(|e| e.to_string())?;
            let mut path = directory.path().to_owned();
            let mut remaining = if folder {
                512 * 1024 * 1024u64
            } else {
                4 * 1024 * 1024 * 1024u64
            };
            for (i, selected) in files.into_iter().enumerate() {
                let mut opts = tauri_plugin_fs::OpenOptions::new();
                opts.read(true);
                let (file_path, size) = crate::with_document(&app, selected, opts, |mut f| {
                    let mut header = Vec::new();
                    (&mut f)
                        .take(8192)
                        .read_to_end(&mut header)
                        .map_err(|e| e.to_string())?;
                    let ext = infer::get(&header)
                        .ok_or("Formato de archivo desconocido.")?
                        .extension();
                    let path = directory.path().join(format!("{i:05}.{ext}"));
                    if folder && !image(&path) {
                        return Err("Seleccioná solamente imágenes.".into());
                    }
                    let mut destination =
                        std::fs::File::create(&path).map_err(|e| e.to_string())?;
                    let size = std::io::copy(
                        &mut header.as_slice().chain(f).take(remaining + 1),
                        &mut destination,
                    )
                    .map_err(|e| e.to_string())?;
                    if size > remaining {
                        return Err(
                            "La selección supera el espacio permitido para abrir archivos.".into(),
                        );
                    }
                    Ok((path, size))
                })?;
                remaining -= size;
                if !folder {
                    path = file_path;
                }
            }
            temp = Some(directory);
            path
        };
        if !folder {
            let (opened, extracted) = open_downloaded_file(&app, &path, &cache)?;
            return Ok((Some(opened), extracted.or(temp)));
        }
        let (kind, mut paths) = {
            let mut paths = Vec::new();
            for (index, entry) in std::fs::read_dir(&path)
                .map_err(|e| e.to_string())?
                .take(10_001)
                .enumerate()
            {
                if index == 10_000 {
                    return Err("La carpeta contiene demasiadas entradas (máximo 10.000).".into());
                }
                let entry = entry.map_err(|e| e.to_string())?;
                // Do not follow symlinks to files outside the selected folder.
                if entry.file_type().map_err(|e| e.to_string())?.is_file() && image(&entry.path()) {
                    paths.push(entry.path());
                }
            }
            if paths.len() > 10_000 {
                return Err("Demasiadas páginas (máximo 10.000).".into());
            }
            ("images", paths)
        };
        if paths.is_empty() {
            return Err("La carpeta no contiene imágenes compatibles.".into());
        }
        paths.sort_by(|a, b| natural(&a.to_string_lossy(), &b.to_string_lossy()));
        for path in &paths {
            app.asset_protocol_scope()
                .allow_file(path)
                .map_err(|e| e.to_string())?;
        }
        Ok((
            Some(OpenedMedia {
                streaming: false,
                kind: kind.into(),
                paths: paths
                    .iter()
                    .map(|p| p.to_string_lossy().into_owned())
                    .collect(),
            }),
            temp,
        ))
    })
    .await?;
    if result.is_some() {
        media.keep(request, temp)?;
    }
    Ok(result)
}
#[tauri::command]
pub async fn get_progress(
    target: ProgressTarget,
    profile_id: uuid::Uuid,
    reader: State<'_, crate::CatalogReads>,
) -> CommandResult<Option<serde_json::Value>> {
    let store = reader.0.get();
    work(move || {
        store
            .for_profile(profile_id)
            .and_then(|store| store.progress(&target))
            .map_err(|e| e.to_string())
    })
    .await
}
#[tauri::command]
pub async fn item_progress(
    item_id: uuid::Uuid,
    profile_id: uuid::Uuid,
    reader: State<'_, crate::CatalogReads>,
) -> CommandResult<Vec<serde_json::Value>> {
    let store = reader.0.get();
    work(move || {
        store
            .for_profile(profile_id)
            .and_then(|store| store.item_progress(item_id))
            .map_err(|e| e.to_string())
    })
    .await
}
#[derive(serde::Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ProgressBatchEntry {
    target: ProgressTarget,
    position: f64,
    kind: String,
    completed: bool,
}
#[tauri::command]
pub async fn save_progress_batch(
    entries: Vec<ProgressBatchEntry>,
    profile_id: uuid::Uuid,
    store: State<'_, CatalogStore>,
) -> CommandResult<()> {
    let store = store.for_profile(profile_id).map_err(|e| e.to_string())?;
    work(move || {
        let mapped: Vec<_> = entries
            .into_iter()
            .map(|e| (e.target, e.position, e.kind, e.completed))
            .collect();
        store
            .save_progress_batch(&mapped)
            .map_err(|e| e.to_string())
    })
    .await
}
#[tauri::command]
pub async fn save_progress(
    target: ProgressTarget,
    position: f64,
    kind: String,
    completed: bool,
    profile_id: uuid::Uuid,
    store: State<'_, CatalogStore>,
) -> CommandResult<()> {
    let store = store.for_profile(profile_id).map_err(|e| e.to_string())?;
    work(move || {
        store
            .save_progress(&target, position, &kind, completed)
            .map_err(|e| e.to_string())
    })
    .await
}
#[cfg(test)]
mod tests {
    use super::*;
    #[tokio::test]
    async fn reads_comics_from_the_connected_catalog_server() {
        use axum::{routing::get, Router};
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let origin = format!("http://{}", listener.local_addr().unwrap());
        let route = Router::new()
            .route(
                "/book",
                get(|| async { include_bytes!("../tests/fixtures/pages-rar5.cbr").as_slice() }),
            )
            .route(
                "/moved",
                get(|| async { axum::response::Redirect::temporary("/book") }),
            );
        let server = tokio::spawn(async move { axum::serve(listener, route).await.unwrap() });
        let cache = tempfile::tempdir().unwrap();
        let file = cache.path().join("received.cbr");
        let address = format!("{origin}/moved");
        assert!(download_book(&address, None, &file).await.is_err());
        assert!(!file.exists());
        download_book(&address, Some(&origin), &file).await.unwrap();
        assert_eq!(cbr_pages(&file, cache.path()).unwrap().1.len(), 2);
        server.abort();
    }
    #[test]
    fn opens_rar4_and_rar5_comics_in_reading_order() {
        let cache = tempfile::tempdir().unwrap();
        for (archive, signature) in [
            ("pages-rar4.cbr", b"Rar!\x1a\x07\x00".as_slice()),
            ("pages-rar5.cbr", b"Rar!\x1a\x07\x01\x00".as_slice()),
        ] {
            let fixture = Path::new(env!("CARGO_MANIFEST_DIR"))
                .join("tests/fixtures")
                .join(archive);
            assert!(std::fs::read(&fixture).unwrap().starts_with(signature));
            let (dir, pages) = cbr_pages(&fixture, cache.path()).unwrap();
            assert_eq!(pages.len(), 2);
            for (path, width) in pages.iter().zip([2, 10]) {
                assert_eq!(path.parent(), Some(dir.path()));
                let png = std::fs::read(path).unwrap();
                assert_eq!(&png[..8], b"\x89PNG\r\n\x1a\n");
                assert_eq!(u32::from_be_bytes(png[16..20].try_into().unwrap()), width);
            }
        }
        assert_eq!(std::fs::read_dir(cache.path()).unwrap().count(), 0);
    }

    #[test]
    fn sorts_pages_by_number() {
        let mut v = vec!["p10.png", "p2.png", "p1.png"];
        v.sort_by(|a, b| natural(a, b));
        assert_eq!(v, vec!["p1.png", "p2.png", "p10.png"]);
    }
}
