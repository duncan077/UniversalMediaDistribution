use crate::{Error, Result};
use serde::Deserialize;
use serde_json::value::RawValue;
use sha2::{Digest, Sha256};
use std::borrow::Cow;
use url::Url;
use uuid::Uuid;

pub const CATALOG_THUMBNAIL_PREFIX: &str = "/api/v1/catalog/thumbnails/";

#[derive(Clone)]
pub struct CatalogThumbnailUrls(Url);

impl CatalogThumbnailUrls {
    pub fn new(origin: &str) -> Result<Self> {
        let url = Url::parse(origin.trim()).ok().filter(|url| {
            Self::source(url.as_str()).is_some()
                && url.path() == "/"
                && url.query().is_none()
                && url.fragment().is_none()
        });
        url.map(Self).ok_or_else(|| {
            Error::Invalid(
                "UMD_SERVER_PUBLIC_URL debe ser un origen HTTPS público sin ruta.".into(),
            )
        })
    }

    pub fn source(raw: &str) -> Option<Url> {
        let raw = raw.trim();
        if raw.len() > 2048 {
            return None;
        }
        let mut url = Url::parse(raw).ok()?;
        if url.scheme() != "https"
            || url.port().is_some()
            || !crate::public_url::web_url(&url)
            || url.path().starts_with(CATALOG_THUMBNAIL_PREFIX)
        {
            return None;
        }
        let path = url.path().to_ascii_lowercase();
        if path.ends_with(".svg") || path.ends_with(".avif") {
            return None;
        }
        url.set_fragment(None);
        Some(url)
    }

    pub fn key(source: &Url) -> String {
        format!("{:x}", Sha256::digest(source.as_str()))
    }

    pub(crate) fn rewrite<'a>(&self, payload: &'a [u8]) -> Result<Cow<'a, [u8]>> {
        #[derive(Deserialize)]
        #[serde(rename_all = "camelCase")]
        struct Fields<'a> {
            sync_id: Uuid,
            #[serde(borrow)]
            thumbnail_ref: Option<&'a RawValue>,
        }
        let fields: Fields<'_> = serde_json::from_slice(payload)?;
        let Some(raw) = fields.thumbnail_ref else {
            return Ok(Cow::Borrowed(payload));
        };
        let Some(source) = serde_json::from_str::<Cow<'_, str>>(raw.get())
            .ok()
            .and_then(|s| Self::source(&s))
        else {
            return Ok(Cow::Borrowed(payload));
        };
        let key = Self::key(&source);
        let url = format!(
            "{}{CATALOG_THUMBNAIL_PREFIX}{}/{key}.jpg",
            self.0.origin().ascii_serialization(),
            fields.sync_id
        );
        // RawValue points to the top-level field in the original row, so the
        // rest of the validated JSON can be copied without reserializing it.
        let offset = raw.get().as_ptr() as usize - payload.as_ptr() as usize;
        let mut output = Vec::with_capacity(payload.len() - raw.get().len() + url.len() + 2);
        output.extend_from_slice(&payload[..offset]);
        serde_json::to_writer(&mut output, &url)?;
        output.extend_from_slice(&payload[offset + raw.get().len()..]);
        Ok(Cow::Owned(output))
    }
}
