use crate::{Error, Result};
use chrono::Utc;
use serde::{Deserialize, Serialize};
use serde_json::Value;
use sha2::{Digest, Sha256};
use std::collections::{BTreeMap, HashSet};
use unicode_normalization::{char::is_combining_mark, UnicodeNormalization};
use uuid::Uuid;

pub const CATALOG_FORMAT: &str = "universal-media-distribution.catalog";
pub const MAX_DOCUMENT_BYTES: usize = 32 * 1024 * 1024;

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CatalogDocument {
    pub format: String,
    pub version: u32,
    #[serde(default = "now")]
    pub exported_at: String,
    #[serde(default)]
    pub source_label: Option<String>,
    pub items: Vec<CatalogItem>,
    #[serde(flatten)]
    pub extra: BTreeMap<String, Value>,
}

impl CatalogDocument {
    pub fn new(items: Vec<CatalogItem>) -> Self {
        Self {
            format: CATALOG_FORMAT.into(),
            version: 1,
            exported_at: now(),
            source_label: Some("Universal Media Distribution".into()),
            items,
            extra: BTreeMap::new(),
        }
    }

    pub fn parse(bytes: &[u8]) -> Result<Self> {
        Self::parse_with_limit(bytes, MAX_DOCUMENT_BYTES)
    }

    pub fn parse_with_limit(bytes: &[u8], max: usize) -> Result<Self> {
        if bytes.len() > max {
            return Err(Error::DocumentTooLarge(max));
        }
        let document: Self = serde_json::from_slice(bytes)?;
        document.validate()?;
        Ok(document)
    }

    pub fn validate(&self) -> Result<()> {
        if self.format != CATALOG_FORMAT || self.version != 1 {
            return Err(Error::Invalid(
                "Unsupported UMD catalog format or version".into(),
            ));
        }
        let mut ids = HashSet::new();
        let mut keys = BTreeMap::new();
        for item in &self.items {
            item.validate()?;
            let explicit = item.sync_id.is_some();
            let ambiguous = keys
                .insert(item.key(), explicit)
                .is_some_and(|prior| !prior || !explicit);
            if !ids.insert(item.id()?) || ambiguous {
                return Err(Error::Invalid(format!(
                    "Duplicate catalog entry: {}",
                    item.name
                )));
            }
        }
        Ok(())
    }
}

#[derive(Clone, PartialEq, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CatalogItem {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub sync_id: Option<Uuid>,
    pub media_type: String,
    pub subtype: String,
    pub name: String,
    #[serde(default)]
    pub deleted: bool,
    // Preserve chapters, seasons, sources, rights, aliases and future metadata
    // during JSON exchange with the .NET app.
    #[serde(flatten)]
    pub metadata: BTreeMap<String, Value>,
}

pub(crate) fn is_adult_tag(value: &str) -> bool {
    let mut ascii = [0; 24];
    let trimmed = value.trim();
    let normalized;
    let label = if trimmed.is_ascii() {
        if trimmed.len() > ascii.len() {
            return false;
        }
        let lower = &mut ascii[..trimmed.len()];
        lower.copy_from_slice(trimmed.as_bytes());
        lower.make_ascii_lowercase();
        std::str::from_utf8(lower).unwrap_or_default()
    } else {
        normalized = normalize(trimmed);
        &normalized
    };
    matches!(
        label,
        "adult"
            | "adulto"
            | "adultos"
            | "porn"
            | "porno"
            | "pornography"
            | "pornografia"
            | "+18"
            | "18+"
            | "hentai"
            | "erotica"
            | "erotico"
            | "nsfw"
            | "xxx"
            | "smut"
    )
}

pub(crate) fn is_adult_rating(value: &str) -> bool {
    matches!(
        normalize(value).as_str(),
        "rx" | "nc-17"
            | "nc17"
            | "x"
            | "xxx"
            | "+18"
            | "18+"
            | "adult"
            | "adulto"
            | "adultos"
            | "nsfw"
            | "ao"
            | "adults only"
            | "solo adultos"
            | "mayores de 18"
    )
}

fn collect_strings(value: &Value, out: &mut String) {
    match value {
        Value::String(s) => {
            if !s.trim().is_empty() {
                out.push(' ');
                out.push_str(s);
            }
        }
        Value::Array(items) => {
            for item in items {
                collect_strings(item, out);
            }
        }
        Value::Object(map) => {
            for v in map.values() {
                collect_strings(v, out);
            }
        }
        _ => {}
    }
}

fn valid_genre_key(raw: &str) -> bool {
    let s = raw.to_ascii_lowercase();
    (2..=40).contains(&s.len())
        && !s.starts_with('-')
        && !s.ends_with('-')
        && !s.contains("--")
        && s.bytes()
            .all(|b| b.is_ascii_lowercase() || b.is_ascii_digit() || b == b'-')
}

impl CatalogItem {
    pub fn adult(&self) -> bool {
        ["categories", "genres"]
            .iter()
            .filter_map(|key| self.metadata.get(*key)?.as_array())
            .flatten()
            .filter_map(Value::as_str)
            .any(is_adult_tag)
            || self
                .metadata
                .get("contentRating")
                .and_then(Value::as_str)
                .is_some_and(is_adult_rating)
    }
    pub fn validate(&self) -> Result<()> {
        if self.name.trim().is_empty() || self.name.len() > 2048 {
            return Err(Error::Invalid(
                "A title must have a name of at most 2048 bytes".into(),
            ));
        }
        let media = self.media_type.to_lowercase();
        if !["movie", "book", "audio"].contains(&media.as_str()) {
            return Err(Error::Invalid(format!(
                "Unknown media type: {}",
                self.media_type
            )));
        }
        let subtype = self.subtype.replace(['-', ' '], "_").to_lowercase();
        let suffix = subtype
            .strip_prefix(&format!("{media}."))
            .unwrap_or(&subtype);
        let suffix = if media == "audio" && suffix == "music_album" {
            "album"
        } else {
            suffix
        };
        let valid = match media.as_str() {
            "movie" => [
                "movie",
                "series",
                "anime_movie",
                "anime_series",
                "documentary",
                "short",
            ]
            .contains(&suffix),
            "book" => [
                "book",
                "novel",
                "manga",
                "comic",
                "light_novel",
                "web_novel",
            ]
            .contains(&suffix),
            "audio" => ["album", "single", "audiobook", "podcast", "radio_drama"].contains(&suffix),
            _ => false,
        };
        if !valid || self.sync_id == Some(Uuid::nil()) || (self.deleted && self.sync_id.is_none()) {
            return Err(Error::Invalid(format!(
                "Invalid subtype or identity for {}",
                self.name
            )));
        }
        for field in ["description", "thumbnailRef", "releaseDate", "originalName"] {
            if let Some(value) = self.metadata.get(field) {
                if !value.is_null() && !value.is_string() {
                    return Err(Error::Invalid(format!("{field} must be text")));
                }
            }
        }
        for field in ["genres", "categories"] {
            if let Some(value) = self.metadata.get(field) {
                if !value.is_null()
                    && !value
                        .as_array()
                        .is_some_and(|values| values.iter().all(Value::is_string))
                {
                    return Err(Error::Invalid(format!("{field} must contain text values")));
                }
            }
        }
        Ok(())
    }

    pub fn key(&self) -> String {
        let media = self.media_type.to_lowercase();
        let subtype = self.subtype.replace(['-', ' '], "_").to_lowercase();
        let subtype = subtype
            .strip_prefix(&format!("{media}."))
            .unwrap_or(&subtype);
        let subtype = if media == "audio" && subtype == "music_album" {
            "album"
        } else {
            subtype
        };
        format!("{media}:{subtype}:{}", identity_name(&self.name))
    }

    pub fn matches_subtype_group(&self, group: &str) -> bool {
        crate::filters::matches_subtype_group(&self.media_type, &self.subtype, group)
    }

    pub fn id(&self) -> Result<Uuid> {
        if let Some(id) = self.sync_id {
            if (self.media_type.is_ascii() && !self.media_type.eq_ignore_ascii_case("book"))
                || self.subtype.eq_ignore_ascii_case("manga")
                || self.subtype.eq_ignore_ascii_case("book.manga")
            {
                return Ok(id);
            }
        }
        let media = self.media_type.to_lowercase();
        let base = dotnet_id(&format!("{media}:{}", identity_name(&self.name)));
        let id = self.sync_id.unwrap_or(base);
        if media == "book" && self.sync_id.is_none_or(|value| value == base) {
            let subtype = self.subtype.replace(['-', ' '], "_").to_lowercase();
            let subtype = subtype.strip_prefix("book.").unwrap_or(&subtype);
            if subtype != "manga" {
                return Ok(dotnet_id(&format!("{}:book.{subtype}", base.simple())));
            }
        }
        Ok(id)
    }

    pub fn search_text(&self) -> String {
        // Search all descriptive fields while keeping media payloads opaque.
        let fields = [
            "originalName",
            "description",
            "alternativeNames",
            "aliases",
            "genres",
            "categories",
            "contributors",
        ];
        let mut text = self.name.clone();
        for field in fields {
            if let Some(value) = self.metadata.get(field) {
                collect_strings(value, &mut text);
            }
        }
        normalize(&text)
    }
}

pub fn normalize(text: &str) -> String {
    if text.is_ascii() {
        return collapse_ascii(text, u8::to_ascii_lowercase);
    }
    let decomposed: String = text.nfkd().filter(|c| !is_combining_mark(*c)).collect();
    collapse_whitespace(&decomposed.to_lowercase())
}

/// Maps an explicit type label published by a reading provider to a catalog subtype.
pub fn provider_book_subtype(label: &str) -> Option<&'static str> {
    let label = normalize(&label.replace(['-', '_'], " "));
    let label = label.strip_prefix("book.").unwrap_or(&label);
    Some(match label {
        "manga" | "manhwa" | "manhua" | "doujinshi" => "manga",
        "comic" | "historieta" => "comic",
        "novel" | "novela" => "novel",
        "light novel" | "lightnovel" | "novela ligera" => "light_novel",
        "web novel" | "webnovel" | "novela web" => "web_novel",
        "book" | "libro" | "short story" | "relato" => "book",
        _ => return None,
    })
}

pub fn friend_item_key(media_type: &str, name: &str) -> String {
    format!(
        "{}:{}",
        media_type.trim().to_ascii_lowercase(),
        identity_name(name)
    )
}

pub fn friend_scoped_item_key(media_type: &str, name: &str, subtype: Option<&str>) -> String {
    if media_type.eq_ignore_ascii_case("book") {
        if let Some(code) = subtype.map(|s| crate::user_state::canonical_subtype("book", s)) {
            if matches!(
                code.as_str(),
                "manga" | "comic" | "book" | "novel" | "light_novel" | "web_novel"
            ) {
                return friend_item_key(&format!("book.{code}"), name);
            }
        }
    }
    friend_item_key(media_type, name)
}

fn identity_name(text: &str) -> String {
    if text.is_ascii() {
        return collapse_ascii(text, u8::to_ascii_uppercase);
    }
    let upper: String = text
        .nfc()
        .map(|character| {
            let mut uppercase = character.to_uppercase();
            let first = uppercase.next().unwrap_or(character);
            // .NET invariant uppercase keeps one code point per character.
            if uppercase.next().is_some() {
                character
            } else {
                first
            }
        })
        .collect();
    collapse_whitespace(&upper)
}

fn collapse_ascii(text: &str, case: fn(&u8) -> u8) -> String {
    let mut out = String::with_capacity(text.len());
    let mut space = false;
    for byte in text.as_bytes() {
        if matches!(byte, b'\t'..=b'\r' | b' ') {
            space = !out.is_empty();
        } else {
            if std::mem::take(&mut space) {
                out.push(' ');
            }
            out.push(char::from(case(byte)));
        }
    }
    out
}

fn collapse_whitespace(text: &str) -> String {
    let mut out = String::with_capacity(text.len());
    for word in text.split_whitespace() {
        if !out.is_empty() {
            out.push(' ');
        }
        out.push_str(word);
    }
    out
}

fn dotnet_id(value: &str) -> Uuid {
    // Preserve CatalogStableId.Create, including Guid's mixed-endian byte layout.
    let namespace = Uuid::from_u128(0xc2f1d7a49b634d1e8a6f3e5d0b7c9a21);
    let mut hash = Sha256::new();
    hash.update(namespace.to_bytes_le());
    hash.update(value.as_bytes());
    let bytes: [u8; 16] = hash.finalize()[..16].try_into().unwrap();
    Uuid::from_bytes_le(bytes)
}

pub fn now() -> String {
    Utc::now().to_rfc3339_opts(chrono::SecondsFormat::Millis, true)
}

#[derive(Clone, Default, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CatalogQuery {
    #[serde(default)]
    pub q: String,
    pub media_type: Option<String>,
    #[serde(default)]
    pub favorites_only: bool,
    pub cursor: Option<String>,
    pub limit: Option<u32>,
    #[serde(flatten)]
    pub filters: CatalogFilters,
}

#[derive(Clone, Default, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CatalogFilters {
    pub list_id: Option<Uuid>,
    pub year: Option<i32>,
    pub genre: Option<String>,
    pub category: Option<String>,
    pub provider: Option<String>,
    pub status: Option<String>,
    pub subtype_group: Option<String>,
    #[serde(default)]
    pub summary: bool,
}

impl CatalogQuery {
    pub fn validate(&self) -> Result<()> {
        self.validate_limit(100)
    }
    pub(crate) fn validate_limit(&self, max: u32) -> Result<()> {
        if self.q.len() > 800 || !(1..=max).contains(&self.limit.unwrap_or(50)) {
            return Err(Error::Invalid(format!(
                "Search accepts at most 800 bytes and a limit from 1 to {max}"
            )));
        }
        let f = &self.filters;
        if f.year.is_some_and(|y| !(1800..=2200).contains(&y))
            || f.genre.as_deref().is_some_and(|s| !valid_genre_key(s))
            || f.category.iter().chain(&f.provider).any(|s| s.len() > 800)
            || f.status.as_ref().is_some_and(|s| {
                ![
                    "unknown",
                    "announced",
                    "ongoing",
                    "completed",
                    "cancelled",
                    "paused",
                ]
                .contains(&s.to_lowercase().as_str())
            })
            || f.subtype_group.as_ref().is_some_and(|s| {
                !["anime", "liveaction", "manga", "prose", "comic"]
                    .contains(&s.to_ascii_lowercase().as_str())
            })
        {
            return Err(Error::Invalid("Filtro de catálogo inválido.".into()));
        }
        if let Some(media) = &self.media_type {
            if !["movie", "book", "audio"].contains(&media.to_lowercase().as_str()) {
                return Err(Error::Invalid("Unknown media type".into()));
            }
        }
        if let Some(cursor) = &self.cursor {
            Uuid::parse_str(cursor).map_err(|_| Error::Invalid("Invalid catalog cursor".into()))?;
        }
        Ok(())
    }
}

#[derive(Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CatalogPage {
    pub revision: i64,
    pub continuation_token: Option<String>,
    pub document: CatalogDocument,
}

#[derive(Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LibraryPage {
    #[serde(flatten)]
    pub page: CatalogPage,
    pub favorite_ids: Vec<Uuid>,
    pub total: u64,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CatalogMetadata {
    pub revision: i64,
    pub updated_at: String,
    pub sha256: String,
    pub item_count: usize,
}
