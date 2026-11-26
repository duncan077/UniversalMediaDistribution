use crate::{CatalogDocument, CatalogItem, CatalogStore, Error, Result};
use rusqlite::{params, OptionalExtension, TransactionBehavior};
use serde_json::{Map, Value};
use std::collections::{HashMap, HashSet};
use uuid::Uuid;

// Only portable metadata crosses the crawler boundary. In particular, preserve existing
// local sources during a refresh without importing sources from any provider.
fn fields(object: &Map<String, Value>, names: &str) -> Map<String, Value> {
    names
        .split_whitespace()
        .filter_map(|name| {
            let v = object.get(name)?;
            (v.is_number() || v.is_boolean() || v.as_str().is_some_and(|s| !s.trim().is_empty()))
                .then(|| (name.into(), v.clone()))
        })
        .collect()
}
fn safe_ref(raw: &str, allow_relative: bool) -> bool {
    let s = raw.trim();
    if s.is_empty() || s.len() > 2048 || s.chars().any(char::is_control) {
        return false;
    }
    if let Ok(url) = url::Url::parse(s) {
        return crate::public_url::web_url(&url);
    }
    if !allow_relative || s.starts_with('/') || s.starts_with('\\') || s.contains('\\') {
        return false;
    }
    let first_segment = s.split('/').next().unwrap_or(s);
    if first_segment.contains(':') {
        return false;
    }
    !s.split('/').any(|seg| seg == "..")
}

fn copy_ref(
    obj: &Map<String, Value>,
    out: &mut Map<String, Value>,
    key: &str,
    allow_relative: bool,
) {
    if let Some(raw) = obj.get(key).and_then(Value::as_str) {
        if safe_ref(raw, allow_relative) {
            out.insert(key.into(), Value::String(raw.trim().into()));
        }
    }
}

fn children(object: &Map<String, Value>, target: &mut Map<String, Value>, key: &str, kind: &str) {
    if let Some(arr) = object.get(key).and_then(Value::as_array) {
        let clean: Vec<_> = arr
            .iter()
            .filter_map(Value::as_object)
            .filter_map(|o| {
                let projected = project(o, kind);
                (!projected.is_empty()).then_some(Value::Object(projected))
            })
            .collect();
        if !clean.is_empty() {
            target.insert(key.into(), clean.into());
        }
    }
}
fn project(obj: &Map<String, Value>, kind: &str) -> Map<String, Value> {
    let names=match kind{
        "item"=>"originalName sortName description releaseDate releaseYear endDate publicationStatus originalLanguage countryCode contentRating runtimeSeconds pageCount",
        "unit"=>"kind displayNumber sortOrder name durationSeconds pageCount releaseDate isSpecial",
        "season"=>"number sortOrder displayName releaseDate",
        "alias"=>"name language aliasType", "contributor"=>"name kind",
        "credit"=>"role characterName sortOrder",_=>""
    };
    let mut out = fields(obj, names);
    match kind {
        "item" => {
            copy_ref(obj, &mut out, "thumbnailRef", true);
            copy_ref(obj, &mut out, "backdropRef", true);
        }
        "unit" | "season" => {
            copy_ref(obj, &mut out, "thumbnailRef", true);
        }
        "contributor" => {
            copy_ref(obj, &mut out, "thumbnailRef", true);
            copy_ref(obj, &mut out, "url", false);
        }
        _ => {}
    }
    if kind == "item" {
        for key in ["genres", "categories"] {
            if let Some(a) = obj.get(key).and_then(Value::as_array) {
                let vals: Vec<_> = a.iter().filter_map(Value::as_str).collect();
                if !vals.is_empty() {
                    out.insert(key.into(), serde_json::json!(vals));
                }
            }
        }
        for (k, t) in [
            ("alternativeNames", "alias"),
            ("aliases", "alias"),
            ("contributors", "contributor"),
            ("seasons", "season"),
        ] {
            children(obj, &mut out, k, t);
        }
    }
    if matches!(kind, "item" | "season") {
        children(obj, &mut out, "units", "unit");
    }
    if kind == "contributor" {
        children(obj, &mut out, "credits", "credit");
    }
    out
}
pub fn catalog_metadata_only(mut item: CatalogItem) -> CatalogItem {
    let object = item.metadata.into_iter().collect();
    let mut metadata = project(&object, "item");
    let mut seen = HashSet::new();
    let mut aliases = Vec::new();
    for key in ["alternativeNames", "aliases"] {
        if let Some(Value::Array(names)) = metadata.remove(key) {
            for name in names {
                if seen.insert(entry_key("alternativeNames", &name)) {
                    aliases.push(name);
                }
            }
        }
    }
    if !aliases.is_empty() {
        metadata.insert("alternativeNames".into(), Value::Array(aliases));
    }
    item.metadata = metadata.into_iter().collect();
    item
}

pub fn provider_reference(value: &str) -> bool {
    !value.is_empty() && value.len() <= 1024
        && value.trim_end_matches('/').split('/').all(|part| {
            !part.is_empty() && !matches!(part, "." | "..")
                && part.bytes().all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'-' | b'_' | b'.'))
        })
}
// Keep the array order from the existing catalog. Look up units by their portable
// identity so a title with thousands of chapters can refresh in linear time.
fn entry_key(key: &str, value: &Value) -> Vec<String> {
    let parts: &[&str] = match key {
        "units" => &["kind", "displayNumber"],
        "seasons" => &["number"],
        "contributors" => &["kind", "name"],
        "credits" => &["role", "characterName"],
        "alternativeNames" | "aliases" => &["name", "language", "aliasType"],
        _ => return vec![value.to_string()],
    };
    parts
        .iter()
        .map(|k| match &value[*k] {
            Value::String(s) => s
                .split_whitespace()
                .collect::<Vec<_>>()
                .join(" ")
                .to_lowercase(),
            Value::Null if *k == "aliasType" => "other".into(),
            Value::Null => String::new(),
            v => v.to_string(),
        })
        .collect()
}
pub(crate) fn overlay(key: &str, old: &mut Value, new: Value) {
    match (old, new) {
        (Value::Object(a), Value::Object(b)) => {
            for (k, v) in b {
                overlay(&k, a.entry(k.clone()).or_insert(Value::Null), v);
            }
        }
        (Value::Array(a), Value::Array(b)) => {
            let mut by_key: HashMap<_, _> = a
                .iter()
                .enumerate()
                .map(|(index, value)| (entry_key(key, value), index))
                .collect();
            for value in b {
                let identity = entry_key(key, &value);
                if let Some(&index) = by_key.get(&identity) {
                    if value.is_object() {
                        overlay("", &mut a[index], value);
                    }
                } else {
                    by_key.insert(identity, a.len());
                    a.push(value);
                }
            }
        }
        (a, b) => *a = b,
    }
}
fn without_artwork(value: &mut Value) {
    match value {
        Value::Object(fields) => {
            fields.remove("thumbnailRef");
            fields.remove("backdropRef");
            fields.values_mut().for_each(without_artwork);
        }
        Value::Array(rows) => rows.iter_mut().for_each(without_artwork),
        _ => {}
    }
}

/// Links between a provider's own identifiers and catalog titles. Created with
/// the library so adding a title does not run schema statements each time.
pub(crate) fn initialize(db: &rusqlite::Connection) -> Result<()> {
    db.execute_batch("CREATE TABLE IF NOT EXISTS rust_crawl_bindings(provider TEXT NOT NULL, external TEXT NOT NULL, item_id TEXT NOT NULL REFERENCES rust_catalog(id) ON DELETE CASCADE, PRIMARY KEY(provider,external));")?;
    Ok(())
}
impl CatalogStore {
    pub fn import_local_sources(&self, doc: &CatalogDocument) -> Result<usize> {
        doc.validate()?;
        let mut db = self.0.lock().map_err(|_| Error::Lock)?;
        let tx = db.transaction_with_behavior(TransactionBehavior::Immediate)?;
        let mut changed = vec![];
        for incoming in &doc.items {
            if incoming.deleted || incoming.sync_id.is_some() {
                return Err(Error::Invalid(
                    "El manifiesto contiene una identidad externa.".into(),
                ));
            }
            let mut statement =
                tx.prepare("SELECT payload FROM rust_catalog WHERE name_key=?1 LIMIT 2")?;
            let rows = statement
                .query_map([incoming.key()], |r| r.get::<_, String>(0))?
                .collect::<rusqlite::Result<Vec<_>>>()?;
            if rows.len() > 1 {
                return Err(Error::Invalid(format!(
                    "Hay varias ediciones de {} en la biblioteca.",
                    incoming.name
                )));
            }
            if let Some(row) = rows.first() {
                let mut item: CatalogItem = serde_json::from_str(row)?;
                let old = item.clone();
                for (k, v) in &incoming.metadata {
                    overlay(
                        k,
                        item.metadata.entry(k.clone()).or_insert(Value::Null),
                        v.clone(),
                    );
                }
                if item != old {
                    changed.push(item);
                }
            } else {
                changed.push(incoming.clone());
            }
        }
        if !changed.is_empty() {
            Self::import_into(&tx, &CatalogDocument::new(changed), false)?;
        }
        tx.commit()?;
        Ok(doc.items.len())
    }
    pub fn update_title_metadata(
        &self,
        id: Uuid,
        expected_key: &str,
        patch: Map<String, Value>,
    ) -> Result<CatalogItem> {
        let mut db = self.0.lock().map_err(|_| Error::Lock)?;
        let tx = db.transaction_with_behavior(TransactionBehavior::Immediate)?;
        let doc: String = tx.query_row(
            "SELECT payload FROM rust_catalog WHERE id=?1",
            [id.to_string()],
            |r| r.get(0),
        )?;
        let mut item: CatalogItem = serde_json::from_str(&doc)?;
        if item.key() != expected_key {
            return Err(Error::Invalid(
                "La ficha cambió durante la consulta.".into(),
            ));
        }
        let previous = item.metadata.clone();
        for (k, v) in patch {
            if !matches!(
                k.as_str(),
                "description"
                    | "originalName"
                    | "releaseDate"
                    | "releaseYear"
                    | "alternativeNames"
                    | "genres"
                    | "thumbnailRef"
            ) {
                return Err(Error::Invalid("Campo de metadatos desconocido.".into()));
            }
            if k == "releaseYear"
                && !v.is_null()
                && !v.as_i64().is_some_and(|y| (1800..=2200).contains(&y))
            {
                return Err(Error::Invalid("Año de lanzamiento inválido.".into()));
            }
            if k == "thumbnailRef"
                && !v.is_null()
                && !v
                    .as_str()
                    .and_then(|s| url::Url::parse(s).ok())
                    .is_some_and(|u| crate::public_url::web_url(&u))
            {
                return Err(Error::Invalid("Referencia de portada inválida.".into()));
            }
            overlay(&k, item.metadata.entry(k.clone()).or_insert(Value::Null), v);
        }
        if item.metadata != previous {
            let profile = crate::user_state::read_state(&tx, self.1)?;
            let clock = item
                .metadata
                .get("syncVersion")
                .and_then(|v| v["clock"].as_i64())
                .unwrap_or(0)
                .saturating_add(1)
                .max(chrono::Utc::now().timestamp_millis())
                .min(i64::MAX - 1);
            item.metadata.insert("syncVersion".into(),serde_json::json!({"clock":clock,"deviceId":profile["deviceId"],"operationId":Uuid::new_v4(),"modifiedAt":crate::now()}));
            crate::user_state::save_state(&tx, self.1, &profile)?;
            item.validate()?;
            Self::import_into(&tx, &CatalogDocument::new(vec![item.clone()]), false)?;
        }
        tx.commit()?;
        Ok(item)
    }
    pub fn crawl_summary(&self) -> Result<(i64, i64, String)> {
        let db = self.0.lock().map_err(|_| Error::Lock)?;
        Ok(db.query_row("SELECT (SELECT COUNT(*) FROM rust_catalog),revision,updated_at FROM rust_state WHERE singleton=1",[],|r|Ok((r.get(0)?,r.get(1)?,r.get(2)?)))?)
    }
    pub fn merge_scraped(
        &self,
        provider: &str,
        external: &str,
        incoming: CatalogItem,
    ) -> Result<bool> {
        if incoming.deleted {
            return Ok(false);
        }
        // The public crawler publishes descriptions, not artwork: thumbnail and
        // backdrop references are dropped at every level before the merge.
        let mut incoming = incoming;
        incoming.metadata.remove("thumbnailRef");
        incoming.metadata.remove("backdropRef");
        for value in incoming.metadata.values_mut() {
            without_artwork(value);
        }
        self.store_provider_title(provider, external, incoming, false, None)
            .map(|(_, changed)| changed)
    }
    pub fn add_provider_title(
        &self,
        provider: &str,
        external: &str,
        incoming: CatalogItem,
    ) -> Result<CatalogItem> {
        if incoming.deleted {
            return Err(Error::Invalid("Título eliminado".into()));
        }
        self.store_provider_title(provider, external, incoming, true, None)
            .map(|(item, _)| item)
    }
    pub fn merge_provider_update(
        &self,
        provider: &str,
        external: &str,
        item: CatalogItem,
    ) -> Result<bool> {
        if item.deleted {
            return Ok(false);
        }
        self.store_provider_title(provider, external, item, true, None)
            .map(|(_, changed)| changed)
    }
    pub fn link_provider_edition(
        &self,
        id: Uuid,
        key: &str,
        provider: &str,
        external: &str,
        item: CatalogItem,
    ) -> Result<()> {
        self.store_provider_title(provider, external, item, true, Some((id, key)))
            .map(|_| ())
    }
    pub fn item_bindings(&self, id: Uuid) -> Result<Vec<(String, String)>> {
        let db = self.0.lock().map_err(|_| Error::Lock)?;
        let mut stmt = db.prepare(
            "SELECT provider, external FROM rust_crawl_bindings WHERE item_id=?1 ORDER BY provider",
        )?;
        let rows = stmt
            .query_map([id.to_string()], |r| {
                Ok((r.get::<_, String>(0)?, r.get::<_, String>(1)?))
            })?
            .collect::<rusqlite::Result<Vec<_>>>()?;
        Ok(rows)
    }
    /// Merge updated metadata, chapters, seasons, and artwork from a connected server
    /// into a local catalog entry without discarding local sources, provider links, or
    /// existing artwork when the server response omits them.
    pub fn merge_remote_item(
        &self,
        id: Uuid,
        incoming: CatalogItem,
    ) -> Result<(CatalogItem, bool)> {
        self.merge_remote_item_for(id, incoming, None)
    }

    pub fn merge_remote_item_for(
        &self,
        remote_id: Uuid,
        incoming: CatalogItem,
        expected_local_id: Option<Uuid>,
    ) -> Result<(CatalogItem, bool)> {
        if incoming.deleted {
            return Err(Error::Invalid("Título eliminado".into()));
        }
        let identity = CatalogItem {
            sync_id: incoming.sync_id,
            media_type: incoming.media_type.clone(),
            subtype: incoming.subtype.clone(),
            name: incoming.name.clone(),
            deleted: false,
            metadata: ["releaseYear", "releaseDate", "onlineProviders"]
                .into_iter()
                .filter_map(|key| {
                    incoming.metadata.get(key).map(|value| (key.to_owned(), value.clone()))
                })
                .collect(),
        };
        let server_art: Vec<(String, Value)> = ["thumbnailRef", "backdropRef"]
            .into_iter()
            .filter_map(|key| {
                let raw = incoming.metadata.get(key)?.as_str()?.trim();
                if raw.is_empty() || raw.len() > 2048 || raw.chars().any(char::is_control) {
                    return None;
                }
                let allowed = if let Ok(url) = url::Url::parse(raw) {
                    matches!(url.scheme(), "http" | "https")
                        && url.username().is_empty()
                        && url.password().is_none()
                        && url.host_str().is_some()
                } else {
                    !raw.starts_with("//")
                        && !raw.contains('\\')
                        && !raw.split('/').any(|seg| seg == "..")
                        && !raw
                            .trim_start_matches('/')
                            .split('/')
                            .next()
                            .unwrap_or("")
                            .contains(':')
                };
                allowed.then(|| (key.to_owned(), Value::String(raw.to_owned())))
            })
            .collect();
        let provider_links = incoming.metadata.get("onlineProviders").and_then(Value::as_object)
            .into_iter().flatten()
            .filter(|(site, external)| site.len() <= 64 && provider_reference(site) && !site.contains('/')
                && external.as_str().is_some_and(provider_reference))
            .map(|(site, external)| (site.clone(), external.clone()))
            .collect::<Map<String, Value>>();
        let mut clean = catalog_metadata_only(incoming);
        for (key, value) in server_art {
            clean.metadata.entry(key).or_insert(value);
        }
        clean.sync_id = Some(remote_id);
        clean.validate()?;
        let mut db = self.0.lock().map_err(|_| Error::Lock)?;
        let tx = db.transaction_with_behavior(TransactionBehavior::Immediate)?;
        let id = Self::matching_summary_from(&tx, remote_id, &identity)?
            .and_then(|item| item.sync_id)
            .unwrap_or(remote_id);
        if expected_local_id.is_some_and(|expected| expected != id) {
            return Err(Error::Invalid(
                "Cambió la ficha de la biblioteca. Volvé a abrir el título.".into(),
            ));
        }
        clean.sync_id = Some(id);
        let previous = Self::item_from(&tx, id)?;
        let mut merged = if let Some(old) = &previous {
            let mut item = old.clone();
            if item.name.trim().is_empty() && !clean.name.trim().is_empty() {
                item.name = clean.name;
            }
            let existing_known_status = item
                .metadata
                .get("publicationStatus")
                .and_then(Value::as_str)
                .is_some_and(|s| !s.trim().is_empty() && !s.eq_ignore_ascii_case("unknown"));
            for (k, v) in clean.metadata {
                if k == "publicationStatus"
                    && existing_known_status
                    && v.as_str()
                        .is_some_and(|s| s.trim().is_empty() || s.eq_ignore_ascii_case("unknown"))
                {
                    continue;
                }
                if matches!(k.as_str(), "thumbnailRef" | "backdropRef")
                    && v.as_str().is_none_or(|s| s.trim().is_empty())
                {
                    continue;
                }
                overlay(&k, item.metadata.entry(k.clone()).or_insert(Value::Null), v);
            }
            item
        } else {
            clean
        };
        if !provider_links.is_empty() {
            let links = merged.metadata.entry("onlineProviders".into()).or_insert_with(|| serde_json::json!({}));
            if let Some(links) = links.as_object_mut() {
                for (site, external) in provider_links {
                    links.entry(site).or_insert(external);
                }
            }
        }
        let changed = previous.as_ref() != Some(&merged);
        if changed {
            Self::import_into(&tx, &CatalogDocument::new(vec![merged.clone()]), false)?;
        }
        tx.commit()?;
        Ok((merged, changed))
    }
    fn store_provider_title(
        &self,
        provider: &str,
        external: &str,
        incoming: CatalogItem,
        linked: bool,
        target: Option<(Uuid, &str)>,
    ) -> Result<(CatalogItem, bool)> {
        let mut item = catalog_metadata_only(incoming);
        if linked {
            item.metadata.insert(
                "onlineProviders".into(),
                serde_json::json!({(provider):external}),
            );
        }
        item.validate()?;
        let mut db = self.0.lock().map_err(|_| Error::Lock)?;
        let tx = db.transaction_with_behavior(TransactionBehavior::Immediate)?;
        let bound: Option<String> = tx
            .query_row(
                "SELECT item_id FROM rust_crawl_bindings WHERE provider=? AND external=?",
                params![provider, external],
                |r| r.get(0),
            )
            .optional()?;
        let year = |i: &CatalogItem| {
            i.metadata
                .get("releaseYear")
                .and_then(Value::as_i64)
                .or_else(|| {
                    i.metadata
                        .get("releaseDate")?
                        .as_str()?
                        .get(..4)?
                        .parse()
                        .ok()
                })
        };
        let key = item.key();
        let same_kind = |other: &CatalogItem| {
            let other_key = other.key();
            key.splitn(3, ':')
                .take(2)
                .eq(other_key.splitn(3, ':').take(2))
        };
        let release_year = year(&item);
        let compatible = |other: &CatalogItem| {
            same_kind(other) && year(other).zip(release_year).is_none_or(|(a, b)| a == b)
        };
        let mut known = bound.as_deref().and_then(|s| Uuid::parse_str(s).ok());
        let mut displaced = None;
        if target.is_none() && item.media_type.eq_ignore_ascii_case("book") {
            if let Some(id) = known {
                if let Some(mut old) = Self::item_from(&tx, id)? {
                    if !same_kind(&old) {
                        if let Some(links) = old
                            .metadata
                            .get_mut("onlineProviders")
                            .and_then(Value::as_object_mut)
                        {
                            if links.get(provider).and_then(Value::as_str) == Some(external) {
                                links.remove(provider);
                                Self::import_into(&tx, &CatalogDocument::new(vec![old]), false)?;
                            }
                        }
                        displaced = Some(id);
                        known = None;
                    }
                }
            }
        }
        if let Some((id, key)) = target {
            let local = Self::summary_from(&tx, id)?
                .ok_or_else(|| Error::Invalid("La edición local ya no existe.".into()))?;
            if local.key() != key || !compatible(&local) || known.is_some_and(|found| found != id) {
                return Err(Error::Invalid(
                    "Esta edición corresponde a otra ficha.".into(),
                ));
            }
            item.name = local.name;
            item.subtype = local.subtype;
            known = Some(id);
        }
        if known.is_none() && item.sync_id.is_some() {
            let requested = item.id()?;
            if Self::summary_from(&tx, requested)?
                .as_ref()
                .is_none_or(&compatible)
            {
                known = Some(requested);
            }
        }
        if known.is_none() {
            let mut stmt = tx.prepare("SELECT s.summary FROM rust_catalog c JOIN rust_catalog_summary s ON s.id=c.id WHERE c.name_key=?")?;
            let mut matches = Vec::new();
            for row in stmt.query_map([&key], |r| r.get::<_, String>(0))? {
                let other: CatalogItem = serde_json::from_str(&row?)?;
                if year(&other).zip(year(&item)).is_none_or(|(a, b)| a == b) {
                    matches.push(other.sync_id);
                }
            }
            if matches.len() == 1 {
                known = matches[0];
            }
        }
        let id = known.unwrap_or_else(|| {
            let namespace = Uuid::NAMESPACE_URL;
            let external_key = format!("umd:crawler:{provider}:{external}");
            let id = Uuid::new_v5(&namespace, external_key.as_bytes());
            if displaced == Some(id) {
                let subtype = crate::user_state::canonical_subtype("book", &item.subtype);
                Uuid::new_v5(
                    &namespace,
                    format!("{external_key}:book.{subtype}").as_bytes(),
                )
            } else {
                id
            }
        });
        item.sync_id = Some(id);
        let previous = Self::item_from(&tx, id)?;
        if let Some(old) = &previous {
            if !old.name.trim().is_empty() {
                item.name = old.name.clone();
            }
            let mut metadata = old.metadata.clone();
            let existing_known_status = old
                .metadata
                .get("publicationStatus")
                .and_then(Value::as_str)
                .is_some_and(|s| !s.trim().is_empty() && !s.eq_ignore_ascii_case("unknown"));
            for (k, v) in item.metadata {
                if k == "publicationStatus"
                    && existing_known_status
                    && v.as_str()
                        .is_some_and(|s| s.trim().is_empty() || s.eq_ignore_ascii_case("unknown"))
                {
                    continue;
                }
                overlay(&k, metadata.entry(k.clone()).or_insert(Value::Null), v);
            }
            item.metadata = metadata;
        }
        let changed = previous.as_ref() != Some(&item);
        if changed {
            Self::import_into(&tx, &CatalogDocument::new(vec![item.clone()]), false)?;
        }
        tx.execute("INSERT INTO rust_crawl_bindings VALUES(?1,?2,?3) ON CONFLICT(provider,external) DO UPDATE SET item_id=excluded.item_id",params![provider,external,id.to_string()])?;
        tx.commit()?;
        Ok((item, changed))
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn browsing_equivalent_titles_reuses_local_identity_and_keeps_other_editions() {
        let store = CatalogStore::in_memory().unwrap();
        let manga = Uuid::from_u128(2);
        let novel = Uuid::from_u128(1);
        let make = |id, name, subtype, year, chapter| {
            serde_json::from_value::<CatalogItem>(json!({
                "syncId":id, "name":name, "mediaType":"book", "subtype":subtype, "releaseYear":year,
                "units":[{"kind":"chapter","displayNumber":chapter}]
            })).unwrap()
        };
        store.import(&CatalogDocument::new(vec![
            make(manga, "La luna", "manga", 2012, "1"),
            make(novel, "La luna", "novel", 2012, "1"),
        ]), false).unwrap();
        store.set_favorite(manga, true).unwrap();
        let remapped = make(Uuid::from_u128(3), "LA   LUNA", "book.manga", 2012, "2");
        let remake = make(Uuid::from_u128(4), "La luna", "manga", 2024, "1");
        let comic = make(Uuid::from_u128(5), "La luna", "comic", 2012, "1");
        let matched = store.matching_catalog_summaries(&[remapped.clone(),remake.clone(),comic]).unwrap();
        assert_eq!(matched.len(), 1);
        assert_eq!(matched[&remapped.id().unwrap()].sync_id, Some(manga));
        assert_eq!(store.page(&Default::default()).unwrap().total, 2);
        assert!(store.merge_remote_item_for(remake.id().unwrap(), remake, Some(manga)).is_err());

        let remote_id = remapped.id().unwrap();
        let (merged, _) = store.merge_remote_item_for(remote_id, remapped, Some(manga)).unwrap();
        assert_eq!(merged.sync_id, Some(manga));
        assert_eq!(merged.metadata["units"].as_array().unwrap().len(), 2);
        let page = store.page(&Default::default()).unwrap();
        assert_eq!(page.total, 2);
        assert_eq!(page.favorite_ids, vec![manga]);
        assert!(store.item(remote_id).unwrap().is_none());
    }

    #[test]
    fn a_corrected_novel_binding_does_not_replace_manga_or_its_favorite() {
        let store = CatalogStore::in_memory().unwrap();
        let work = |subtype, number| {
            serde_json::from_value(json!({
                "name":"Obra","mediaType":"book","subtype":subtype,
                "units":[{"kind":"chapter","displayNumber":number}]
            }))
            .unwrap()
        };
        let legacy = store
            .add_provider_title("zonatmo", "novel/191/obra", work("manga", "97"))
            .unwrap();
        let manga_id = legacy.id().unwrap();
        store.set_favorite(manga_id, true).unwrap();
        let corrected = store
            .add_provider_title("zonatmo", "novel/191/obra", work("novel", "185"))
            .unwrap();
        assert_ne!(manga_id, corrected.id().unwrap());
        let manga = store.item(manga_id).unwrap().unwrap().items.remove(0);
        assert_eq!(manga.subtype, "manga");
        assert_eq!(manga.metadata["units"][0]["displayNumber"], "97");
        assert!(manga.metadata["onlineProviders"]["zonatmo"].is_null());
        assert_eq!(
            store
                .page(&crate::CatalogQuery::default())
                .unwrap()
                .favorite_ids,
            vec![manga_id]
        );
        assert!(!store
            .merge_provider_update("zonatmo", "novel/191/obra", work("novel", "185"))
            .unwrap());
    }
    #[test]
    fn matching_a_chapter_preserves_local_identity_sources_and_other_profiles() {
        let store = CatalogStore::in_memory().unwrap();
        let id = Uuid::new_v4();
        let local:CatalogItem=serde_json::from_value(json!({"name":"Mi edición","syncId":id,"mediaType":"book","subtype":"manga","units":[{"kind":"chapter","displayNumber":"1","sources":[{"filePath":"manual"}]}]})).unwrap();
        let incoming:CatalogItem=serde_json::from_value(json!({"name":"Título alternativo","mediaType":"book","subtype":"manga","units":[{"kind":"chapter","displayNumber":"2"}]})).unwrap();
        let key = local.key();
        store
            .import(&CatalogDocument::new(vec![local]), false)
            .unwrap();
        store.set_favorite(id, true).unwrap();
        store
            .link_provider_edition(id, &key, "example", "work", incoming)
            .unwrap();
        let mut patch = Map::new();
        patch.insert("description".into(), json!("Descripción oficial"));
        let item = store
            .update_title_metadata(id, &key, patch.clone())
            .unwrap();
        assert_eq!(item.name, "Mi edición");
        assert_eq!(item.metadata["units"].as_array().unwrap().len(), 2);
        assert_eq!(
            item.metadata["units"][0]["sources"][0]["filePath"],
            "manual"
        );
        assert_eq!(item.metadata["onlineProviders"]["example"], "work");
        assert_eq!(item.metadata["description"], "Descripción oficial");
        assert!(store
            .update_title_metadata(id, "un título distinto", patch)
            .is_err());
        assert_eq!(
            store
                .page(&crate::CatalogQuery::default())
                .unwrap()
                .favorite_ids,
            vec![id]
        );
    }
    #[test]
    fn colliding_provider_ids_cannot_replace_unrelated_manual_titles() {
        let store = CatalogStore::in_memory().unwrap();
        let id = Uuid::new_v4();
        let manual=serde_json::from_value(json!({"syncId":id,"name":"Origen","mediaType":"book","subtype":"manga","releaseYear":2015,"sources":[{"uri":"manual"}]})).unwrap();
        store
            .import(&CatalogDocument::new(vec![manual]), false)
            .unwrap();
        let movie:CatalogItem=serde_json::from_value(json!({"syncId":id,"name":"Origen","mediaType":"movie","subtype":"movie","releaseYear":2020})).unwrap();
        assert!(store.merge_scraped("site", "abc", movie.clone()).unwrap());
        let mut renamed = movie;
        renamed.name = "Renombrada".into();
        renamed
            .metadata
            .insert("description".into(), json!("Sinopsis actualizada"));
        assert!(store.merge_scraped("site", "abc", renamed.clone()).unwrap());
        assert!(!store.merge_scraped("site", "abc", renamed).unwrap());
        assert_eq!(store.crawl_summary().unwrap().0, 2);
        let saved = store.item(id).unwrap().unwrap();
        assert_eq!(saved.items[0].name, "Origen");
        assert_eq!(saved.items[0].metadata["sources"][0]["uri"], "manual");
        let bound_id = Uuid::new_v5(&Uuid::NAMESPACE_URL, b"umd:crawler:site:abc");
        let bound_item = store.item(bound_id).unwrap().unwrap();
        assert_eq!(bound_item.items[0].name, "Origen");
        assert_eq!(
            bound_item.items[0].metadata["description"],
            "Sinopsis actualizada"
        );
    }
    #[test]
    fn imported_aliases_use_the_portable_field_and_keep_distinct_languages() {
        let item=serde_json::from_value(json!({"mediaType":"book","subtype":"comic","name":"Ejemplo","aliases":[{"name":" nombre  Dos ","language":"es"},{"name":"Nombre Dos","language":"pt"}],"alternativeNames":[{"name":"Nombre Dos","language":"ES","aliasType":"other"}]})).unwrap();
        let clean = catalog_metadata_only(item);
        assert!(!clean.metadata.contains_key("aliases"));
        let aliases = clean.metadata["alternativeNames"].as_array().unwrap();
        assert_eq!(aliases.len(), 2);
        assert_eq!(aliases[0]["name"], "Nombre Dos");
    }

    #[test]
    fn metadata_projection_preserves_safe_refs_and_strips_unsafe_schemes() {
        let item: CatalogItem = serde_json::from_value(json!({
            "mediaType": "movie",
            "subtype": "series",
            "name": "Serie",
            "thumbnailRef": "https://cdn.example.org/poster.jpg",
            "backdropRef": "art/backdrop.webp",
            "seasons": [{
                "number": 1,
                "thumbnailRef": "javascript:alert(1)",
                "units": [{
                    "kind": "episode",
                    "displayNumber": "1",
                    "thumbnailRef": "art/ep1.jpg"
                }]
            }],
            "contributors": [
                {
                    "name": "Studio Safe",
                    "kind": "organization",
                    "url": "https://studio.example.org/",
                    "thumbnailRef": "https://studio.example.org/logo.png"
                },
                {
                    "name": "Studio Unsafe",
                    "kind": "organization",
                    "url": "file:///etc/passwd",
                    "thumbnailRef": "data:image/svg+xml,<svg/>"
                }
            ]
        }))
        .unwrap();
        let clean = catalog_metadata_only(item);
        assert_eq!(
            clean.metadata["thumbnailRef"],
            "https://cdn.example.org/poster.jpg"
        );
        assert_eq!(clean.metadata["backdropRef"], "art/backdrop.webp");
        assert!(clean.metadata["seasons"][0].get("thumbnailRef").is_none());
        assert_eq!(
            clean.metadata["seasons"][0]["units"][0]["thumbnailRef"],
            "art/ep1.jpg"
        );
        assert_eq!(
            clean.metadata["contributors"][0]["url"],
            "https://studio.example.org/"
        );
        assert_eq!(
            clean.metadata["contributors"][0]["thumbnailRef"],
            "https://studio.example.org/logo.png"
        );
        assert!(clean.metadata["contributors"][1].get("url").is_none());
        assert!(clean.metadata["contributors"][1]
            .get("thumbnailRef")
            .is_none());
    }

    #[test]
    fn provider_refresh_preserves_local_name_and_known_status_and_validates_manual_patch() {
        let store = CatalogStore::in_memory().unwrap();
        let id = Uuid::new_v4();
        let local: CatalogItem = serde_json::from_value(json!({
            "syncId": id,
            "name": "Nombre Local",
            "mediaType": "book",
            "subtype": "manga",
            "publicationStatus": "ongoing",
            "units": [{"kind": "chapter", "displayNumber": "1"}]
        }))
        .unwrap();
        let key = local.key();
        store
            .import(&CatalogDocument::new(vec![local]), false)
            .unwrap();
        let incoming: CatalogItem = serde_json::from_value(json!({
            "syncId": id,
            "name": "Nombre Remoto",
            "mediaType": "book",
            "subtype": "manga",
            "publicationStatus": "unknown",
            "units": [{"kind": "chapter", "displayNumber": "2"}]
        }))
        .unwrap();
        assert!(store.merge_scraped("site", "123", incoming).unwrap());
        let updated = store.item(id).unwrap().unwrap().items.remove(0);
        assert_eq!(updated.name, "Nombre Local");
        assert_eq!(updated.metadata["publicationStatus"], "ongoing");
        assert_eq!(updated.metadata["units"].as_array().unwrap().len(), 2);

        let mut bad_year = Map::new();
        bad_year.insert("releaseYear".into(), json!(1799));
        assert!(store.update_title_metadata(id, &key, bad_year).is_err());

        let mut bad_thumb = Map::new();
        bad_thumb.insert(
            "thumbnailRef".into(),
            json!("https://user:pass@example.org/cover.jpg"),
        );
        assert!(store.update_title_metadata(id, &key, bad_thumb).is_err());

        let mut good_patch = Map::new();
        good_patch.insert("releaseYear".into(), json!(2024));
        good_patch.insert(
            "thumbnailRef".into(),
            json!("https://cdn.example.org/cover.jpg"),
        );
        let patched = store.update_title_metadata(id, &key, good_patch).unwrap();
        assert_eq!(patched.metadata["releaseYear"], 2024);
        assert_eq!(
            patched.metadata["thumbnailRef"],
            "https://cdn.example.org/cover.jpg"
        );
    }

    #[test]
    fn merge_remote_item_updates_chapters_and_thumbnail_while_preserving_local_data() {
        let store = CatalogStore::in_memory().unwrap();
        let id = Uuid::new_v4();
        let local: CatalogItem = serde_json::from_value(json!({
            "syncId": id,
            "name": "Heroe, regresa",
            "mediaType": "book",
            "subtype": "manga",
            "publicationStatus": "ongoing",
            "onlineProviders": {"olympusxyz": "hero-returns"},
            "units": [
                {"kind": "chapter", "displayNumber": "1", "sources": [{"filePath": "local/ch1.cbz"}]}
            ]
        }))
        .unwrap();
        store
            .import(&CatalogDocument::new(vec![local]), false)
            .unwrap();

        let remote: CatalogItem = serde_json::from_value(json!({
            "syncId": id,
            "name": "Heroe, regresa",
            "mediaType": "book",
            "subtype": "manga",
            "publicationStatus": "unknown",
            "description": "Sinopsis nueva",
            "thumbnailRef": "https://cdn.example.org/hero.webp",
            "onlineProviders": {
                "olympusxyz": "other-translation",
                "zonatmo": "library/manhwa/123/hero-returns",
                "leercapitulo": "https://private.example/movie"
            },
            "units": [
                {"kind": "chapter", "displayNumber": "1"},
                {"kind": "chapter", "displayNumber": "2"}
            ]
        }))
        .unwrap();

        let (merged, changed) = store.merge_remote_item(id, remote).unwrap();
        assert!(changed);
        assert_eq!(merged.metadata["description"], "Sinopsis nueva");
        assert_eq!(
            merged.metadata["thumbnailRef"],
            "https://cdn.example.org/hero.webp"
        );
        assert_eq!(merged.metadata["publicationStatus"], "ongoing");
        assert_eq!(
            merged.metadata["onlineProviders"]["olympusxyz"],
            "hero-returns"
        );
        assert_eq!(merged.metadata["onlineProviders"]["zonatmo"], "library/manhwa/123/hero-returns");
        assert!(merged.metadata["onlineProviders"]["leercapitulo"].is_null());
        let units = merged.metadata["units"].as_array().unwrap();
        assert_eq!(units.len(), 2);
        assert_eq!(units[0]["sources"][0]["filePath"], "local/ch1.cbz");

        // A later server payload without thumbnailRef must not erase the existing cover.
        let without_art: CatalogItem = serde_json::from_value(json!({
            "syncId": id,
            "name": "Heroe, regresa",
            "mediaType": "book",
            "subtype": "manga",
            "units": [
                {"kind": "chapter", "displayNumber": "3"}
            ]
        }))
        .unwrap();
        let (merged2, changed2) = store.merge_remote_item(id, without_art).unwrap();
        assert!(changed2);
        assert_eq!(
            merged2.metadata["thumbnailRef"],
            "https://cdn.example.org/hero.webp"
        );
        assert_eq!(merged2.metadata["units"].as_array().unwrap().len(), 3);
    }
}
