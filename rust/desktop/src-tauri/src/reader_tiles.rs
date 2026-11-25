use crate::{work, CommandResult};
use jni::{
    objects::{GlobalRef, JByteArray, JClass, JValue},
    JNIEnv, JavaVM,
};
use reqwest::Url;
use std::{
    collections::VecDeque,
    io::Read,
    path::{Path, PathBuf},
    sync::{Arc, Mutex, OnceLock},
    time::Duration,
};
use tauri::{http::Response, Manager, WebviewWindow};
use tokio::{io::AsyncWriteExt, sync::Semaphore};
use uuid::Uuid;

const MAX_BYTES: u64 = 24 << 20;
const TILE_ROWS: u32 = 576;
static JAVA: OnceLock<(JavaVM, GlobalRef)> = OnceLock::new();
static PREPARE: Semaphore = Semaphore::const_new(2);
static DECODE: Semaphore = Semaphore::const_new(1);

#[no_mangle]
pub extern "system" fn Java_org_universalmediadistribution_rust_ReaderTiles_register(
    env: JNIEnv,
    class: JClass,
) {
    if let (Ok(vm), Ok(class)) = (env.get_java_vm(), env.new_global_ref(class)) {
        let _ = JAVA.set((vm, class));
    }
}

#[derive(Clone, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Layout {
    id: Uuid,
    width: u32,
    height: u32,
    rows: u32,
}
struct Page {
    decoder: GlobalRef,
    _file: Option<tempfile::TempPath>,
    layout: Layout,
    sample: u32,
}
#[derive(Default)]
pub struct TileBook(Mutex<VecDeque<Arc<Page>>>);

impl Drop for Page {
    fn drop(&mut self) {
        if let Some((vm, _)) = JAVA.get() {
            if let Ok(mut env) = vm.attach_current_thread() {
                let _ = env.call_method(self.decoder.as_obj(), "recycle", "()V", &[]);
                let _ = env.exception_clear();
            }
        }
    }
}

fn open(path: &Path, file: Option<tempfile::TempPath>) -> CommandResult<Option<Page>> {
    let mut input = std::fs::File::open(path).map_err(|e| e.to_string())?;
    if input.metadata().map_err(|e| e.to_string())?.len() > MAX_BYTES {
        return Ok(None);
    }
    let mut header = [0; 24];
    if input.read_exact(&mut header).is_err()
        || header.starts_with(b"GIF")
        || (header.starts_with(b"RIFF") && &header[12..16] == b"VP8X" && header[20] & 2 != 0)
    {
        return Ok(None);
    }
    let (vm, class) = JAVA.get().ok_or("Decodificador Android no disponible.")?;
    let mut env = vm.attach_current_thread().map_err(jni_error)?;
    let path = env
        .new_string(path.to_str().ok_or("Ruta del lector inválida.")?)
        .map_err(jni_error)?;
    let opened = env.call_static_method(
        <&JClass>::from(class.as_obj()),
        "open",
        "(Ljava/lang/String;)Landroid/graphics/BitmapRegionDecoder;",
        &[JValue::Object(&path)],
    );
    let Ok(decoder) = opened.and_then(|result| result.l()) else {
        let _ = env.exception_clear();
        return Ok(None);
    };
    if decoder.is_null() {
        return Ok(None);
    }
    let width = env
        .call_method(&decoder, "getWidth", "()I", &[])
        .and_then(|v| v.i())
        .map_err(jni_error)? as u32;
    let height = env
        .call_method(&decoder, "getHeight", "()I", &[])
        .and_then(|v| v.i())
        .map_err(jni_error)? as u32;
    if !(1..=8192).contains(&width) || !(1..=65535).contains(&height) {
        let _ = env.call_method(&decoder, "recycle", "()V", &[]);
        return Ok(None);
    }
    let mut sample = 1;
    while width.div_ceil(sample) > 1440 {
        sample *= 2;
    }
    Ok(Some(Page {
        decoder: env.new_global_ref(decoder).map_err(jni_error)?,
        _file: file,
        layout: Layout {
            id: Uuid::new_v4(),
            width,
            height,
            rows: TILE_ROWS * sample,
        },
        sample,
    }))
}

fn jni_error(error: jni::errors::Error) -> String {
    format!("El lector de Android no pudo preparar la imagen. {error}")
}

impl Page {
    fn tile(&self, index: u32) -> CommandResult<Vec<u8>> {
        let (vm, class) = JAVA.get().ok_or("Decodificador Android no disponible.")?;
        let mut env = vm.attach_current_thread().map_err(jni_error)?;
        let top = index * self.layout.rows;
        let result = env
            .call_static_method(
                <&JClass>::from(class.as_obj()),
                "read",
                "(Landroid/graphics/BitmapRegionDecoder;III)[B",
                &[
                    JValue::Object(self.decoder.as_obj()),
                    JValue::Int(top as i32),
                    JValue::Int(self.layout.rows as i32),
                    JValue::Int(self.sample as i32),
                ],
            )
            .and_then(|v| v.l());
        let result = match result {
            Ok(result) if !result.is_null() => result,
            _ => {
                let _ = env.exception_clear();
                return Err("No se pudo leer este segmento.".into());
            }
        };
        env.convert_byte_array(JByteArray::from(result))
            .map_err(jni_error)
    }
}

fn decoded_path(url: &Url) -> CommandResult<String> {
    percent_encoding::percent_decode_str(url.path().trim_start_matches('/'))
        .decode_utf8()
        .map(|value| value.into_owned())
        .map_err(|_| "Ruta del lector inválida.".into())
}

async fn materialize(
    app: &tauri::AppHandle,
    url: Url,
) -> CommandResult<Option<(PathBuf, Option<tempfile::TempPath>)>> {
    let webview_domain = matches!(url.scheme(), "http" | "https");
    let host = url.host_str();
    if url.scheme() == "asset" || (webview_domain && host == Some("asset.localhost")) {
        let path = PathBuf::from(format!("/{}", decoded_path(&url)?.trim_start_matches('/')));
        if !app.asset_protocol_scope().is_allowed(&path) {
            return Err("La imagen no pertenece al lector.".into());
        }
        return Ok(Some((path, None)));
    }
    let cache = app.path().app_cache_dir().map_err(|e| e.to_string())?;
    tokio::fs::create_dir_all(&cache)
        .await
        .map_err(|e| e.to_string())?;
    let file =
        work(move || tempfile::NamedTempFile::new_in(cache).map_err(|e| e.to_string())).await?;
    let (output, path) = file.into_parts();
    let mut output = tokio::fs::File::from_std(output);
    if url.scheme() == "umdstream" || (webview_domain && host == Some("umdstream.localhost")) {
        let path_parts = decoded_path(&url)?;
        let (token, name) = path_parts.split_once('/').ok_or("Página desconocida.")?;
        let token = token.parse().map_err(|_| "Página desconocida.")?;
        if !name.starts_with("page-") {
            return Ok(None);
        }
        let (bytes, _) = crate::torrent_stream::archive_resource(app, token, name.into()).await?;
        if bytes.len() as u64 > MAX_BYTES {
            return Ok(None);
        }
        output.write_all(&bytes).await.map_err(|e| e.to_string())?;
    } else if webview_domain {
        let trusted = app
            .state::<crate::artwork::Origin>()
            .get()
            .is_some_and(|root| root.origin() == url.origin());
        let duration = Duration::from_secs(15);
        let mut response = if trusted {
            crate::direct_client()?
                .get(url)
                .timeout(duration)
                .send()
                .await
                .map_err(|e| e.to_string())?
        } else {
            crate::streams::get_public(url, duration).await?
        };
        if !response.status().is_success()
            || response.content_length().is_some_and(|len| len > MAX_BYTES)
        {
            return Ok(None);
        }
        let mut size = 0;
        while let Some(chunk) = response.chunk().await.map_err(|e| e.to_string())? {
            size += chunk.len() as u64;
            if size > MAX_BYTES {
                return Ok(None);
            }
            output.write_all(&chunk).await.map_err(|e| e.to_string())?;
        }
    } else {
        return Ok(None);
    }
    output.flush().await.map_err(|e| e.to_string())?;
    drop(output);
    Ok(Some((path.to_path_buf(), Some(path))))
}

#[tauri::command]
pub async fn prepare_reader_tiles(
    window: WebviewWindow,
    address: String,
) -> CommandResult<Option<Layout>> {
    if window.label() != "main" || JAVA.get().is_none() || address.len() > 8192 {
        return Ok(None);
    }
    let _slot = PREPARE.acquire().await.map_err(|_| "Lector ocupado.")?;
    let app = window.app_handle();
    let url = Url::parse(&address).map_err(|_| "Página inválida.")?;
    if !url.username().is_empty() || url.password().is_some() {
        return Ok(None);
    }
    let Some((path, file)) = materialize(app, url).await? else {
        return Ok(None);
    };
    let page = work(move || open(&path, file)).await?;
    let Some(page) = page else {
        return Ok(None);
    };
    let layout = page.layout.clone();
    let removed = {
        let state = app.state::<TileBook>();
        let mut pages = state.0.lock().map_err(|_| "Lector ocupado.")?;
        let removed = (pages.len() >= 6).then(|| pages.pop_front()).flatten();
        pages.push_back(Arc::new(page));
        removed
    };
    drop(removed);
    Ok(Some(layout))
}

#[tauri::command]
pub async fn release_reader_tiles(window: WebviewWindow, id: Uuid) -> CommandResult<()> {
    if window.label() != "main" {
        return Ok(());
    }
    let state = window.state::<TileBook>();
    let page = {
        let mut pages = state.0.lock().map_err(|_| "Lector ocupado.")?;
        pages
            .iter()
            .position(|page| page.layout.id == id)
            .and_then(|index| pages.remove(index))
    };
    work(move || {
        drop(page);
        Ok(())
    })
    .await
}

pub async fn serve(app: &tauri::AppHandle, path: &str, head: bool) -> Response<Vec<u8>> {
    let response = || {
        Response::builder()
            .status(404)
            .body(Vec::<u8>::new())
            .unwrap()
    };
    let Some((id, index)) = path
        .split_once('/')
        .and_then(|(id, n)| Some((Uuid::parse_str(id).ok()?, n.parse::<u32>().ok()?)))
    else {
        return response();
    };
    let _slot = match DECODE.acquire().await {
        Ok(slot) => slot,
        Err(_) => return response(),
    };
    let page = app.state::<TileBook>().0.lock().ok().and_then(|pages| {
        pages
            .iter()
            .find(|page| {
                page.layout.id == id && index < page.layout.height.div_ceil(page.layout.rows)
            })
            .cloned()
    });
    let Some(page) = page else {
        return response();
    };
    let bytes = if head {
        Vec::new()
    } else {
        match work(move || page.tile(index)).await {
            Ok(bytes) => bytes,
            Err(_) => return response(),
        }
    };
    Response::builder()
        .header("content-type", "image/png")
        .header("cache-control", "no-store")
        .body(bytes)
        .unwrap()
}
