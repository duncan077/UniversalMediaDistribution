use crate::{downloads::Downloads, CommandResult};
use librqbit::{api::TorrentIdOrHash, Api, Session};
use serde::Serialize;
use std::{
    io::SeekFrom,
    sync::{Arc, Mutex},
    time::Duration,
};
use tauri::{
    http::{Request, Response},
    Manager, State,
};
use tokio::io::{AsyncReadExt, AsyncSeekExt};
use uuid::Uuid;

const CHUNK: u64 = 1024 * 1024;
#[derive(Clone)]
struct File {
    token: Uuid,
    session: Arc<Session>,
    torrent: usize,
    index: usize,
    length: u64,
    mime: &'static str,
}
#[derive(Default)]
pub struct Streams(
    Mutex<Option<File>>,
    tokio::sync::OnceCell<u16>,
    Mutex<std::collections::VecDeque<(Uuid, Arc<Mutex<Archive>>)>>,
);
#[derive(Serialize)]
pub struct Opened {
    kind: &'static str,
    paths: Vec<String>,
    streaming: bool,
}
fn media(name: &str) -> Option<(&'static str, &'static str)> {
    match name.rsplit_once('.')?.1.to_ascii_lowercase().as_str() {
        "mp4" | "m4v" => Some(("video", "video/mp4")),
        "webm" => Some(("video", "video/webm")),
        "mkv" => Some(("video", "video/x-matroska")),
        "mov" => Some(("video", "video/quicktime")),
        "mp3" => Some(("audio", "audio/mpeg")),
        "aac" => Some(("audio", "audio/aac")),
        "m4a" => Some(("audio", "audio/mp4")),
        "flac" => Some(("audio", "audio/flac")),
        "pdf" => Some(("pdf", "application/pdf")),
        "opus" => Some(("audio", "audio/ogg")),
        "ogg" => Some(("audio", "audio/ogg")),
        "wav" => Some(("audio", "audio/wav")),
        _ => None,
    }
}
impl Streams {
    fn open_archive(&self, archive: Archive, paths: Vec<String>) -> CommandResult<Vec<String>> {
        let mut books = self.2.lock().map_err(|_| "Lector ocupado.")?;
        let token = Uuid::new_v4();
        if books.len() >= 8 {
            books.pop_front();
        }
        books.push_back((token, Arc::new(Mutex::new(archive))));
        Ok(paths
            .into_iter()
            .map(|name| format!("{token}/{name}"))
            .collect())
    }
}
#[tauri::command]
pub fn close_book(streams: State<'_, Streams>, id: Uuid) {
    if let Ok(mut books) = streams.2.lock() {
        books.retain(|(token, _)| *token != id);
    }
}
pub(super) fn open_epub(
    app: &tauri::AppHandle,
    path: &std::path::Path,
) -> CommandResult<Vec<String>> {
    let reader = std::fs::File::open(path).map_err(|e| e.to_string())?;
    let (archive, names) = Archive::open(Box::new(reader), true)?;
    app.state::<Streams>().open_archive(archive, names)
}
enum Archive {
    Epub(crate::epub::Book, tempfile::TempDir),
    Comic(
        zip::ZipArchive<Box<dyn crate::epub::ArchiveInput>>,
        Vec<(usize, String, &'static str)>,
    ),
}
impl Archive {
    fn open(
        reader: Box<dyn crate::epub::ArchiveInput>,
        epub: bool,
    ) -> CommandResult<(Self, Vec<String>)> {
        if epub {
            let dir = tempfile::tempdir().map_err(|e| e.to_string())?;
            let book = crate::epub::Book::open(reader, dir.path().to_owned())?;
            let paths = (0..book.titles.len())
                .map(|i| format!("section-{i}.json"))
                .collect();
            return Ok((Self::Epub(book, dir), paths));
        }
        let archive = zip::ZipArchive::new(reader).map_err(|_| "CBZ inválido.")?;
        if archive.len() > 10_000 || archive.decompressed_size().unwrap_or(u128::MAX) > 512 << 20 {
            return Err("El CBZ supera los límites de lectura.".into());
        }
        let mut pages = Vec::new();
        for name in archive.file_names() {
            if let (Some(mime), Some(index)) = (raster(name), archive.index_for_name(name)) {
                pages.push((index, name.to_owned(), mime));
            }
        }
        if pages.is_empty() {
            return Err("El CBZ no contiene imágenes.".into());
        }
        pages.sort_by(|a, b| crate::media::natural(&a.1, &b.1));
        let paths = (0..pages.len()).map(|i| format!("page-{i}")).collect();
        Ok((Self::Comic(archive, pages), paths))
    }
    fn resource(&mut self, name: &str) -> CommandResult<(Vec<u8>, &'static str)> {
        match self {
            Self::Epub(book, _folder) => {
                if name == "contents.json" {
                    return Ok((
                        serde_json::to_vec(&book.titles).map_err(|e| e.to_string())?,
                        "application/json",
                    ));
                }
                if let Some(i) = name
                    .strip_prefix("section-")
                    .and_then(|s| s.strip_suffix(".json"))
                    .and_then(|s| s.parse::<usize>().ok())
                {
                    return Ok((book.section(i)?, "application/json"));
                }
                let mime = raster(name).ok_or("Imagen desconocida.")?;
                let number = name
                    .strip_prefix("image-")
                    .and_then(|s| s.split_once('.'))
                    .ok_or("Imagen inválida.")?
                    .0;
                if number.parse::<usize>().is_err()
                    || !name
                        .bytes()
                        .all(|b| b.is_ascii_alphanumeric() || b == b'.' || b == b'-')
                {
                    return Err("Imagen inválida.".into());
                }
                Ok((book.image(name)?, mime))
            }
            Self::Comic(zip, pages) => {
                let page = name
                    .strip_prefix("page-")
                    .and_then(|s| s.parse::<usize>().ok())
                    .and_then(|i| pages.get(i))
                    .ok_or("Página desconocida.")?;
                let file = zip
                    .by_index(page.0)
                    .map_err(|_| "No se pudo leer el CBZ.")?;
                if file.size() > 64 << 20 {
                    return Err("Página demasiado grande.".into());
                }
                let mut data = Vec::new();
                use std::io::Read;
                file.take((64 << 20) + 1)
                    .read_to_end(&mut data)
                    .map_err(|e| e.to_string())?;
                if data.len() > 64 << 20 {
                    return Err("Página demasiado grande.".into());
                }
                Ok((data, page.2))
            }
        }
    }
}
fn raster(name: &str) -> Option<&'static str> {
    match name.rsplit_once('.')?.1.to_ascii_lowercase().as_str() {
        "png" => Some("image/png"),
        "jpg" | "jpeg" => Some("image/jpeg"),
        "webp" => Some("image/webp"),
        "avif" => Some("image/avif"),
        "gif" => Some("image/gif"),
        _ => None,
    }
}
#[tauri::command]
pub async fn stream_download(
    id: usize,
    index: usize,
    downloads: State<'_, Downloads>,
    streams: State<'_, Streams>,
) -> CommandResult<Opened> {
    let (session, id) = downloads.for_id(id).await?;
    let details = Api::new(session.clone(), None)
        .api_torrent_details(TorrentIdOrHash::Id(id))
        .map_err(|_| "La descarga todavía no tiene metadatos.")?;
    let entry = details
        .files
        .as_ref()
        .and_then(|f| f.get(index))
        .filter(|f| f.included && f.length > 0)
        .ok_or("Archivo no disponible.")?;
    let ext = entry
        .name
        .rsplit('.')
        .next()
        .unwrap_or("")
        .to_ascii_lowercase();
    if matches!(ext.as_str(), "epub" | "cbz") {
        let input = Api::new(session.clone(), None)
            .api_stream(TorrentIdOrHash::Id(id), index)
            .await
            .map_err(|_| "Archivo no disponible.")?;
        let reader =
            std::io::BufReader::with_capacity(64 << 10, tokio_util::io::SyncIoBridge::new(input));
        let epub = ext == "epub";
        let (archive, names) = crate::work(move || Archive::open(Box::new(reader), epub)).await?;
        return Ok(Opened {
            kind: if epub { "epub" } else { "images" },
            paths: streams.open_archive(archive, names)?,
            streaming: true,
        });
    }
    let (kind, mime) = media(&entry.name).ok_or("El formato requiere que la descarga termine.")?;
    let token = Uuid::new_v4();
    *streams.0.lock().map_err(|_| "Reproductor ocupado.")? = Some(File {
        token,
        session: session.clone(),
        torrent: id,
        index,
        length: entry.length,
        mime,
    });
    Ok(Opened {
        kind,
        paths: vec![token.to_string()],
        streaming: true,
    })
}
fn byte_range(range: &str, length: u64) -> Option<(u64, u64)> {
    let (a, b) = range.strip_prefix("bytes=")?.split_once('-')?;
    if length == 0 {
        return None;
    }
    if a.is_empty() {
        let n = b.parse::<u64>().ok()?.min(length);
        return (n > 0).then(|| (length - n, length - 1));
    }
    let from = a.parse::<u64>().ok()?;
    let end = if b.is_empty() {
        length - 1
    } else {
        b.parse::<u64>().ok()?.min(length - 1)
    };
    (from <= end).then_some((from, end))
}
pub(crate) async fn native_url(app: &tauri::AppHandle, token: Uuid) -> CommandResult<String> {
    let streams = app.state::<Streams>();
    if streams
        .0
        .lock()
        .map_err(|_| "Reproductor ocupado.")?
        .as_ref()
        .is_none_or(|f| f.token != token)
    {
        return Err("La descarga seleccionada cambió.".into());
    }
    let port = streams
        .1
        .get_or_try_init(|| async {
            let listener =
                tokio::net::TcpListener::bind((std::net::Ipv4Addr::LOCALHOST, 0)).await?;
            let port = listener.local_addr()?.port();
            let route = axum::Router::new()
                .route("/{token}", axum::routing::get(native_range))
                .with_state(app.clone());
            tauri::async_runtime::spawn(async move {
                let _ = axum::serve(listener, route).await;
            });
            Ok::<_, std::io::Error>(port)
        })
        .await
        .map_err(|e| e.to_string())?;
    Ok(format!("http://127.0.0.1:{port}/{token}"))
}
async fn native_range(
    axum::extract::State(app): axum::extract::State<tauri::AppHandle>,
    axum::extract::Path(token): axum::extract::Path<Uuid>,
    method: axum::http::Method,
    headers: axum::http::HeaderMap,
) -> axum::response::Response {
    use axum::{body::Body, http::StatusCode, response::IntoResponse};
    let selected = app
        .state::<Streams>()
        .0
        .lock()
        .ok()
        .and_then(|s| s.clone())
        .filter(|f| f.token == token);
    let Some(f) = selected else {
        return StatusCode::NOT_FOUND.into_response();
    };
    let range = headers.get("range");
    let Some((start, end)) = range.map_or(Some((0, f.length - 1)), |v| {
        byte_range(v.to_str().ok()?, f.length)
    }) else {
        return (
            StatusCode::RANGE_NOT_SATISFIABLE,
            [("content-range", format!("bytes */{}", f.length))],
        )
            .into_response();
    };
    let size = end - start + 1;
    let mut response = Response::builder()
        .status(if range.is_some() { 206 } else { 200 })
        .header("content-type", f.mime)
        .header("accept-ranges", "bytes")
        .header("cache-control", "no-store")
        .header("content-length", size);
    if range.is_some() {
        response = response.header("content-range", format!("bytes {start}-{end}/{}", f.length));
    }
    if method == axum::http::Method::HEAD {
        return response.body(Body::empty()).unwrap();
    }
    let Ok(mut stream) = Api::new(f.session, None)
        .api_stream(TorrentIdOrHash::Id(f.torrent), f.index)
        .await
    else {
        return StatusCode::SERVICE_UNAVAILABLE.into_response();
    };
    if stream.seek(SeekFrom::Start(start)).await.is_err() {
        return StatusCode::SERVICE_UNAVAILABLE.into_response();
    }
    response
        .body(Body::from_stream(tokio_util::io::ReaderStream::new(
            stream.take(size),
        )))
        .unwrap()
}
fn status(n: u16) -> Response<Vec<u8>> {
    Response::builder().status(n).body(vec![]).unwrap()
}
async fn serve(app: tauri::AppHandle, req: Request<Vec<u8>>) -> Response<Vec<u8>> {
    if !matches!(req.method().as_str(), "HEAD" | "GET") {
        return status(405);
    }
    let path = percent_encoding::percent_decode_str(req.uri().path().trim_start_matches('/'))
        .decode_utf8_lossy();
    if let Some((token, name)) = path.split_once('/') {
        let Some((token, name)) = Uuid::parse_str(token).ok().map(|id| (id, name.to_owned()))
        else {
            return status(404);
        };
        let archive = app.state::<Streams>().2.lock().ok().and_then(|book| {
            book.iter()
                .find(|(id, _)| *id == token)
                .map(|(_, archive)| archive.clone())
        });
        let Some(archive) = archive else {
            return status(404);
        };
        let resource = crate::work(move || {
            archive
                .lock()
                .map_err(|_| "Lector ocupado.")?
                .resource(&name)
        })
        .await;
        return match resource {
            Ok((bytes, mime)) => Response::builder()
                .header("content-type", mime)
                .header("cache-control", "no-store")
                .body(bytes)
                .unwrap(),
            Err(_) => status(404),
        };
    }
    let token = req
        .uri()
        .path()
        .trim_start_matches('/')
        .parse::<Uuid>()
        .ok();
    let selected = app
        .state::<Streams>()
        .0
        .lock()
        .ok()
        .and_then(|s| s.clone())
        .filter(|f| Some(f.token) == token);
    let Some(f) = selected else {
        return status(404);
    };
    let header = req.headers().get("Range");
    let Some((start, end)) = header.map_or(Some((0, f.length - 1)), |s| {
        byte_range(s.to_str().ok()?, f.length)
    }) else {
        return Response::builder()
            .status(416)
            .header("Content-Range", format!("bytes */{}", f.length))
            .body(vec![])
            .unwrap();
    };
    let head = req.method().as_str() == "HEAD";
    let end = if head {
        end
    } else {
        end.min(start.saturating_add(CHUNK - 1))
    };
    let size = end - start + 1;
    let partial = header.is_some() || size < f.length;
    let mut res = Response::builder()
        .status(if partial { 206 } else { 200 })
        .header("Content-Type", f.mime)
        .header("Accept-Ranges", "bytes")
        .header("Cache-Control", "no-store")
        .header("Content-Length", size);
    if partial {
        res = res.header("Content-Range", format!("bytes {start}-{end}/{}", f.length));
    }
    if head {
        return res.body(vec![]).unwrap();
    }
    static READS: tokio::sync::Semaphore = tokio::sync::Semaphore::const_new(8);
    let Ok(_permit) = READS.try_acquire() else {
        return status(503);
    };
    let content = tokio::time::timeout(Duration::from_secs(90), async {
        let mut file = Api::new(f.session, None)
            .api_stream(TorrentIdOrHash::Id(f.torrent), f.index)
            .await
            .ok()?;
        file.seek(SeekFrom::Start(start)).await.ok()?;
        let mut bytes = vec![0; size as usize];
        file.read_exact(&mut bytes).await.ok()?;
        Some(bytes)
    })
    .await;
    match content {
        Ok(Some(bytes)) => res.body(bytes).unwrap(),
        Ok(None) => status(503),
        Err(_) => status(504),
    }
}
pub fn protocol(
    ctx: tauri::UriSchemeContext<'_, tauri::Wry>,
    req: Request<Vec<u8>>,
    res: tauri::UriSchemeResponder,
) {
    let app = ctx.app_handle().clone();
    if ctx.webview_label() != "main" {
        res.respond(status(403));
        return;
    }
    tauri::async_runtime::spawn(async move {
        let mut response = serve(app, req).await;
        response
            .headers_mut()
            .insert("access-control-allow-origin", "*".parse().unwrap());
        response.headers_mut().insert(
            "access-control-expose-headers",
            "Content-Length, Content-Range, Accept-Ranges"
                .parse()
                .unwrap(),
        );
        res.respond(response)
    });
}
#[cfg(test)]
mod tests {
    use super::*;
    #[tokio::test(flavor = "multi_thread", worker_threads = 2)]
    async fn reads_a_comic_page_from_a_peer_before_the_torrent_finishes() {
        use crate::downloads::tests::local_session;
        use librqbit::{
            create_torrent, spawn_utils::BlockingSpawner, AddTorrent, AddTorrentOptions,
            CreateTorrentOptions,
        };
        use std::{io::Write, num::NonZero};
        let src = tempfile::tempdir().unwrap();
        let dst = tempfile::tempdir().unwrap();
        let path = src.path().join("comic.cbz");
        let mut zip = zip::ZipWriter::new(std::fs::File::create(&path).unwrap());
        let options = zip::write::SimpleFileOptions::default()
            .compression_method(zip::CompressionMethod::Stored);
        zip.start_file("01.png", options).unwrap();
        zip.write_all(b"first page").unwrap();
        zip.start_file("02.png", options).unwrap();
        zip.write_all(&vec![42; 8 << 20]).unwrap();
        drop(zip.finish().unwrap());
        let meta = create_torrent(
            &path,
            CreateTorrentOptions {
                piece_length: Some(16384),
                ..Default::default()
            },
            &BlockingSpawner::new(1),
        )
        .await
        .unwrap()
        .as_bytes()
        .unwrap();
        let seed = local_session(src.path().into()).await;
        let client = local_session(dst.path().into()).await;
        let seeded = seed
            .add_torrent(
                AddTorrent::from_bytes(meta.clone()),
                Some(AddTorrentOptions {
                    overwrite: true,
                    ..Default::default()
                }),
            )
            .await
            .unwrap()
            .into_handle()
            .unwrap();
        tokio::time::timeout(Duration::from_secs(15), seeded.wait_until_completed())
            .await
            .unwrap()
            .unwrap();
        let transfer = client
            .add_torrent(
                AddTorrent::from_bytes(meta),
                Some(AddTorrentOptions {
                    initial_peers: Some(vec![seed.listen_addr().unwrap()]),
                    ratelimits: librqbit::limits::LimitsConfig {
                        download_bps: NonZero::new(512 << 10),
                        ..Default::default()
                    },
                    ..Default::default()
                }),
            )
            .await
            .unwrap()
            .into_handle()
            .unwrap();
        let input = Api::new(client.clone(), None)
            .api_stream(TorrentIdOrHash::Id(transfer.id()), 0)
            .await
            .unwrap();
        let reader =
            std::io::BufReader::with_capacity(64 << 10, tokio_util::io::SyncIoBridge::new(input));
        let first = tokio::task::spawn_blocking(move || {
            let (mut book, names) = Archive::open(Box::new(reader), false).unwrap();
            book.resource(&names[0]).unwrap().0
        });
        let result = tokio::time::timeout(Duration::from_secs(35), first).await;
        let stats = transfer.stats();
        client.stop().await;
        seed.stop().await;
        assert_eq!(result.unwrap().unwrap(), b"first page");
        assert!(stats.progress_bytes < stats.total_bytes);
    }

    #[test]
    fn ranges_handle_seeking_and_suffixes() {
        assert_eq!(byte_range("bytes=10-", 100), Some((10, 99)));
        assert_eq!(byte_range("bytes=-4", 100), Some((96, 99)));
        assert_eq!(byte_range("bytes=9-10", 10), Some((9, 9)));
        assert!(byte_range("bytes=10-", 10).is_none());
        assert!(byte_range("bytes=-0", 10).is_none());
    }
}
