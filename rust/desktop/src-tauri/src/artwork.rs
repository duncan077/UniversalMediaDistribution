use crate::{direct_client, streams, CommandResult};
use image::{ImageFormat, ImageReader, Limits};
use reqwest::Url;
use sha2::{Digest, Sha256};
use std::collections::{HashMap, VecDeque};
use std::time::Duration;
use std::{
    io::Cursor,
    path::{Path, PathBuf},
    sync::{Arc, LazyLock, Mutex},
};
use tauri::{
    http::{Request, Response},
    Manager,
};

const MAX_IMAGE_BYTES: usize = 6 * 1024 * 1024;
const MAX_CACHE_ENTRIES: usize = 128;
const MAX_CACHE_BYTES: usize = 32 * 1024 * 1024;
const TIMEOUT: Duration = Duration::from_secs(10);
static LOADS: tokio::sync::Semaphore = tokio::sync::Semaphore::const_new(2);

#[derive(Clone)]
struct Artwork {
    mime: &'static str,
    bytes: Arc<[u8]>,
}

impl Artwork {
    fn response(&self, head: bool) -> Response<Vec<u8>> {
        Response::builder()
            .header("content-type", self.mime)
            .header("content-length", self.bytes.len())
            .header("cache-control", "private, max-age=3600")
            .body(if head {
                Vec::new()
            } else {
                self.bytes.to_vec()
            })
            .unwrap()
    }
}

#[derive(Default)]
struct ArtworkCache {
    map: HashMap<String, Artwork>,
    order: VecDeque<String>,
    total_bytes: usize,
}

impl ArtworkCache {
    fn get(&self, key: &str) -> Option<Artwork> {
        self.map.get(key).cloned()
    }

    fn insert(&mut self, key: String, value: Artwork, budget: usize) {
        let size = value.bytes.len();
        if size > budget / 4 {
            return;
        }
        if let Some(prev) = self.map.remove(&key) {
            self.total_bytes = self.total_bytes.saturating_sub(prev.bytes.len());
            self.order.retain(|k| k != &key);
        }
        while self.map.len() >= MAX_CACHE_ENTRIES || self.total_bytes + size > budget {
            let Some(oldest) = self.order.pop_front() else {
                break;
            };
            if let Some(removed) = self.map.remove(&oldest) {
                self.total_bytes = self.total_bytes.saturating_sub(removed.bytes.len());
            }
        }
        self.total_bytes += size;
        self.order.push_back(key.clone());
        self.map.insert(key, value);
    }
}

fn thumbnail(bytes: Vec<u8>, width: u32) -> CommandResult<Artwork> {
    let mime = sniff_raster_mime(&bytes).ok_or("La portada no es una imagen compatible.")?;
    if width == 0 || mime == "image/avif" {
        return Ok(Artwork {
            mime,
            bytes: bytes.into(),
        });
    }
    let reader = || ImageReader::new(Cursor::new(&bytes)).with_guessed_format();
    let invalid = |_: image::ImageError| "No se pudo preparar la miniatura.".to_owned();
    let (w, h) = reader()
        .map_err(|e| e.to_string())?
        .into_dimensions()
        .map_err(invalid)?;
    if w == 0 || h == 0 || u64::from(w) * u64::from(h) > 8_000_000 {
        return Err("La portada supera el tamaño permitido.".into());
    }
    let height = match width {
        0..=64 => 96,
        65..=400 => width * 3 / 2,
        _ => 640,
    };
    if w <= width && h <= height {
        return Ok(Artwork {
            mime,
            bytes: bytes.into(),
        });
    }
    let mut decoder = reader().map_err(|e| e.to_string())?;
    let mut limits = Limits::default();
    limits.max_image_width = Some(w);
    limits.max_image_height = Some(h);
    limits.max_alloc = Some(32 << 20);
    decoder.limits(limits);
    let image = decoder.decode().map_err(invalid)?.thumbnail(width, height);
    let (format, mime) = if image.color().has_alpha() {
        (ImageFormat::WebP, "image/webp")
    } else {
        (ImageFormat::Jpeg, "image/jpeg")
    };
    let mut output = Cursor::new(Vec::new());
    image.write_to(&mut output, format).map_err(invalid)?;
    Ok(Artwork {
        mime,
        bytes: output.into_inner().into(),
    })
}

static CACHE: LazyLock<Mutex<ArtworkCache>> = LazyLock::new(Default::default);

#[cfg(target_os = "android")]
pub(crate) fn trim() {
    if let Ok(mut cache) = CACHE.lock() {
        *cache = ArtworkCache::default();
    }
}

fn sniff_raster_mime(bytes: &[u8]) -> Option<&'static str> {
    if bytes.len() >= 3 && bytes[0..3] == [0xFF, 0xD8, 0xFF] {
        return Some("image/jpeg");
    }
    if bytes.len() >= 8 && bytes[0..8] == [0x89, b'P', b'N', b'G', 0x0D, 0x0A, 0x1A, 0x0A] {
        return Some("image/png");
    }
    if bytes.len() >= 12 && &bytes[0..4] == b"RIFF" && &bytes[8..12] == b"WEBP" {
        return Some("image/webp");
    }
    if bytes.len() >= 6 && (&bytes[0..6] == b"GIF87a" || &bytes[0..6] == b"GIF89a") {
        return Some("image/gif");
    }
    if bytes.len() >= 12 && &bytes[4..8] == b"ftyp" {
        let brand = &bytes[8..12];
        if brand == b"avif" || brand == b"avis" || brand == b"mif1" {
            return Some("image/avif");
        }
    }
    None
}

pub(crate) fn host_referer(url: &Url) -> Option<Url> {
    let host = url.host_str()?.to_ascii_lowercase();
    let candidate = if host.contains("olympus") {
        "https://olympusbiblioteca.com/"
    } else if host == "img2mw.xyz" || host.contains("manhwaweb") || host.contains("mweb") {
        "https://manhwaweb.com/"
    } else if host.contains("zonatmo") || host.contains("lectortmo") || host.contains("tmofans") {
        "https://zonatmo.com/"
    } else if host.contains("leercapitulo") {
        "https://www.leercapitulo.co/"
    } else if host.contains("capibaratraductor") {
        "https://capibaratraductor.com/"
    } else if host.contains("jkanime") {
        "https://jkanime.net/"
    } else if host.contains("animeav1") {
        "https://animeav1.com/"
    } else if host.contains("uchuujin") {
        "https://uchuujinmangas.com/"
    } else if host.contains("doramasflix") {
        "https://doramasflix.in/"
    } else if host.contains("animetmo") {
        "https://animetmo.com/"
    } else {
        return None;
    };
    Url::parse(candidate).ok()
}

async fn read_bounded(mut response: reqwest::Response) -> CommandResult<Vec<u8>> {
    if !response.status().is_success() {
        return Err(format!("HTTP {}", response.status().as_u16()));
    }
    if response
        .content_length()
        .is_some_and(|len| len > MAX_IMAGE_BYTES as u64)
    {
        return Err("La portada supera el tamaño permitido.".into());
    }
    let mut bytes = Vec::new();
    while let Some(chunk) = response.chunk().await.map_err(|e| e.to_string())? {
        if bytes.len() + chunk.len() > MAX_IMAGE_BYTES {
            return Err("La portada supera el tamaño permitido.".into());
        }
        bytes.extend_from_slice(&chunk);
    }
    Ok(bytes)
}

#[derive(Default)]
pub struct Origin(std::sync::RwLock<Option<Url>>, std::sync::OnceLock<PathBuf>);

impl Origin {
    pub fn set(&self, url: Option<Url>) {
        if let Ok(mut origin) = self.0.write() {
            *origin = url;
        }
    }
    pub(crate) fn get(&self) -> Option<Url> {
        self.0.read().ok()?.clone()
    }

    pub fn set_cache(&self, root: PathBuf) {
        let _ = self.1.set(root.join("artwork"));
    }
}

fn disk_read(path: &Path) -> Option<Artwork> {
    let file = std::fs::File::open(path).ok()?;
    if file.metadata().ok()?.len() > MAX_IMAGE_BYTES as u64 {
        return None;
    }
    let bytes = std::fs::read(path).ok()?;
    let mime = sniff_raster_mime(&bytes)?;
    let _ = file.set_modified(std::time::SystemTime::now());
    Some(Artwork {
        mime,
        bytes: bytes.into(),
    })
}

fn disk_save(path: &Path, artwork: &Artwork) {
    use std::io::Write;
    const MAX_DISK: u64 = 24 << 20;
    let Some(folder) = path.parent() else {
        return;
    };
    if artwork.bytes.len() > MAX_IMAGE_BYTES || std::fs::create_dir_all(folder).is_err() {
        return;
    }
    let Ok(mut temp) = tempfile::NamedTempFile::new_in(folder) else {
        return;
    };
    if temp.write_all(&artwork.bytes).is_err() || temp.persist(path).is_err() {
        return;
    }
    let Ok(files) = std::fs::read_dir(folder) else {
        return;
    };
    let mut entries: Vec<_> = files
        .filter_map(|entry| {
            let entry = entry.ok()?;
            let path = entry.path();
            if path.extension()?.to_str()? != "raster" {
                return None;
            }
            let metadata = entry.metadata().ok()?;
            metadata
                .is_file()
                .then(|| (metadata.modified().ok(), metadata.len(), path))
        })
        .collect();
    entries.sort_by_key(|(recent, _, _)| *recent);
    let mut size: u64 = entries.iter().map(|(_, len, _)| len).sum();
    for (index, (_, len, old)) in entries.iter().enumerate() {
        if size <= MAX_DISK && entries.len() - index <= MAX_CACHE_ENTRIES {
            break;
        }
        if std::fs::remove_file(old).is_ok() {
            size = size.saturating_sub(*len);
        }
    }
}

pub async fn serve(app: &tauri::AppHandle, req: &Request<Vec<u8>>) -> Response<Vec<u8>> {
    let mut width = 0;
    let mut address = None;
    let mut sites = Vec::new();
    if let Some(query) = req.uri().query().filter(|query| query.len() <= 8192) {
        for (key, value) in url::form_urlencoded::parse(query.as_bytes()) {
            match key.as_ref() {
                "w" => width = value.parse().unwrap_or(0),
                "u" => address = Some(value),
                "p" if sites.len() < 16 => sites.push(value),
                _ => {}
            }
        }
    }
    if let Some(url) = address {
        let config = app.state::<Origin>();
        let disk = (width > 0).then(|| config.1.get().cloned()).flatten();
        if let Ok(artwork) = fetch(
            &url,
            &sites.iter().map(|site| site.as_ref()).collect::<Vec<_>>(),
            width,
            config.get(),
            disk,
        )
        .await
        {
            return artwork.response(req.method() == tauri::http::Method::HEAD);
        }
    }
    Response::builder().status(404).body(Vec::new()).unwrap()
}

async fn fetch(
    url: &str,
    providers: &[&str],
    width: u32,
    server_base: Option<Url>,
    disk: Option<PathBuf>,
) -> CommandResult<Artwork> {
    let raw = url.trim();
    if raw.is_empty() || raw.len() > 2048 {
        return Err("Dirección de portada inválida.".into());
    }
    let parsed = match Url::parse(raw) {
        Ok(u) => u,
        Err(_) => {
            let base = server_base
                .as_ref()
                .ok_or("Dirección de portada inválida.")?;
            base.join(raw)
                .map_err(|_| "Dirección de portada inválida.")?
        }
    };
    if !matches!(parsed.scheme(), "http" | "https")
        || !parsed.username().is_empty()
        || parsed.password().is_some()
    {
        return Err("Dirección de portada inválida.".into());
    }

    let width = if width == 0 { 0 } else { width.clamp(64, 960) };
    let budget = if width > 0 { 8 << 20 } else { MAX_CACHE_BYTES };
    let same_as_server = server_base
        .as_ref()
        .is_some_and(|base| base.origin() == parsed.origin());
    let cache_key = format!("{width}\n{same_as_server}\n{parsed}");
    if let Some(hit) = CACHE.lock().ok().and_then(|cache| cache.get(&cache_key)) {
        return Ok(hit);
    }
    let permit = LOADS
        .acquire()
        .await
        .map_err(|_| "No se pudo cargar la portada.")?;
    if let Some(hit) = CACHE.lock().ok().and_then(|cache| cache.get(&cache_key)) {
        return Ok(hit);
    }
    let disk = disk
        .map(|folder| folder.join(format!("{:x}.raster", Sha256::digest(cache_key.as_bytes()))));
    if let Some(path) = disk.clone() {
        if let Some(hit) = crate::work(move || Ok(disk_read(&path))).await? {
            return Ok(hit);
        }
    }

    let mut from_browser = false;
    let bytes = if same_as_server {
        let response = direct_client()?
            .get(parsed.clone())
            .timeout(TIMEOUT)
            .send()
            .await
            .map_err(|e| e.to_string())?;
        read_bounded(response).await?
    } else {
        let mut referers: Vec<Option<Url>> = Vec::new();
        for site in providers {
            if let Some(r) = crate::providers::provider_referer(site) {
                if !referers
                    .iter()
                    .any(|existing| existing.as_ref() == Some(&r))
                {
                    referers.push(Some(r));
                }
            }
        }
        if let Some(r) = host_referer(&parsed) {
            if !referers
                .iter()
                .any(|existing| existing.as_ref() == Some(&r))
            {
                referers.push(Some(r));
            }
        }
        if let Ok(origin_url) = Url::parse(&format!("{}/", parsed.origin().ascii_serialization())) {
            if !referers
                .iter()
                .any(|existing| existing.as_ref() == Some(&origin_url))
            {
                referers.push(Some(origin_url));
            }
        }
        referers.push(None);

        let mut last_err = "No se pudo cargar la portada.".to_owned();
        let mut fetched = None;
        for referer in referers {
            match streams::get_public_image(parsed.clone(), TIMEOUT, referer.as_ref()).await {
                Ok(response) => match read_bounded(response).await {
                    Ok(data) if sniff_raster_mime(&data).is_some() => {
                        fetched = Some(data);
                        break;
                    }
                    Ok(_) => last_err = "La portada no es una imagen compatible.".into(),
                    Err(err) => last_err = err,
                },
                Err(err) => last_err = err,
            }
        }
        if fetched.is_none() {
            for site in providers {
                let Some((app, id, origin)) = crate::providers::verified(site) else {
                    continue;
                };
                if origin.origin() != parsed.origin() {
                    continue;
                }
                if let Ok(data) = crate::ad_filter::verified_image(&app, id, &origin, &parsed).await
                {
                    if sniff_raster_mime(&data).is_some() {
                        fetched = Some(data);
                        from_browser = true;
                        break;
                    }
                }
            }
        }
        fetched.ok_or(last_err)?
    };

    if from_browser {
        return crate::work(move || thumbnail(bytes, width)).await;
    }
    let disk_enabled = disk.is_some();
    let artwork = crate::work(move || {
        let result = thumbnail(bytes, width)?;
        if let Some(path) = disk {
            disk_save(&path, &result);
        }
        Ok(result)
    })
    .await?;
    if !disk_enabled {
        if let Ok(mut cache) = CACHE.lock() {
            cache.insert(cache_key, artwork.clone(), budget);
        }
    }
    drop(permit);
    Ok(artwork)
}
