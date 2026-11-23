use crate::*;
use rusqlite::{params, Connection, OptionalExtension};
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};
use std::collections::{HashMap, HashSet};
use uuid::Uuid;

pub const USER_FORMAT: &str = "universal-media-distribution.user-sync";

#[derive(Debug, Deserialize)]
#[serde(
    tag = "kind",
    rename_all = "camelCase",
    rename_all_fields = "camelCase"
)]
pub enum ListEdit {
    Create {
        name: String,
    },
    Rename {
        id: Uuid,
        name: String,
    },
    Delete {
        id: Uuid,
    },
    Member {
        id: Uuid,
        item_id: Uuid,
        enabled: bool,
    },
}

pub(crate) const MAX_LIST_NAME_CHARS: usize = 80;
const MAX_ACTIVE_LISTS: usize = 100;
const MAX_TOTAL_LIST_ITEMS: usize = 100_000;

fn list_name(name: &str) -> Result<&str> {
    let name = name.trim();
    if name.is_empty()
        || name.chars().count() > MAX_LIST_NAME_CHARS
        || name.chars().any(char::is_control)
    {
        return Err(invalid("Usá un nombre de lista de 1 a 80 caracteres."));
    }
    Ok(name)
}

pub(crate) fn unique_list_name(base: &str, used: &mut HashSet<String>) -> String {
    let base = base.trim();
    if used.insert(normalize(base)) {
        return base.to_owned();
    }
    for n in 2usize.. {
        let suffix = format!(" ({n})");
        let max_base = MAX_LIST_NAME_CHARS.saturating_sub(suffix.chars().count());
        let prefix: String = base.chars().take(max_base).collect();
        let candidate = format!("{}{}", prefix.trim_end(), suffix);
        if used.insert(normalize(&candidate)) {
            return candidate;
        }
    }
    unreachable!()
}

fn invalid(message: &str) -> Error {
    Error::Invalid(message.into())
}
fn valid_version(value: &Value) -> bool {
    value["clock"]
        .as_i64()
        .is_some_and(|n| n > 0 && n < i64::MAX)
        && value["deviceId"].as_str().is_some_and(|v| {
            !v.is_empty()
                && v.len() <= 128
                && v.bytes()
                    .all(|c| c.is_ascii_alphanumeric() || b"._-".contains(&c))
        })
        && value["operationId"]
            .as_str()
            .and_then(|v| Uuid::parse_str(v).ok())
            .is_some_and(|v| !v.is_nil())
        && value["modifiedAt"]
            .as_str()
            .and_then(|v| chrono::DateTime::parse_from_rfc3339(v).ok())
            .is_some()
}
/// Counts the bytes a document would serialize to without building the text.
struct ByteCount(usize);
impl std::io::Write for ByteCount {
    fn write(&mut self, bytes: &[u8]) -> std::io::Result<usize> {
        self.0 += bytes.len();
        Ok(bytes.len())
    }
    fn flush(&mut self) -> std::io::Result<()> {
        Ok(())
    }
}

pub fn validate_backup(document: &Value) -> Result<CatalogDocument> {
    validate_backup_with_limit(document, MAX_DOCUMENT_BYTES)
}

pub fn validate_backup_with_limit(document: &Value, max: usize) -> Result<CatalogDocument> {
    validate_envelope(document, max)?;
    // Read the catalog straight from the borrowed value: copying it first
    // doubled the memory held for a document that may be 32 MiB.
    let catalog = CatalogDocument::deserialize(&document["catalog"])?;
    catalog.validate()?;
    validate_personal(document, &catalog)?;
    Ok(catalog)
}

fn validate_envelope(document: &Value, max: usize) -> Result<()> {
    let mut size = ByteCount(0);
    serde_json::to_writer(&mut size, document)?;
    if size.0 > max {
        return Err(Error::DocumentTooLarge(max));
    }
    if document["format"] != USER_FORMAT
        || !document["version"]
            .as_u64()
            .is_some_and(|v| (1..=4).contains(&v))
    {
        return Err(invalid("Formato de respaldo UMD no compatible."));
    }
    let device = document["deviceId"].as_str().unwrap_or("");
    if device.is_empty()
        || device.len() > 128
        || !device
            .bytes()
            .all(|c| c.is_ascii_alphanumeric() || b"._-".contains(&c))
    {
        return Err(invalid("Dispositivo inválido."));
    }
    let name = document["deviceName"].as_str().unwrap_or("");
    if name.trim().is_empty()
        || name.chars().count() > 100
        || name.chars().any(char::is_control)
        || chrono::DateTime::parse_from_rfc3339(document["exportedAt"].as_str().unwrap_or(""))
            .is_err()
    {
        return Err(invalid("Datos del respaldo inválidos."));
    }
    Ok(())
}

pub(crate) fn canonical_subtype(media: &str, subtype: &str) -> String {
    let media = media.trim().to_ascii_lowercase();
    let norm = subtype.trim().replace(['-', ' '], "_").to_ascii_lowercase();
    let suffix = norm.strip_prefix(&format!("{media}.")).unwrap_or(&norm);
    let suffix = if media == "audio" && suffix == "music_album" {
        "album"
    } else {
        suffix
    };
    suffix.to_owned()
}

fn item_aliases(item: &CatalogItem) -> Vec<String> {
    let mut out = Vec::new();
    if let Some(orig) = item.metadata.get("originalName").and_then(Value::as_str) {
        let n = normalize(orig);
        if !n.is_empty() {
            out.push(n);
        }
    }
    for key in ["alternativeNames", "aliases"] {
        if let Some(arr) = item.metadata.get(key).and_then(Value::as_array) {
            for entry in arr {
                let raw = entry.as_str().or_else(|| entry["name"].as_str());
                if let Some(name) = raw {
                    let n = normalize(name);
                    if !n.is_empty() {
                        out.push(n);
                    }
                }
            }
        }
    }
    out
}

pub(crate) fn validate_personal(document: &Value, catalog: &CatalogDocument) -> Result<()> {
    for key in [
        "progress",
        "readerPreferences",
        "favoriteContributorKeys",
        "favoriteStates",
        "favoriteItemStates",
        "lists",
        "catalogRedirects",
    ] {
        if !document[key].is_null() && document[key].as_array().is_none_or(|v| v.len() > 250_000) {
            return Err(invalid("Colección de respaldo inválida."));
        }
    }
    let mut redirects = HashMap::new();
    for row in document["catalogRedirects"]
        .as_array()
        .into_iter()
        .flatten()
    {
        if !valid_version(&row["syncVersion"]) {
            return Err(invalid("Versión de estado personal inválida."));
        }
        let from = row["fromId"]
            .as_str()
            .and_then(|v| Uuid::parse_str(v).ok())
            .filter(|v| !v.is_nil())
            .ok_or_else(|| invalid("Redirección de catálogo inválida."))?;
        let to = row["toId"]
            .as_str()
            .and_then(|v| Uuid::parse_str(v).ok())
            .filter(|v| !v.is_nil())
            .ok_or_else(|| invalid("Redirección de catálogo inválida."))?;
        if from == to || redirects.insert(from, to).is_some() {
            return Err(invalid("Redirección de catálogo inválida."));
        }
    }
    for &from in redirects.keys() {
        crate::reconcile_catalog::destination(from, &redirects)?;
    }
    let mut by_id = HashMap::with_capacity(catalog.items.len());
    for item in &catalog.items {
        by_id.insert(item.id()?, item);
        if let Some(raw) = item.sync_id {
            by_id.entry(raw).or_insert(item);
        }
    }
    let mut by_name = HashMap::new();
    let mut by_alias = HashMap::new();
    for item in &catalog.items {
        let media = item.media_type.to_ascii_lowercase();
        let name = normalize(&item.name);
        let sub = canonical_subtype(&media, &item.subtype);
        by_name.entry((media.clone(), name.clone(), None)).or_insert(item);
        by_name.insert((media.clone(), name, Some(sub.clone())), item);
        for alias in item_aliases(item) {
            by_alias.entry((media.clone(), alias.clone(), None)).or_insert(item);
            by_alias.entry((media.clone(), alias, Some(sub.clone()))).or_insert(item);
        }
    }
    let lookup_by_name = |media: &str, title: &str, subtype: Option<&str>| -> Option<&CatalogItem> {
        let name = normalize(title);
        let sub = subtype
            .map(|s| canonical_subtype(media, s))
            .filter(|s| !s.is_empty());
        if let Some(ref s) = sub {
            if let Some(item) = by_name.get(&(media.to_owned(), name.clone(), Some(s.clone()))) {
                return Some(*item);
            }
        }
        if let Some(item) = by_name.get(&(media.to_owned(), name.clone(), None)) {
            return Some(*item);
        }
        if let Some(ref s) = sub {
            if let Some(item) = by_alias.get(&(media.to_owned(), name.clone(), Some(s.clone()))) {
                return Some(*item);
            }
        }
        by_alias.get(&(media.to_owned(), name, None)).copied()
    };
    let validate_identity =
        |identity: &Value, allow_missing: bool| -> Result<(Option<Uuid>, String)> {
            let title = identity["name"]
                .as_str()
                .filter(|v| !v.trim().is_empty() && v.len() <= 2048)
                .ok_or_else(|| invalid("Título de registro inválido."))?;
            let media_lower = identity["mediaType"]
                .as_str()
                .map(|v| v.trim().to_ascii_lowercase())
                .filter(|v| matches!(v.as_str(), "movie" | "book" | "audio"))
                .ok_or_else(|| invalid("Medio de registro inválido."))?;
            let media = media_lower.as_str();
            let (resolved_id, found) = if !identity["itemId"].is_null() {
                let parsed = identity["itemId"]
                    .as_str()
                    .and_then(|raw| Uuid::parse_str(raw).ok())
                    .filter(|v| !v.is_nil())
                    .ok_or_else(|| {
                        invalid("El respaldo contiene referencias a títulos inexistentes.")
                    })?;
                let target = crate::reconcile_catalog::destination(parsed, &redirects)?;
                (Some(target), by_id.get(&target).copied())
            } else {
                let item = lookup_by_name(media, title, identity["subtype"].as_str());
                (item.map(CatalogItem::id).transpose()?, item)
            };
            if let Some(item) = found {
                if !media.eq_ignore_ascii_case(&item.media_type) {
                    return Err(invalid("Identidad inválida."));
                }
                let id = item.id()?;
                return Ok((Some(id), id.to_string()));
            }
            if allow_missing {
                let fallback_key = resolved_id.map_or_else(
                    || {
                        format!(
                            "{}:{}:{}",
                            media,
                            normalize(title),
                            identity["subtype"]
                                .as_str()
                                .map(|s| canonical_subtype(media, s))
                                .unwrap_or_default()
                        )
                    },
                    |id| id.to_string(),
                );
                return Ok((resolved_id, fallback_key));
            }
            Err(invalid(
                "El respaldo contiene referencias a títulos inexistentes.",
            ))
        };
    let version = document["version"].as_u64().unwrap_or(0);
    for key in ["progress", "favoriteItemStates", "readerPreferences"] {
        let mut seen = HashSet::new();
        for row in document[key].as_array().into_iter().flatten() {
            let identity = if key == "progress" {
                &row["content"]["item"]
            } else {
                &row["item"]
            };
            if key == "favoriteItemStates" && !row["isFavorite"].is_boolean() {
                return Err(invalid("Favorito inválido."));
            }
            let allow_missing =
                key == "favoriteItemStates" && row["isFavorite"].as_bool() == Some(false);
            let (_, item_key) = validate_identity(identity, allow_missing)?;
            if version >= 2 && !valid_version(&row["syncVersion"]) {
                return Err(invalid("Versión de registro inválida."));
            }
            let content = if key == "progress" {
                json!({"id":item_key,"unitKind":row["content"]["unitKind"],"displayNumber":row["content"]["displayNumber"],"seasonNumber":row["content"]["seasonNumber"]})
            } else {
                json!({"id":item_key})
            };
            if !seen.insert(serde_json::to_string(&content)?) {
                return Err(invalid("Registro de respaldo duplicado."));
            }
            if key == "progress"
                && (!row["position"]
                    .as_f64()
                    .is_some_and(|v| v.is_finite() && v >= 0.0)
                    || !matches!(row["positionKind"].as_str(), Some("seconds" | "page")))
            {
                return Err(invalid("Posición de lectura o reproducción inválida."));
            }
            if key == "progress" {
                let content = &row["content"];
                let kind = content["unitKind"].as_str();
                let number = content["displayNumber"].as_str();
                if kind.is_some() != number.is_some()
                    || kind.is_some_and(|v| {
                        !matches!(v, "episode" | "chapter" | "track" | "audio_chapter")
                    })
                    || number.is_some_and(|v| v.trim().is_empty() || v.len() > 128)
                    || (!content["seasonNumber"].is_null()
                        && content["seasonNumber"].as_i64().is_none_or(|v| v < 0))
                    || (kind.is_none() && !content["seasonNumber"].is_null())
                    || !row["isCompleted"].is_boolean()
                    || chrono::DateTime::parse_from_rfc3339(row["updatedAt"].as_str().unwrap_or(""))
                        .is_err()
                {
                    return Err(invalid("Identidad o fecha de progreso inválida."));
                }
            }
            if key == "readerPreferences"
                && !matches!(row["displayMode"].as_str(), Some("paged" | "continuous"))
            {
                return Err(invalid("Preferencia de lectura inválida."));
            }
        }
    }
    let mut seen_contributors = HashSet::new();
    for row in document["favoriteStates"].as_array().into_iter().flatten() {
        if !valid_version(&row["syncVersion"]) {
            return Err(invalid("Versión de estado personal inválida."));
        }
        let key = row["contributorKey"]
            .as_str()
            .map(str::trim)
            .filter(|v| !v.is_empty() && v.chars().count() <= 256)
            .ok_or_else(|| invalid("Favorito de colaborador inválido."))?;
        if !row["isFavorite"].is_boolean() || !seen_contributors.insert(key.to_owned()) {
            return Err(invalid("Favorito de colaborador inválido."));
        }
    }
    let mut seen_lists = HashSet::new();
    let mut total_list_items: usize = 0;
    let mut total_list_states: usize = 0;
    for row in document["lists"].as_array().into_iter().flatten() {
        if version >= 2 && !valid_version(&row["syncVersion"]) {
            return Err(invalid("Versión de estado personal inválida."));
        }
        let list_id = row["id"]
            .as_str()
            .and_then(|v| Uuid::parse_str(v).ok())
            .filter(|v| !v.is_nil())
            .ok_or_else(|| invalid("Lista personal inválida."))?;
        if !seen_lists.insert(list_id)
            || !row["name"]
                .as_str()
                .is_some_and(|v| !v.trim().is_empty() && v.chars().count() <= 100)
            || !row["isDeleted"].is_boolean()
        {
            return Err(invalid("Lista personal inválida."));
        }
        let items = row["items"]
            .as_array()
            .ok_or_else(|| invalid("Lista personal inválida."))?;
        total_list_items = total_list_items.saturating_add(items.len());
        if total_list_items > MAX_TOTAL_LIST_ITEMS {
            return Err(invalid("Lista personal inválida."));
        }
        for item_ref in items {
            validate_identity(item_ref, false)?;
        }
        if !row["itemStates"].is_null() {
            let states = row["itemStates"]
                .as_array()
                .ok_or_else(|| invalid("Estado de lista inválido."))?;
            total_list_states = total_list_states.saturating_add(states.len());
            if total_list_states > MAX_TOTAL_LIST_ITEMS {
                return Err(invalid("Estado de lista inválido."));
            }
            let mut seen_states = HashSet::new();
            for state in states {
                if !valid_version(&state["syncVersion"]) || !state["isMember"].is_boolean() {
                    return Err(invalid("Estado de lista inválido."));
                }
                let (_, item_key) = validate_identity(&state["item"], false)?;
                if !seen_states.insert(item_key) {
                    return Err(invalid("Estado de lista inválido."));
                }
            }
        }
    }
    Ok(())
}
fn resolve<'a>(catalog: &'a CatalogDocument, identity: &Value) -> Option<&'a CatalogItem> {
    if let Some(id) = identity["itemId"]
        .as_str()
        .and_then(|v| Uuid::parse_str(v).ok())
    {
        return catalog
            .items
            .iter()
            .find(|i| i.id().ok() == Some(id) || i.sync_id == Some(id));
    }
    let name = identity["name"].as_str().map(normalize)?;
    let media = identity["mediaType"].as_str()?.trim();
    let sub = identity["subtype"]
        .as_str()
        .map(|s| canonical_subtype(media, s))
        .filter(|s| !s.is_empty());
    if let Some(ref wanted) = sub {
        if let Some(found) = catalog.items.iter().find(|i| {
            normalize(&i.name) == name
                && media.eq_ignore_ascii_case(&i.media_type)
                && canonical_subtype(&i.media_type, &i.subtype) == *wanted
        }) {
            return Some(found);
        }
    }
    if let Some(found) = catalog
        .items
        .iter()
        .find(|i| normalize(&i.name) == name && media.eq_ignore_ascii_case(&i.media_type))
    {
        return Some(found);
    }
    if let Some(ref wanted) = sub {
        if let Some(found) = catalog.items.iter().find(|i| {
            media.eq_ignore_ascii_case(&i.media_type)
                && canonical_subtype(&i.media_type, &i.subtype) == *wanted
                && item_aliases(i).iter().any(|a| a == &name)
        }) {
            return Some(found);
        }
    }
    catalog.items.iter().find(|i| {
        media.eq_ignore_ascii_case(&i.media_type) && item_aliases(i).iter().any(|a| a == &name)
    })
}
/// Titles of one catalog by identity. Building it once replaces a scan of the
/// whole catalog, hashing every name, for each personal record.
pub(crate) struct Titles<'a> {
    catalog: &'a CatalogDocument,
    by_id: HashMap<Uuid, &'a CatalogItem>,
}
impl<'a> Titles<'a> {
    pub(crate) fn new(catalog: &'a CatalogDocument) -> Self {
        let mut by_id = HashMap::with_capacity(catalog.items.len());
        for item in &catalog.items {
            if let Ok(id) = item.id() {
                // The first title with an identity wins, as a linear search did.
                by_id.entry(id).or_insert(item);
            }
            if let Some(raw) = item.sync_id {
                by_id.entry(raw).or_insert(item);
            }
        }
        Self { catalog, by_id }
    }
    pub(crate) fn resolve(
        &self,
        redirects: &HashMap<Uuid, Uuid>,
        identity: &Value,
    ) -> Option<&'a CatalogItem> {
        if let Some(id) = identity["itemId"]
            .as_str()
            .and_then(|v| Uuid::parse_str(v).ok())
        {
            let target = crate::reconcile_catalog::destination(id, redirects).ok()?;
            return self.by_id.get(&target).copied();
        }
        resolve(self.catalog, identity)
    }
}
pub(crate) fn identity(item: &CatalogItem) -> Result<Value> {
    Ok(
        json!({"itemId":item.id()?,"name":item.name,"mediaType":item.media_type.to_lowercase(),"subtype":item.subtype}),
    )
}
// Older backups identify a title only by name (or by a .NET syncId that differs
// from the canonical Rust book ID). Resolve those targets against the catalog.
pub(crate) fn bind_legacy_targets(document: &mut Value, catalog: &CatalogDocument) -> Result<()> {
    let bind = |target: &mut Value| -> Result<()> {
        if let Some(item) = resolve(catalog, target) {
            let canonical = item.id()?;
            if target["itemId"].is_null()
                || target["itemId"]
                    .as_str()
                    .and_then(|s| Uuid::parse_str(s).ok())
                    != Some(canonical)
            {
                target["itemId"] = json!(canonical);
            }
        }
        Ok(())
    };
    for key in ["progress", "favoriteItemStates", "readerPreferences"] {
        for row in document[key].as_array_mut().into_iter().flatten() {
            let target = if key == "progress" {
                &mut row["content"]["item"]
            } else {
                &mut row["item"]
            };
            bind(target)?;
        }
    }
    for list in document["lists"].as_array_mut().into_iter().flatten() {
        for field in ["items", "itemStates"] {
            for row in list[field].as_array_mut().into_iter().flatten() {
                let target = if field == "items" {
                    row
                } else {
                    &mut row["item"]
                };
                bind(target)?;
            }
        }
    }
    Ok(())
}
pub(crate) fn migrate_legacy_targets(db: &Connection) -> Result<()> {
    let saved: Option<String> = db
        .query_row(
            "SELECT value FROM rust_settings WHERE key='user_document'",
            [],
            |r| r.get(0),
        )
        .optional()?;
    let Some(saved) = saved else { return Ok(()) };
    let mut doc: Value = serde_json::from_str(&saved)?;
    let legacy = ["favoriteItemStates", "readerPreferences"].iter().any(|k| {
        doc[k]
            .as_array()
            .into_iter()
            .flatten()
            .any(|r| r["item"]["itemId"].is_null())
    }) || doc["lists"].as_array().into_iter().flatten().any(|l| {
        l["items"]
            .as_array()
            .into_iter()
            .flatten()
            .any(|r| r["itemId"].is_null())
            || l["itemStates"]
                .as_array()
                .into_iter()
                .flatten()
                .any(|r| r["item"]["itemId"].is_null())
    }) || doc["progress"]
        .as_array()
        .into_iter()
        .flatten()
        .any(|r| r["content"]["item"]["itemId"].is_null());
    if legacy {
        bind_legacy_targets(&mut doc, &CatalogStore::export_from(db)?)?;
        db.execute(
            "UPDATE rust_settings SET value=?1 WHERE key='user_document'",
            [serde_json::to_string(&doc)?],
        )?;
    }
    Ok(())
}
fn matches_item(reference: &Value, target: &Value) -> bool {
    if let Some(id) = reference["itemId"]
        .as_str()
        .and_then(|raw| Uuid::parse_str(raw).ok())
    {
        return target["itemId"]
            .as_str()
            .and_then(|raw| Uuid::parse_str(raw).ok())
            == Some(id);
    }
    reference["name"]
        .as_str()
        .zip(target["name"].as_str())
        .is_some_and(|(a, b)| normalize(a) == normalize(b))
        && reference["mediaType"]
            .as_str()
            .zip(target["mediaType"].as_str())
            .is_some_and(|(a, b)| a.eq_ignore_ascii_case(b))
        && reference["subtype"].as_str().is_none_or(|s| {
            target["subtype"]
                .as_str()
                .is_some_and(|t| s.eq_ignore_ascii_case(t))
        })
}
fn same_content(a: &Value, b: &Value) -> bool {
    matches_item(&a["item"], &b["item"])
        && a["unitKind"] == b["unitKind"]
        && a["displayNumber"] == b["displayNumber"]
        && a["seasonNumber"] == b["seasonNumber"]
}
fn version(device: &str, clock: i64) -> Value {
    json!({"clock":clock,"deviceId":device,"operationId":Uuid::new_v4(),"modifiedAt":now()})
}

/// A position is saved every few seconds while something plays. Rewriting the
/// profile's whole document each time wrote megabytes for a change of a few
/// bytes, so a position goes to `rust_progress_pending` first: one small row
/// per chapter or episode. `read_state` applies those rows, so every reader
/// sees them, and `save_state` folds them into the document.
/// `rust_profile_clock` keeps the profile's highest sync clock so stamping a
/// position does not need the document either.
const MAX_PENDING_POSITIONS: i64 = 32;

pub(crate) fn initialize(db: &Connection) -> Result<()> {
    db.execute_batch(
        "CREATE TABLE IF NOT EXISTS rust_progress_pending(
            profile_id TEXT NOT NULL REFERENCES rust_profiles(id) ON DELETE CASCADE,
            content_key TEXT NOT NULL, clock INTEGER NOT NULL, entry TEXT NOT NULL,
            PRIMARY KEY(profile_id,content_key));
         CREATE TABLE IF NOT EXISTS rust_profile_clock(
            profile_id TEXT PRIMARY KEY REFERENCES rust_profiles(id) ON DELETE CASCADE,
            device_id TEXT NOT NULL, clock INTEGER NOT NULL, document_bytes INTEGER NOT NULL);",
    )?;
    Ok(())
}

fn apply_pending(db: &Connection, profile: Uuid, doc: &mut Value) -> Result<()> {
    let mut pending = db.prepare_cached(
        "SELECT entry FROM rust_progress_pending WHERE profile_id=?1 ORDER BY clock",
    )?;
    let mut entries = pending.query([profile.to_string()])?;
    while let Some(entry) = entries.next()? {
        let row: Value = serde_json::from_str(&entry.get::<_, String>(0)?)?;
        if !doc["progress"].is_array() {
            doc["progress"] = json!([]);
        }
        let Some(rows) = doc["progress"].as_array_mut() else {
            continue;
        };
        let clock = row["syncVersion"]["clock"].as_i64().unwrap_or(0);
        // A document saved by another build after this row was queued already
        // holds something newer for the same chapter: keep that.
        if rows.iter().any(|r| {
            same_content(&r["content"], &row["content"])
                && r["syncVersion"]["clock"].as_i64().unwrap_or(0) >= clock
        }) {
            continue;
        }
        rows.retain(|r| !same_content(&r["content"], &row["content"]));
        rows.push(row);
    }
    Ok(())
}

pub(crate) fn read_state(db: &Connection, profile: Uuid) -> Result<Value> {
    let saved: Option<String> = db
        .prepare_cached("SELECT document FROM rust_profiles WHERE id=?1")?
        .query_row([profile.to_string()], |r| r.get(0))?;
    if let Some(saved) = saved {
        let mut doc = serde_json::from_str(&saved)?;
        apply_pending(db, profile, &mut doc)?;
        return Ok(doc);
    }
    let device = Uuid::new_v4().to_string();
    let mut doc = json!({"format":USER_FORMAT,"version":4,"exportedAt":now(),"deviceId":device,"deviceName":"UMD Desktop","progress":[],"readerPreferences":[],"favoriteContributorKeys":[],"favoriteStates":[],"favoriteItemStates":[],"lists":[],"catalogRedirects":[]});
    let mut stmt = db.prepare(
        "SELECT c.payload FROM rust_favorites f JOIN rust_catalog c ON c.id=f.id WHERE f.profile_id=?1 ORDER BY f.id",
    )?;
    for payload in stmt.query_map([profile.to_string()], |r| r.get::<_, String>(0))? {
        let item: CatalogItem = serde_json::from_str(&payload?)?;
        if let Some(arr) = doc["favoriteItemStates"].as_array_mut() {
            arr.push(
                json!({"item":identity(&item)?,"isFavorite":true,"syncVersion":version(&device,1)}),
            );
        }
    }
    Ok(doc)
}
pub(crate) fn save_state(db: &Connection, profile: Uuid, document: &Value) -> Result<()> {
    let text = serde_json::to_string(document)?;
    let id = profile.to_string();
    if db
        .prepare_cached("UPDATE rust_profiles SET document=?1 WHERE id=?2")?
        .execute(params![text, id])?
        == 0
    {
        return Err(invalid("El perfil ya no existe."));
    }
    // Callers build `document` from `read_state`, so it holds every pending
    // position, or they replace the personal state outright.
    db.prepare_cached("DELETE FROM rust_progress_pending WHERE profile_id=?1")?
        .execute([&id])?;
    match document["deviceId"].as_str().filter(|s| !s.is_empty()) {
        Some(device) => db
            .prepare_cached(
                "INSERT INTO rust_profile_clock(profile_id,device_id,clock,document_bytes)
                 VALUES(?1,?2,?3,?4) ON CONFLICT(profile_id) DO UPDATE SET
                 device_id=excluded.device_id,clock=excluded.clock,
                 document_bytes=excluded.document_bytes",
            )?
            .execute(params![id, device, max_clock(document), text.len() as i64])?,
        None => db
            .prepare_cached("DELETE FROM rust_profile_clock WHERE profile_id=?1")?
            .execute([&id])?,
    };
    Ok(())
}
fn next_version(doc: &Value) -> Result<Value> {
    let device = doc["deviceId"]
        .as_str()
        .filter(|s| !s.is_empty())
        .ok_or_else(|| invalid("Dispositivo inválido."))?;
    // valid_version requires 0 < clock < i64::MAX, so cap at i64::MAX - 1.
    let next_clock = max_clock(doc).clamp(0, i64::MAX - 2) + 1;
    Ok(version(device, next_clock))
}
fn max_clock(doc: &Value) -> i64 {
    [
        "progress",
        "favoriteItemStates",
        "readerPreferences",
        "favoriteStates",
        "lists",
        "catalogRedirects",
    ]
    .iter()
    .flat_map(|key| doc[key].as_array().into_iter().flatten())
    .filter_map(|row| row["syncVersion"]["clock"].as_i64())
    .chain(
        doc["lists"]
            .as_array()
            .into_iter()
            .flatten()
            .flat_map(|l| l["itemStates"].as_array().into_iter().flatten())
            .filter_map(|r| r["syncVersion"]["clock"].as_i64()),
    )
    .max()
    .unwrap_or(0)
}
fn redirect_map(doc: &Value) -> HashMap<Uuid, Uuid> {
    doc["catalogRedirects"]
        .as_array()
        .into_iter()
        .flatten()
        .filter_map(|row| {
            let from = row["fromId"]
                .as_str()
                .and_then(|s| Uuid::parse_str(s).ok())?;
            let to = row["toId"].as_str().and_then(|s| Uuid::parse_str(s).ok())?;
            Some((from, to))
        })
        .collect()
}
#[derive(Clone, Debug, Deserialize, Serialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct ProgressTarget {
    pub item_id: Uuid,
    pub unit_kind: Option<String>,
    pub display_number: Option<String>,
    pub season_number: Option<i64>,
}
impl ProgressTarget {
    fn content(&self, db: &Connection) -> Result<Value> {
        let item = CatalogStore::summary_from(db, self.item_id)?
            .ok_or_else(|| invalid("Título inexistente."))?;
        if self.unit_kind.is_some() != self.display_number.is_some()
            || self.season_number.is_some_and(|v| v < 0)
            || (self.unit_kind.is_none() && self.season_number.is_some())
            || self
                .display_number
                .as_ref()
                .is_some_and(|v| v.is_empty() || v.len() > 128)
            || self.unit_kind.as_ref().is_some_and(|v| {
                !matches!(
                    v.as_str(),
                    "episode" | "chapter" | "track" | "audio_chapter"
                )
            })
        {
            return Err(invalid("Capítulo o episodio inválido."));
        }
        Ok(
            json!({"item":identity(&item)?,"unitKind":self.unit_kind,"displayNumber":self.display_number,"seasonNumber":self.season_number}),
        )
    }
}
impl CatalogStore {
    pub fn reader_mode(&self, item_id: Uuid, mode: Option<&str>) -> Result<String> {
        let mut db = self.0.lock().map_err(|_| Error::Lock)?;
        let tx = db.transaction_with_behavior(rusqlite::TransactionBehavior::Immediate)?;
        let item =
            Self::summary_from(&tx, item_id)?.ok_or_else(|| invalid("Título inexistente."))?;
        let target = identity(&item)?;
        let mut doc = read_state(&tx, self.1)?;
        let Some(mode) = mode else {
            return Ok(doc["readerPreferences"]
                .as_array()
                .into_iter()
                .flatten()
                .find(|r| matches_item(&r["item"], &target))
                .and_then(|r| r["displayMode"].as_str())
                .unwrap_or("paged")
                .into());
        };
        if !matches!(mode, "paged" | "continuous") {
            return Err(invalid("Modo de lectura inválido."));
        }
        let version = next_version(&doc)?;
        let rows = doc["readerPreferences"]
            .as_array_mut()
            .ok_or_else(|| invalid("Preferencias de lectura inválidas."))?;
        rows.retain(|row| !matches_item(&row["item"], &target));
        rows.push(
            json!({"item":target,"displayMode":mode,"updatedAt":now(),"syncVersion":version}),
        );
        save_state(&tx, self.1, &doc)?;
        tx.commit()?;
        Ok(mode.into())
    }

    pub fn personal_lists(&self) -> Result<Vec<Value>> {
        let db = self.0.lock().map_err(|_| Error::Lock)?;
        Ok(read_state(&db, self.1)?["lists"]
            .as_array()
            .into_iter()
            .flatten()
            .filter(|l| l["isDeleted"] != true)
            .cloned()
            .collect())
    }

    /// Save the reviewed public catalog and its membership as one private copy.
    pub fn save_shared_list(
        &self,
        link: &str,
        name: &str,
        catalog: &CatalogDocument,
    ) -> Result<bool> {
        use sha2::{Digest, Sha256};
        let name = list_name(name)?;
        catalog.validate()?;
        let digest = Sha256::digest(format!("umd.shared-list.copy\n{link}").as_bytes());
        let mut bytes = [0u8; 16];
        bytes.copy_from_slice(&digest[..16]);
        let list_id = uuid::Builder::from_custom_bytes(bytes)
            .into_uuid()
            .to_string();
        let mut db = self.0.lock().map_err(|_| Error::Lock)?;
        let tx = db.transaction_with_behavior(rusqlite::TransactionBehavior::Immediate)?;
        let mut doc = read_state(&tx, self.1)?;
        let version = next_version(&doc)?;
        let lists = doc["lists"]
            .as_array_mut()
            .ok_or_else(|| invalid("Listas inválidas."))?;
        if lists
            .iter()
            .any(|l| l["id"] == list_id && l["isDeleted"] != true)
        {
            return Ok(false);
        }
        let active_count = lists.iter().filter(|l| l["isDeleted"] != true).count();
        if active_count >= MAX_ACTIVE_LISTS {
            return Err(invalid("No se pueden crear más de 100 listas."));
        }
        let mut used_names: HashSet<String> = lists
            .iter()
            .filter(|l| l["isDeleted"] != true)
            .filter_map(|l| l["name"].as_str().map(normalize))
            .collect();
        let final_name = unique_list_name(name, &mut used_names);
        let mut additions = Vec::new();
        let mut members = Vec::new();
        let mut states = Vec::new();
        for item in &catalog.items {
            if item.deleted {
                return Err(invalid("La lista contiene un título eliminado."));
            }
            let id = item.id()?;
            let mut existing = Self::summary_from(&tx, id)?;
            if existing.is_none() && item.sync_id.is_none() {
                let mut lookup =
                    tx.prepare("SELECT payload FROM rust_catalog WHERE name_key=?1 LIMIT 2")?;
                let mut rows = lookup.query([item.key()])?;
                if let Some(row) = rows.next()? {
                    existing = Some(serde_json::from_str(&row.get::<_, String>(0)?)?);
                }
                if rows.next()?.is_some() {
                    return Err(invalid("La lista requiere un identificador para distinguir títulos con el mismo nombre."));
                }
            }
            let reference = if let Some(local) = existing {
                identity(&local)?
            } else {
                let mut added = item.clone();
                added.sync_id = Some(id);
                additions.push(added);
                identity(item)?
            };
            states.push(json!({"item":reference,"isMember":true,"syncVersion":version}));
            members.push(reference);
        }
        if !additions.is_empty() {
            Self::import_into(&tx, &CatalogDocument::new(additions), false)?;
        }
        lists.retain(|l| l["id"] != list_id);
        let stamp = now();
        lists.push(
            json!({"id":list_id,"name":final_name,"createdAt":stamp,"updatedAt":stamp,"isDeleted":false,
            "items":members,"itemStates":states,"syncVersion":version}),
        );
        save_state(&tx, self.1, &doc)?;
        tx.commit()?;
        Ok(true)
    }

    pub fn edit_list(&self, action: ListEdit) -> Result<()> {
        let mut db = self.0.lock().map_err(|_| Error::Lock)?;
        let tx = db.transaction_with_behavior(rusqlite::TransactionBehavior::Immediate)?;
        let mut doc = read_state(&tx, self.1)?;
        let version = next_version(&doc)?;
        let stamp = now();
        let lists = doc["lists"]
            .as_array_mut()
            .ok_or_else(|| invalid("Listas inválidas."))?;
        match action {
            ListEdit::Create { name } => {
                let name = list_name(&name)?;
                let active_count = lists.iter().filter(|l| l["isDeleted"] != true).count();
                if active_count >= MAX_ACTIVE_LISTS {
                    return Err(invalid("No se pueden crear más de 100 listas."));
                }
                let key = normalize(name);
                if lists.iter().any(|l| {
                    l["isDeleted"] != true
                        && l["name"].as_str().is_some_and(|n| normalize(n) == key)
                }) {
                    return Err(invalid("Ya existe una lista con ese nombre."));
                }
                lists.push(
                    json!({"id":Uuid::new_v4(),"name":name,"createdAt":stamp,"updatedAt":stamp,
                    "isDeleted":false,"items":[],"itemStates":[],"syncVersion":version}),
                );
            }
            edit => {
                let membership = matches!(edit, ListEdit::Member { .. });
                let id = match &edit {
                    ListEdit::Rename { id, .. }
                    | ListEdit::Delete { id }
                    | ListEdit::Member { id, .. } => *id,
                    ListEdit::Create { .. } => unreachable!(),
                };
                if let ListEdit::Rename { name, .. } = &edit {
                    let trimmed = list_name(name)?;
                    let key = normalize(trimmed);
                    if lists.iter().any(|l| {
                        l["isDeleted"] != true
                            && l["id"].as_str().and_then(|s| Uuid::parse_str(s).ok()) != Some(id)
                            && l["name"].as_str().is_some_and(|n| normalize(n) == key)
                    }) {
                        return Err(invalid("Ya existe una lista con ese nombre."));
                    }
                }
                let list = lists
                    .iter_mut()
                    .find(|l| {
                        l["id"].as_str().and_then(|s| Uuid::parse_str(s).ok()) == Some(id)
                            && l["isDeleted"] != true
                    })
                    .ok_or_else(|| invalid("La lista ya no está disponible."))?;
                match edit {
                    ListEdit::Rename { name, .. } => list["name"] = json!(list_name(&name)?),
                    ListEdit::Delete { .. } => {
                        list["isDeleted"] = json!(true);
                        list["items"] = json!([]);
                        list["itemStates"] = json!([]);
                    }
                    ListEdit::Member {
                        item_id, enabled, ..
                    } => {
                        let item = Self::summary_from(&tx, item_id)?
                            .ok_or_else(|| invalid("Título inexistente."))?;
                        let target = identity(&item)?;
                        let items = list["items"]
                            .as_array_mut()
                            .ok_or_else(|| invalid("Lista inválida."))?;
                        items.retain(|r| !matches_item(r, &target));
                        if enabled {
                            items.push(target.clone());
                        }
                        if list["itemStates"].is_null() {
                            list["itemStates"] = json!([]);
                        }
                        let states = list["itemStates"]
                            .as_array_mut()
                            .ok_or_else(|| invalid("Estado de lista inválido."))?;
                        states.retain(|r| !matches_item(&r["item"], &target));
                        states
                            .push(json!({"item":target,"isMember":enabled,"syncVersion":version}));
                    }
                    ListEdit::Create { .. } => unreachable!(),
                }
                list["updatedAt"] = json!(stamp);
                if !membership {
                    list["syncVersion"] = version;
                }
            }
        }
        save_state(&tx, self.1, &doc)?;
        tx.commit()?;
        Ok(())
    }

    pub(crate) fn change_favorite(&self, id: Uuid, enabled: bool) -> Result<()> {
        let mut db = self.0.lock().map_err(|_| Error::Lock)?;
        let tx = db.transaction_with_behavior(rusqlite::TransactionBehavior::Immediate)?;
        let item = Self::summary_from(&tx, id)?.ok_or_else(|| invalid("Título inexistente."))?;
        let target = identity(&item)?;
        let mut doc = read_state(&tx, self.1)?;
        let version = next_version(&doc)?;
        let rows = doc["favoriteItemStates"]
            .as_array_mut()
            .ok_or_else(|| invalid("Favoritos inválidos."))?;
        rows.retain(|row| !matches_item(&row["item"], &target));
        rows.push(json!({"item":target,"isFavorite":enabled,"syncVersion":version}));
        if enabled {
            tx.execute(
                "INSERT OR IGNORE INTO rust_favorites(profile_id,id) VALUES(?2,?1)",
                params![id.to_string(), self.1.to_string()],
            )?;
        } else {
            tx.execute(
                "DELETE FROM rust_favorites WHERE id=?1 AND profile_id=?2",
                params![id.to_string(), self.1.to_string()],
            )?;
        }
        save_state(&tx, self.1, &doc)?;
        tx.commit()?;
        Ok(())
    }
    pub fn save_progress(
        &self,
        target: &ProgressTarget,
        position: f64,
        kind: &str,
        completed: bool,
    ) -> Result<()> {
        if !position.is_finite() || position < 0.0 || !matches!(kind, "seconds" | "page") {
            return Err(invalid("Posición inválida."));
        }
        let mut db = self.0.lock().map_err(|_| Error::Lock)?;
        let tx = db.transaction_with_behavior(rusqlite::TransactionBehavior::Immediate)?;
        let content = target.content(&tx)?;
        let profile = self.1.to_string();
        // The clock row is trusted only while it describes the stored document.
        let stamp: Option<(String, i64)> = tx
            .prepare_cached(
                "SELECT k.device_id,k.clock FROM rust_profile_clock k
                 JOIN rust_profiles p ON p.id=k.profile_id
                 WHERE k.profile_id=?1 AND octet_length(p.document)=k.document_bytes
                 AND (SELECT count(*) FROM rust_progress_pending q WHERE q.profile_id=?1)<?2",
            )?
            .query_row(params![profile, MAX_PENDING_POSITIONS], |r| {
                Ok((r.get(0)?, r.get(1)?))
            })
            .optional()?;
        if let Some((device, clock)) = stamp {
            let next = clock.clamp(0, i64::MAX - 2) + 1;
            let key = serde_json::to_string(&json!([
                content["item"]["itemId"],
                content["unitKind"],
                content["displayNumber"],
                content["seasonNumber"]
            ]))?;
            let entry = json!({"content":content,"position":position,"positionKind":kind,"isCompleted":completed,"updatedAt":now(),"syncVersion":version(&device,next)});
            tx.prepare_cached(
                "INSERT INTO rust_progress_pending(profile_id,content_key,clock,entry)
                 VALUES(?1,?2,?3,?4) ON CONFLICT(profile_id,content_key) DO UPDATE SET
                 clock=excluded.clock,entry=excluded.entry",
            )?
            .execute(params![profile, key, next, serde_json::to_string(&entry)?])?;
            tx.prepare_cached("UPDATE rust_profile_clock SET clock=?2 WHERE profile_id=?1")?
                .execute(params![profile, next])?;
            tx.commit()?;
            return Ok(());
        }
        let mut doc = read_state(&tx, self.1)?;
        let version = next_version(&doc)?;
        let rows = doc["progress"]
            .as_array_mut()
            .ok_or_else(|| invalid("Progreso inválido."))?;
        rows.retain(|r| !same_content(&r["content"], &content));
        rows.push(json!({"content":content,"position":position,"positionKind":kind,"isCompleted":completed,"updatedAt":now(),"syncVersion":version}));
        save_state(&tx, self.1, &doc)?;
        tx.commit()?;
        Ok(())
    }
    pub fn progress(&self, target: &ProgressTarget) -> Result<Option<Value>> {
        let db = self.0.lock().map_err(|_| Error::Lock)?;
        let _tx = db.unchecked_transaction()?;
        let content = target.content(&db)?;
        Ok(read_state(&db, self.1)?["progress"]
            .as_array()
            .into_iter()
            .flatten()
            .find(|r| same_content(&r["content"], &content))
            .cloned())
    }
    /// Latest non-special unit and whether it lies beyond this profile's furthest recorded unit.
    pub fn shelf_units(&self, ids: &[Uuid]) -> Result<HashMap<String, Value>> {
        if ids.len() > 500 {
            return Err(invalid("Demasiados títulos."));
        }
        let db = self.0.lock().map_err(|_| Error::Lock)?;
        let tx = db.unchecked_transaction()?;
        let state = read_state(&tx, self.1)?;
        let redirects = redirect_map(&state);
        let mut progress: HashMap<String, HashSet<(Option<i64>, String, String)>> = HashMap::new();
        for row in state["progress"].as_array().into_iter().flatten() {
            let c = &row["content"];
            let (Some(kind), Some(num)) = (c["unitKind"].as_str(), c["displayNumber"].as_str())
            else {
                continue;
            };
            let identity = &c["item"];
            let key = if let Some(id) = identity["itemId"]
                .as_str()
                .and_then(|v| Uuid::parse_str(v).ok())
            {
                crate::reconcile_catalog::destination(id, &redirects)?.to_string()
            } else {
                format!(
                    "{}:{}",
                    identity["mediaType"].as_str().unwrap_or("").to_lowercase(),
                    normalize(identity["name"].as_str().unwrap_or(""))
                )
            };
            progress.entry(key).or_default().insert((
                c["seasonNumber"].as_i64(),
                kind.to_string(),
                num.to_string(),
            ));
        }
        let mut stmt = tx.prepare("WITH chosen AS (
            SELECT id,media_type,payload FROM rust_catalog WHERE id IN (SELECT value FROM json_each(?1))
        ), units AS (
            SELECT c.id,c.media_type,c.payload->>'name' title,NULL season,u.value unit,u.key idx FROM chosen c,json_each(c.payload,'$.units') u
            UNION ALL SELECT c.id,c.media_type,c.payload->>'name',s.value->>'number',u.value,u.key
                FROM chosen c,json_each(c.payload,'$.seasons') s,json_each(s.value,'$.units') u
        ) SELECT id,media_type,title,season,unit->>'kind',CAST(unit->>'displayNumber' AS TEXT) FROM units
          WHERE coalesce(unit->>'isSpecial',0)=0 AND unit->>'displayNumber' IS NOT NULL
              AND unit->>'kind' IN ('episode','chapter','track','audioChapter','audio_chapter')
          ORDER BY id,coalesce(season,-1),coalesce(unit->>'sortOrder',idx),idx")?;
        let mut rows = stmt.query([serde_json::to_string(ids)?])?;
        let mut result = HashMap::new();
        let mut seen = HashSet::new();
        while let Some(row) = rows.next()? {
            let id: String = row.get(0)?;
            let media: String = row.get(1)?;
            let title: String = row.get(2)?;
            let season: Option<i64> = row.get(3)?;
            let kind: String = row.get(4)?;
            let number: String = row.get(5)?;
            let target = (season, kind.clone(), number.clone());
            let matched = progress.get(&id).is_some_and(|s| s.contains(&target))
                || progress
                    .get(&format!("{}:{}", media.to_lowercase(), normalize(&title)))
                    .is_some_and(|s| s.contains(&target));
            if matched {
                seen.insert(id.clone());
            }
            let later = seen.contains(&id) && !matched;
            result.insert(id, json!({"kind":kind,"number":number,"later":later}));
        }
        Ok(result)
    }
    pub fn item_progress(&self, item_id: Uuid) -> Result<Vec<Value>> {
        let db = self.0.lock().map_err(|_| Error::Lock)?;
        let tx = db.unchecked_transaction()?;
        let Some(item) = Self::summary_from(&tx, item_id)? else {
            return Ok(Vec::new());
        };
        let document = read_state(&tx, self.1)?;
        let redirects = redirect_map(&document);
        let item_media = item.media_type.to_ascii_lowercase();
        let item_name = normalize(&item.name);
        let mut results = Vec::new();
        for row in document["progress"].as_array().into_iter().flatten() {
            let reference = &row["content"]["item"];
            let matches = if let Some(id) = reference["itemId"]
                .as_str()
                .and_then(|raw| Uuid::parse_str(raw).ok())
            {
                crate::reconcile_catalog::destination(id, &redirects)? == item_id
            } else {
                reference["mediaType"]
                    .as_str()
                    .is_some_and(|m| m.eq_ignore_ascii_case(&item_media))
                    && reference["name"]
                        .as_str()
                        .is_some_and(|n| normalize(n) == item_name)
            };
            if matches {
                results.push(row.clone());
            }
        }
        Ok(results)
    }
    pub fn save_progress_batch(
        &self,
        entries: &[(ProgressTarget, f64, String, bool)],
    ) -> Result<()> {
        if entries.is_empty() {
            return Ok(());
        }
        for (_, position, kind, _) in entries {
            if !position.is_finite()
                || *position < 0.0
                || !matches!(kind.as_str(), "seconds" | "page")
            {
                return Err(invalid("Posición inválida."));
            }
        }
        let mut db = self.0.lock().map_err(|_| Error::Lock)?;
        let tx = db.transaction_with_behavior(rusqlite::TransactionBehavior::Immediate)?;
        let mut doc = read_state(&tx, self.1)?;
        let stamp = now();
        for (target, position, kind, completed) in entries {
            let content = target.content(&tx)?;
            let ver = next_version(&doc)?;
            let rows = doc["progress"]
                .as_array_mut()
                .ok_or_else(|| invalid("Progreso inválido."))?;
            rows.retain(|r| !same_content(&r["content"], &content));
            rows.push(json!({
                "content": content,
                "position": position,
                "positionKind": kind,
                "isCompleted": completed,
                "updatedAt": stamp,
                "syncVersion": ver
            }));
        }
        save_state(&tx, self.1, &doc)?;
        tx.commit()?;
        Ok(())
    }
    pub fn continue_reading_or_watching(&self, hide_adult: bool) -> Result<Option<Value>> {
        Ok(self.continue_items(hide_adult, 1)?.into_iter().next())
    }
    pub fn continue_items(&self, hide_adult: bool, limit: usize) -> Result<Vec<Value>> {
        if limit == 0 {
            return Ok(Vec::new());
        }
        let db = self.0.lock().map_err(|_| Error::Lock)?;
        let tx = db.unchecked_transaction()?;
        let document = read_state(&tx, self.1)?;
        let redirects = redirect_map(&document);
        let mut rows: Vec<_> = document["progress"]
            .as_array()
            .into_iter()
            .flatten()
            .collect();
        rows.sort_by_cached_key(|r| {
            std::cmp::Reverse((
                chrono::DateTime::parse_from_rfc3339(r["updatedAt"].as_str().unwrap_or("")).ok(),
                r["syncVersion"]["clock"].as_i64().unwrap_or(0),
            ))
        });
        let mut seen = HashSet::new();
        let mut legacy = None;
        let mut results = Vec::new();
        for row in rows {
            let reference = &row["content"]["item"];
            let id = if let Some(id) = reference["itemId"]
                .as_str()
                .and_then(|id| Uuid::parse_str(id).ok())
            {
                crate::reconcile_catalog::destination(id, &redirects)?
            } else {
                if legacy.is_none() {
                    legacy = Some(Self::export_from(&tx)?);
                }
                let Some(item) = legacy
                    .as_ref()
                    .and_then(|catalog| resolve(catalog, reference))
                else {
                    continue;
                };
                item.id()?
            };
            if !seen.insert(id)
                || row["isCompleted"] == true
                || row["position"].as_f64().unwrap_or(0.) <= 0.
            {
                continue;
            }
            if let Some(item) = Self::item_from(&tx, id)? {
                if !hide_adult || !item.adult() {
                    results.push(json!({"item":item,"progress":row}));
                    if results.len() >= limit {
                        break;
                    }
                }
            }
        }
        Ok(results)
    }

    pub fn backup(&self) -> Result<Value> {
        let mut db = self.0.lock().map_err(|_| Error::Lock)?;
        let needs_identity: bool = db.query_row(
            "SELECT document IS NULL FROM rust_profiles WHERE id=?1",
            [self.1.to_string()],
            |r| r.get(0),
        )?;
        let tx = if needs_identity {
            db.transaction_with_behavior(rusqlite::TransactionBehavior::Immediate)?
        } else {
            db.transaction_with_behavior(rusqlite::TransactionBehavior::Deferred)?
        };
        let still_missing: bool = if needs_identity {
            tx.query_row(
                "SELECT document IS NULL FROM rust_profiles WHERE id=?1",
                [self.1.to_string()],
                |r| r.get(0),
            )?
        } else {
            false
        };
        let mut document = read_state(&tx, self.1)?;
        if still_missing {
            // Device identity is persisted once even before the first favorite/progress edit.
            save_state(&tx, self.1, &document)?;
        }
        let catalog = Self::export_from(&tx)?;
        document["exportedAt"] = json!(now());
        // Deleted catalog entries cannot leave dangling personal state.
        let redirects = redirect_map(&document);
        {
            let titles = Titles::new(&catalog);
            for key in ["progress", "readerPreferences", "favoriteItemStates"] {
                if let Some(rows) = document[key].as_array_mut() {
                    rows.retain(|r| {
                        titles
                            .resolve(
                                &redirects,
                                if key == "progress" {
                                    &r["content"]["item"]
                                } else {
                                    &r["item"]
                                },
                            )
                            .is_some()
                    });
                }
            }
            if let Some(lists) = document["lists"].as_array_mut() {
                for list in lists {
                    if let Some(items) = list["items"].as_array_mut() {
                        items.retain(|r| titles.resolve(&redirects, r).is_some());
                    }
                    if let Some(states) = list["itemStates"].as_array_mut() {
                        states.retain(|r| titles.resolve(&redirects, &r["item"]).is_some());
                    }
                }
            }
        }
        // The catalog was just read as typed items, so it is checked as it is
        // instead of being converted to JSON and parsed back.
        catalog.validate()?;
        document["catalog"] = serde_json::to_value(&catalog)?;
        validate_envelope(&document, MAX_DOCUMENT_BYTES)?;
        validate_personal(&document, &catalog)?;
        if still_missing {
            tx.commit()?;
        }
        Ok(document)
    }
    pub fn restore_backup(&self, document: &Value) -> Result<usize> {
        self.restore_backup_with_limit(document, MAX_DOCUMENT_BYTES)
    }

    pub fn restore_backup_with_limit(&self, document: &Value, max: usize) -> Result<usize> {
        self.restore_document(document, None, max)
    }

    pub fn restore_account_backup(
        &self,
        document: &Value,
        server: &str,
        account: Uuid,
        revision: i64,
    ) -> Result<usize> {
        self.restore_document(
            document,
            Some((server, account, revision)),
            MAX_DOCUMENT_BYTES,
        )
    }

    fn restore_document(
        &self,
        document: &Value,
        origin: Option<(&str, Uuid, i64)>,
        max: usize,
    ) -> Result<usize> {
        let catalog = validate_backup_with_limit(document, max)?;
        let mut db = self.0.lock().map_err(|_| Error::Lock)?;
        let tx = db.transaction_with_behavior(rusqlite::TransactionBehavior::Immediate)?;
        let local = read_state(&tx, self.1)?;
        let shared: bool =
            tx.query_row("SELECT count(*)>1 FROM rust_profiles", [], |r| r.get(0))?;
        if shared {
            let mut additions = catalog.clone();
            additions.items.retain(|item| !item.deleted);
            Self::import_into(&tx, &additions, false)?;
        } else {
            Self::import_into(&tx, &catalog, true)?;
        }
        tx.execute(
            "DELETE FROM rust_favorites WHERE profile_id=?1",
            [self.1.to_string()],
        )?;
        let mut doc = document.clone();
        bind_legacy_targets(&mut doc, &catalog)?;
        // A restore brings the data, not the originating device's identity.
        doc["deviceId"] = local["deviceId"].clone();
        doc["deviceName"] = local["deviceName"].clone();
        doc.as_object_mut()
            .ok_or_else(|| invalid("Datos del respaldo inválidos."))?
            .remove("catalog");
        for key in [
            "progress",
            "readerPreferences",
            "favoriteItemStates",
            "favoriteStates",
            "favoriteContributorKeys",
            "lists",
            "catalogRedirects",
        ] {
            if doc[key].is_null() {
                doc[key] = json!([]);
            }
        }
        let redirects = redirect_map(&doc);
        let titles = Titles::new(&catalog);
        for row in doc["favoriteItemStates"].as_array_mut().into_iter().flatten() {
            if row["isFavorite"] == true {
                if let Some(item) = titles.resolve(&redirects, &row["item"]) {
                    if !item.deleted {
                        let canonical = item.id()?;
                        row["item"]["itemId"] = json!(canonical);
                        tx.execute(
                            "INSERT OR IGNORE INTO rust_favorites(profile_id,id)
                             SELECT ?2,?1 WHERE EXISTS(SELECT 1 FROM rust_catalog WHERE id=?1)",
                            params![canonical.to_string(), self.1.to_string()],
                        )?;
                    }
                }
            }
        }
        save_state(&tx, self.1, &doc)?;
        // A replacement library no longer descends from other accounts' snapshots.
        tx.execute(
            "DELETE FROM rust_backup_revisions WHERE profile_id=?1",
            [self.1.to_string()],
        )?;
        if let Some((server, account, revision)) = origin {
            self.write_backup_revision(&tx, server, account, revision)?;
        }
        tx.commit()?;
        Ok(catalog.items.len())
    }
}

#[cfg(test)]
mod pending_positions {
    use super::*;

    fn library() -> (CatalogStore, ProgressTarget) {
        let store = CatalogStore::in_memory().unwrap();
        let id = Uuid::new_v4();
        let document = CatalogDocument::parse(
            json!({"format":"universal-media-distribution.catalog","version":1,"items":[{
                "syncId":id,"name":"Serie","mediaType":"movie","subtype":"movie.series",
                "genres":["Drama"],
                "units":(1..=40).map(|n| json!({"kind":"episode","displayNumber":n.to_string()})).collect::<Vec<_>>()
            }]})
            .to_string()
            .as_bytes(),
        )
        .unwrap();
        store.import(&document, true).unwrap();
        let target = ProgressTarget {
            item_id: id,
            unit_kind: Some("episode".into()),
            display_number: Some("1".into()),
            season_number: None,
        };
        (store, target)
    }
    #[test]
    fn shelf_badge_uses_furthest_progress_and_ignores_rewatches() {
        let (store, mut target) = library();
        let ids = [target.item_id];
        let check = |later| {
            let badges = store.shelf_units(&ids).unwrap();
            assert_eq!(
                badges[&ids[0].to_string()],
                json!({"kind":"episode","number":"40","later":later})
            );
        };
        check(false);
        store.save_progress(&target, 6.0, "seconds", false).unwrap();
        check(true);
        target.display_number = Some("40".into());
        store.save_progress(&target, 6.0, "seconds", false).unwrap();
        target.display_number = Some("3".into());
        store.save_progress(&target, 6.0, "seconds", false).unwrap();
        check(false);
    }
    fn count(store: &CatalogStore, sql: &str) -> i64 {
        store
            .0
            .lock()
            .unwrap()
            .query_row(sql, [], |r| r.get(0))
            .unwrap()
    }
    fn document(store: &CatalogStore) -> String {
        store
            .0
            .lock()
            .unwrap()
            .query_row("SELECT document FROM rust_profiles", [], |r| r.get(0))
            .unwrap()
    }

    #[test]
    fn a_repeated_position_is_queued_read_back_and_folded_by_the_next_full_save() {
        let (store, target) = library();
        // The first save creates the document and its clock.
        store
            .save_progress(&target, 10.0, "seconds", false)
            .unwrap();
        assert_eq!(
            count(&store, "SELECT count(*) FROM rust_progress_pending"),
            0
        );
        let first = document(&store);
        let first_clock = store.progress(&target).unwrap().unwrap()["syncVersion"]["clock"]
            .as_i64()
            .unwrap();

        store
            .save_progress(&target, 20.0, "seconds", false)
            .unwrap();
        store.save_progress(&target, 30.0, "seconds", true).unwrap();
        // Later saves leave the document alone and keep one row per episode.
        assert_eq!(document(&store), first);
        assert_eq!(
            count(&store, "SELECT count(*) FROM rust_progress_pending"),
            1
        );
        let latest = store.progress(&target).unwrap().unwrap();
        assert_eq!(latest["position"], 30.0);
        assert_eq!(latest["isCompleted"], true);
        assert_eq!(latest["syncVersion"]["clock"], first_clock + 2);

        // A backup sees the queued position exactly once and stays valid.
        let backup = store.backup().unwrap();
        let rows = backup["progress"].as_array().unwrap();
        assert_eq!(rows.len(), 1);
        assert_eq!(rows[0]["position"], 30.0);
        validate_backup(&backup).unwrap();

        // Any other edit writes the document and empties the queue.
        store.set_favorite(target.item_id, true).unwrap();
        assert_eq!(
            count(&store, "SELECT count(*) FROM rust_progress_pending"),
            0
        );
        let folded: Value = serde_json::from_str(&document(&store)).unwrap();
        assert_eq!(folded["progress"][0]["position"], 30.0);
        assert_eq!(
            folded["favoriteItemStates"][0]["syncVersion"]["clock"],
            first_clock + 3
        );
        assert_eq!(store.progress(&target).unwrap().unwrap()["position"], 30.0);
    }

    #[test]
    fn the_queue_is_bounded_and_every_position_survives() {
        let (store, mut target) = library();
        for episode in 1..=40 {
            target.display_number = Some(episode.to_string());
            store
                .save_progress(&target, f64::from(episode), "seconds", false)
                .unwrap();
            assert!(
                count(&store, "SELECT count(*) FROM rust_progress_pending")
                    <= MAX_PENDING_POSITIONS
            );
        }
        let backup = store.backup().unwrap();
        let rows = backup["progress"].as_array().unwrap();
        assert_eq!(rows.len(), 40);
        let mut clocks: Vec<_> = rows
            .iter()
            .map(|r| r["syncVersion"]["clock"].as_i64().unwrap())
            .collect();
        clocks.sort_unstable();
        clocks.dedup();
        assert_eq!(clocks.len(), 40, "every position keeps its own clock");
    }

    #[test]
    fn a_document_changed_behind_the_clock_falls_back_to_the_full_save() {
        let (store, target) = library();
        store
            .save_progress(&target, 10.0, "seconds", false)
            .unwrap();
        // Another build rewrote the document with a higher clock and no clock row update.
        {
            let db = store.0.lock().unwrap();
            let mut doc: Value = serde_json::from_str(
                &db.query_row("SELECT document FROM rust_profiles", [], |r| {
                    r.get::<_, String>(0)
                })
                .unwrap(),
            )
            .unwrap();
            doc["progress"][0]["syncVersion"]["clock"] = json!(900);
            doc["progress"][0]["position"] = json!(55.5);
            db.execute(
                "UPDATE rust_profiles SET document=?1",
                [serde_json::to_string(&doc).unwrap()],
            )
            .unwrap();
        }
        store
            .save_progress(&target, 60.0, "seconds", false)
            .unwrap();
        let latest = store.progress(&target).unwrap().unwrap();
        assert_eq!(latest["position"], 60.0);
        assert_eq!(latest["syncVersion"]["clock"], 901);
    }

    #[test]
    fn summaries_follow_every_catalog_write() {
        let (store, target) = library();
        let summaries = "SELECT count(*) FROM rust_catalog_summary";
        assert_eq!(count(&store, summaries), 1);
        assert_eq!(
            count(
                &store,
                "SELECT count(*) FROM rust_catalog_summary WHERE summary LIKE '%\"units\"%'"
            ),
            0
        );
        let mut query = CatalogQuery::default();
        query.filters.genre = Some("drama".into());
        assert_eq!(store.page(&query).unwrap().total, 1);
        let units = |query: &CatalogQuery| {
            store.page(query).unwrap().page.document.items[0]
                .metadata
                .get("units")
                .and_then(Value::as_array)
                .map_or(0, Vec::len)
        };
        assert_eq!(units(&query), 40);
        query.filters.summary = true;
        assert_eq!(units(&query), 0);

        // A tombstone removes the title and its summary together.
        let mut gone = store.export().unwrap();
        gone.items[0].deleted = true;
        store.import(&gone, false).unwrap();
        assert_eq!(count(&store, summaries), 0);
        assert_eq!(store.page(&CatalogQuery::default()).unwrap().total, 0);
        assert!(store.progress(&target).is_err());
    }

    #[test]
    fn dotnet_favorite_sync_handles_legacy_identities_tombstones_and_table_repair() {
        let store = CatalogStore::in_memory().unwrap();
        let manga_id = Uuid::new_v4();
        let novel_item = CatalogItem {
            sync_id: None,
            media_type: "book".into(),
            subtype: "book.novel".into(),
            name: "Novela".into(),
            deleted: false,
            metadata: std::collections::BTreeMap::new(),
        };
        let novel_canonical = novel_item.id().unwrap();
        // Simulate a .NET server snapshot with:
        // 1) a manga favorite whose identity omits itemId and uses short subtype "manga"
        // 2) a novel favorite whose itemId uses the legacy non-subtyped base ID
        // 3) an unfavorited tombstone for a deleted title with itemId = null
        let base_novel = crate::CatalogItem {
            sync_id: None,
            media_type: "book".into(),
            subtype: "book.manga".into(),
            name: "Novela".into(),
            deleted: false,
            metadata: std::collections::BTreeMap::new(),
        }
        .id()
        .unwrap();
        let version = |clock: i64| {
            json!({
                "clock": clock,
                "deviceId": "dotnet-device",
                "operationId": Uuid::new_v4(),
                "modifiedAt": "2026-01-01T00:00:00.000Z"
            })
        };
        let remote = json!({
            "format": USER_FORMAT,
            "version": 4,
            "exportedAt": "2026-01-01T00:00:00.000Z",
            "deviceId": "dotnet-device",
            "deviceName": "UMD .NET",
            "catalog": {
                "format": CATALOG_FORMAT,
                "version": 1,
                "exportedAt": "2026-01-01T00:00:00.000Z",
                "items": [
                    { "syncId": manga_id, "mediaType": "book", "subtype": "book.manga", "name": "Manga Uno" },
                    { "syncId": base_novel, "mediaType": "book", "subtype": "book.novel", "name": "Novela" }
                ]
            },
            "progress": [],
            "readerPreferences": [],
            "favoriteContributorKeys": [],
            "favoriteStates": [],
            "favoriteItemStates": [
                {
                    "item": { "mediaType": "book", "subtype": "manga", "itemId": null, "name": "Manga Uno" },
                    "isFavorite": true,
                    "syncVersion": version(1)
                },
                {
                    "item": { "mediaType": "book", "subtype": "book.novel", "itemId": base_novel, "name": "Novela" },
                    "isFavorite": true,
                    "syncVersion": version(2)
                },
                {
                    "item": { "mediaType": "movie", "subtype": null, "itemId": null, "name": "Borrado" },
                    "isFavorite": false,
                    "syncVersion": version(3)
                }
            ],
            "lists": [],
            "catalogRedirects": []
        });
        let (changed, _) = store.reconcile_profile(Some(&remote)).unwrap();
        assert!(changed);
        let page = store
            .page(&CatalogQuery {
                favorites_only: true,
                ..Default::default()
            })
            .unwrap();
        assert_eq!(page.total, 2);
        assert!(page.favorite_ids.contains(&manga_id));
        assert!(page.favorite_ids.contains(&novel_canonical));

        // Even if rust_favorites is cleared externally, reconciling repairs it.
        store
            .0
            .lock()
            .unwrap()
            .execute("DELETE FROM rust_favorites", [])
            .unwrap();
        let (repaired, _) = store.reconcile_profile(Some(&remote)).unwrap();
        assert!(repaired);
        let after = store
            .page(&CatalogQuery {
                favorites_only: true,
                ..Default::default()
            })
            .unwrap();
        assert_eq!(after.total, 2);
    }
}
