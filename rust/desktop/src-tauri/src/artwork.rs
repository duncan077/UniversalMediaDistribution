use crate::{direct_client, streams, CommandResult};
use base64::{engine::general_purpose::STANDARD, Engine};
use reqwest::Url;
use std::collections::{HashMap, VecDeque};
use std::sync::{LazyLock, Mutex};
use std::time::Duration;
use tauri::State;
use umd_core::CatalogStore;

const MAX_IMAGE_BYTES: usize = 6 * 1024 * 1024;
const MAX_CACHE_ENTRIES: usize = 128;
const MAX_CACHE_BYTES: usize = 32 * 1024 * 1024;
const TIMEOUT: Duration = Duration::from_secs(10);

#[derive(Default)]
struct ArtworkCache {
    map: HashMap<String, String>,
    order: VecDeque<String>,
    total_bytes: usize,
}

impl ArtworkCache {
    fn get(&self, key: &str) -> Option<String> {
        self.map.get(key).cloned()
    }

    fn insert(&mut self, key: String, value: String) {
        let size = value.len();
        if size > MAX_CACHE_BYTES / 4 {
            return;
        }
        if let Some(prev) = self.map.remove(&key) {
            self.total_bytes = self.total_bytes.saturating_sub(prev.len());
            self.order.retain(|k| k != &key);
        }
        while self.map.len() >= MAX_CACHE_ENTRIES || self.total_bytes + size > MAX_CACHE_BYTES {
            let Some(oldest) = self.order.pop_front() else {
                break;
            };
            if let Some(removed) = self.map.remove(&oldest) {
                self.total_bytes = self.total_bytes.saturating_sub(removed.len());
            }
        }
        self.total_bytes += size;
        self.order.push_back(key.clone());
        self.map.insert(key, value);
    }
}

static CACHE: LazyLock<Mutex<ArtworkCache>> = LazyLock::new(Default::default);

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

fn host_referer(url: &Url) -> Option<Url> {
    let host = url.host_str()?.to_ascii_lowercase();
    let candidate = if host.contains("olympus") {
        "https://olympusbiblioteca.com/"
    } else if host.contains("manhwaweb") || host.contains("mweb") {
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

#[tauri::command]
pub async fn fetch_artwork(
    url: String,
    providers: Option<Vec<String>>,
    store: State<'_, CatalogStore>,
) -> CommandResult<String> {
    let raw = url.trim();
    if raw.is_empty() || raw.len() > 2048 {
        return Err("Dirección de portada inválida.".into());
    }
    let server_base = store
        .setting("server_url")
        .ok()
        .flatten()
        .and_then(|s| Url::parse(&s).ok());
    let parsed = match Url::parse(raw) {
        Ok(u) => u,
        Err(_) => {
            let base = server_base
                .as_ref()
                .ok_or("Dirección de portada inválida.")?;
            base.join(raw).map_err(|_| "Dirección de portada inválida.")?
        }
    };
    if !matches!(parsed.scheme(), "http" | "https")
        || !parsed.username().is_empty()
        || parsed.password().is_some()
    {
        return Err("Dirección de portada inválida.".into());
    }

    let cache_key = parsed.as_str().to_owned();
    if let Ok(cache) = CACHE.lock() {
        if let Some(hit) = cache.get(&cache_key) {
            return Ok(hit);
        }
    }

    let same_as_server = server_base
        .as_ref()
        .is_some_and(|base| base.origin() == parsed.origin());

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
        for site in providers.unwrap_or_default() {
            if let Some(r) = crate::providers::provider_referer(&site) {
                if !referers.iter().any(|existing| existing.as_ref() == Some(&r)) {
                    referers.push(Some(r));
                }
            }
        }
        if let Some(r) = host_referer(&parsed) {
            if !referers.iter().any(|existing| existing.as_ref() == Some(&r)) {
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
            match streams::fetch_media(parsed.clone(), TIMEOUT, None, None, referer.as_ref()).await
            {
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
        fetched.ok_or(last_err)?
    };

    let mime = sniff_raster_mime(&bytes).ok_or("La portada no es una imagen compatible.")?;
    let data_uri = format!("data:{mime};base64,{}", STANDARD.encode(&bytes));
    if let Ok(mut cache) = CACHE.lock() {
        cache.insert(cache_key, data_uri.clone());
    }
    Ok(data_uri)
}
