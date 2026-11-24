use super::{bounded_text, fetch_media, get_public, media, Stream};
use crate::CommandResult;
use reqwest::{header, StatusCode, Url};
use serde::Deserialize;
use std::time::Duration;

const TIMEOUT: Duration = Duration::from_secs(10);

pub(super) fn address(page: &Url) -> Option<Url> {
    if page.scheme() != "https"
        || !matches!(
            page.host_str(),
            Some("pixeldrain.com" | "www.pixeldrain.com")
        )
        || !super::web_url(page)
    {
        return None;
    }
    let path = page.path().trim_end_matches('/');
    let id = path
        .strip_prefix("/u/")
        .or_else(|| path.strip_prefix("/api/file/"))?;
    if id.is_empty() || id.len() > 64 || !id.bytes().all(|b| b.is_ascii_alphanumeric()) {
        return None;
    }
    Url::parse(&format!("https://pixeldrain.com/api/file/{id}")).ok()
}

#[derive(Deserialize)]
struct FileInfo {
    size: u64,
    mime_type: String,
    availability: String,
    can_download: bool,
    allow_video_player: bool,
}

impl FileInfo {
    fn video_type(&self) -> CommandResult<&str> {
        if !self.availability.is_empty() || !self.can_download {
            return Err(
                "Pixeldrain restringe este archivo. Abrilo en el reproductor del proveedor.".into(),
            );
        }
        if self.size == 0 || !self.allow_video_player || !self.mime_type.starts_with("video/") {
            return Err("Pixeldrain no ofrece reproducción de video para este archivo.".into());
        }
        Ok(&self.mime_type)
    }
}

pub(super) async fn resolve(address: Url, referer: Option<&Url>) -> CommandResult<Stream> {
    let info = get_public(
        address
            .join(&format!("{}/info", address.path()))
            .map_err(|_| "Enlace de Pixeldrain inválido.")?,
        TIMEOUT,
    )
    .await?;
    let info: FileInfo = serde_json::from_str(&bounded_text(info, 64 << 10).await?)
        .map_err(|_| "Pixeldrain no devolvió información válida del archivo.")?;
    let video_type = info.video_type()?;
    // The actual range request determines whether playback is permitted from
    // this context. Keep any upstream rejection, including hotlink restrictions.
    let mut response = fetch_media(
        address.clone(),
        TIMEOUT,
        referer,
        Some("bytes=0-31"),
        referer,
    )
    .await?;
    let mime = response
        .headers()
        .get(header::CONTENT_TYPE)
        .and_then(|value| value.to_str().ok())
        .and_then(|value| value.split(';').next());
    if response.status() != StatusCode::PARTIAL_CONTENT
        || !response
            .headers()
            .get(header::CONTENT_RANGE)
            .is_some_and(|value| value.as_bytes().starts_with(b"bytes 0-"))
        || !mime
            .is_some_and(|mime| mime.starts_with("video/") || mime == "application/octet-stream")
        || response
            .chunk()
            .await
            .map_err(|_| "Pixeldrain interrumpió la descarga.")?
            .is_none_or(|bytes| bytes.is_empty())
    {
        return Err("Pixeldrain no entregó un rango de video reproducible.".into());
    }
    let mut stream = media(address, video_type).ok_or("Enlace de Pixeldrain inválido.")?;
    stream.referer = referer.map(Url::to_string);
    Ok(stream)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn accepts_single_public_file_routes_only() {
        for path in ["/u/abc123", "/api/file/abc123"] {
            let page = Url::parse(&format!("https://www.pixeldrain.com{path}?download")).unwrap();
            assert_eq!(
                address(&page).unwrap().as_str(),
                "https://pixeldrain.com/api/file/abc123"
            );
        }
        for address_to_reject in [
            "https://pixeldrain.com/l/list",
            "https://pixeldrain.com/api/file/a,b",
            "https://pixeldrain.com/u/a%2fb",
            "https://notpixeldrain.com/u/abc",
            "http://pixeldrain.com/u/abc",
        ] {
            assert!(address(&Url::parse(address_to_reject).unwrap()).is_none());
        }
    }

    #[test]
    fn honors_availability_download_and_video_flags() {
        let mut info = FileInfo {
            size: 1,
            mime_type: "video/mp4".into(),
            availability: String::new(),
            can_download: true,
            allow_video_player: true,
        };
        assert_eq!(info.video_type().unwrap(), "video/mp4");
        info.availability = "hotlink_detected".into();
        assert!(info.video_type().is_err());
        info.availability.clear();
        info.can_download = false;
        assert!(info.video_type().is_err());
        info.can_download = true;
        info.allow_video_player = false;
        assert!(info.video_type().is_err());
        info.allow_video_player = true;
        info.mime_type = "application/zip".into();
        assert!(info.video_type().is_err());
    }
}
