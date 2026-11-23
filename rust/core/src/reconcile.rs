use crate::user_state::{bind_legacy_targets, read_state, save_state};
use crate::{now, validate_backup_with_limit, CatalogDocument, CatalogStore, Error, Result};
use rusqlite::{params, TransactionBehavior};
use serde_json::{json, Value};
use sha2::{Digest, Sha256};
use std::collections::{BTreeMap, HashMap, HashSet};
use uuid::Uuid;

fn invalid(reason: &str) -> Error {
    Error::Invalid(reason.into())
}
fn rows<'a>(doc: &'a Value, field: &str) -> impl Iterator<Item = &'a Value> {
    doc[field].as_array().into_iter().flatten()
}
fn id(value: &Value) -> Result<Uuid> {
    value
        .as_str()
        .and_then(|s| Uuid::parse_str(s).ok())
        .filter(|v| !v.is_nil())
        .ok_or_else(|| invalid("El respaldo contiene una referencia sin identidad."))
}
fn stamp(row: &Value) -> (i64, &str, Option<Uuid>) {
    let v = &row["syncVersion"];
    (
        v["clock"].as_i64().unwrap_or(0),
        v["deviceId"].as_str().unwrap_or(""),
        v["operationId"]
            .as_str()
            .and_then(|s| Uuid::parse_str(s).ok()),
    )
}
// Very old backups predate per-record versions. Generate the same stamp on
// every retry, including when two devices encounter the same legacy snapshot.
pub(crate) fn legacy(doc: &Value, row: &Value, key: &str) -> Result<Value> {
    let date = row["updatedAt"]
        .as_str()
        .unwrap_or_else(|| doc["exportedAt"].as_str().unwrap_or(""));
    let time = chrono::DateTime::parse_from_rfc3339(date)
        .map_err(|_| invalid("Fecha de sincronización inválida."))?;
    let device = &doc["deviceId"];
    let digest = Sha256::digest(serde_json::to_vec(&(device, date, key))?);
    let mut bytes = [0; 16];
    bytes.copy_from_slice(&digest[..16]);
    Ok(
        json!({"clock":time.timestamp_millis().max(1),"operationId":Uuid::from_bytes(bytes),"deviceId":device,"modifiedAt":date}),
    )
}
type Records = BTreeMap<String, Value>;
fn compare_payload(left: &Value, right: &Value) -> std::cmp::Ordering {
    use std::cmp::Ordering;
    let text = |field: &str| {
        left[field]
            .as_str()
            .unwrap_or("")
            .encode_utf16()
            .cmp(right[field].as_str().unwrap_or("").encode_utf16())
    };
    for field in ["isFavorite", "isMember"] {
        if left[field].is_boolean() {
            return right[field].as_bool().cmp(&left[field].as_bool());
        }
    }
    if left["isCompleted"].is_boolean() {
        return left["isCompleted"]
            .as_bool()
            .cmp(&right["isCompleted"].as_bool())
            .then_with(|| text("positionKind"))
            .then_with(|| {
                left["position"]
                    .as_f64()
                    .partial_cmp(&right["position"].as_f64())
                    .unwrap_or(Ordering::Equal)
            });
    }
    if left["isDeleted"].is_boolean() {
        return right["isDeleted"]
            .as_bool()
            .cmp(&left["isDeleted"].as_bool())
            .then_with(|| text("name"));
    }
    if left["displayMode"].is_string() {
        return text("displayMode");
    }
    text("toId")
}
fn insert(records: &mut Records, key: String, row: Value) {
    if let Some(current) = records.get(&key) {
        match stamp(&row).cmp(&stamp(current)) {
            std::cmp::Ordering::Less => return,
            std::cmp::Ordering::Equal
                if row == *current || compare_payload(&row, current).is_le() =>
            {
                return
            }
            _ => {}
        }
    }
    records.insert(key, row);
}
fn values(records: Records) -> Value {
    Value::Array(records.into_values().collect())
}

const COLLECTIONS: [&str; 7] = [
    "progress",
    "readerPreferences",
    "favoriteItemStates",
    "favoriteStates",
    "favoriteContributorKeys",
    "lists",
    "catalogRedirects",
];

// .NET orders lists by creation date and normalizes timestamp spellings. A
// different presentation order is not an edit to send back to the account.
fn comparable_rows(doc: &Value, field: &str) -> Vec<String> {
    let mut result: Vec<_> = rows(doc, field)
        .map(|r| {
            if field != "lists" {
                return r.to_string();
            }
            let mut list = r.clone();
            for child in ["items", "itemStates"] {
                list[child] = json!(comparable_rows(r, child));
            }
            list.to_string()
        })
        .collect();
    result.sort_unstable();
    result
}

struct Titles {
    names: HashMap<Uuid, Value>,
    by_name: HashMap<(String, String, Option<String>), Uuid>,
    redirects: HashMap<Uuid, Uuid>,
    deleted: HashSet<Uuid>,
}
impl Titles {
    fn index_item(&mut self, key: Uuid, item: &crate::CatalogItem) {
        let media = item.media_type.to_ascii_lowercase();
        let norm_name = crate::normalize(&item.name);
        let norm_sub = crate::user_state::canonical_subtype(&media, &item.subtype);
        self.names.insert(
            key,
            json!({"itemId":key,"mediaType":media,"name":item.name,"subtype":item.subtype}),
        );
        self.by_name
            .entry((media.clone(), norm_name.clone(), None))
            .or_insert(key);
        self.by_name.insert((media, norm_name, Some(norm_sub)), key);
    }

    fn resolve_by_name(&self, reference: &Value) -> Option<Uuid> {
        let media = reference["mediaType"]
            .as_str()
            .map(|v| v.trim().to_ascii_lowercase())?;
        let name = crate::normalize(reference["name"].as_str()?);
        if name.is_empty() {
            return None;
        }
        if let Some(sub) = reference["subtype"]
            .as_str()
            .map(|s| crate::user_state::canonical_subtype(&media, s))
            .filter(|s| !s.is_empty())
        {
            if let Some(&id) = self.by_name.get(&(media.clone(), name.clone(), Some(sub))) {
                return Some(id);
            }
        }
        self.by_name.get(&(media, name, None)).copied()
    }

    fn canonical(&self, reference: &mut Value) -> Result<Option<Uuid>> {
        match self.canonical_with_missing(reference, false)? {
            Some(CanonicalRef::Item(id)) => Ok(Some(id)),
            _ => Ok(None),
        }
    }

    fn canonical_with_missing(
        &self,
        reference: &mut Value,
        allow_missing: bool,
    ) -> Result<Option<CanonicalRef>> {
        let raw_id = if reference["itemId"].is_null() {
            self.resolve_by_name(reference)
        } else {
            Some(id(&reference["itemId"])?)
        };
        let Some(mut key) = raw_id else {
            if allow_missing {
                let media = reference["mediaType"]
                    .as_str()
                    .map(|v| v.trim().to_ascii_lowercase())
                    .unwrap_or_default();
                let name = crate::normalize(reference["name"].as_str().unwrap_or(""));
                let sub = reference["subtype"]
                    .as_str()
                    .map(|s| crate::user_state::canonical_subtype(&media, s))
                    .unwrap_or_default();
                return Ok(Some(CanonicalRef::Missing(format!(
                    "missing:{media}:{name}:{sub}"
                ))));
            }
            return Err(invalid("El respaldo contiene una referencia sin identidad."));
        };
        let mut seen = HashSet::new();
        while let Some(next) = self.redirects.get(&key) {
            if !seen.insert(key) {
                return Err(invalid("El respaldo tiene redirecciones circulares."));
            }
            key = *next;
        }
        if self.deleted.contains(&key) {
            return Ok(None);
        }
        let Some(title) = self.names.get(&key) else {
            if allow_missing {
                reference["itemId"] = json!(key);
                return Ok(Some(CanonicalRef::Item(key)));
            }
            return Err(invalid("Falta un título al que hace referencia el perfil. Revisá el catálogo antes de sincronizar."));
        };
        for field in ["itemId", "mediaType", "name", "subtype"] {
            reference[field] = title[field].clone();
        }
        Ok(Some(CanonicalRef::Item(key)))
    }
}

enum CanonicalRef {
    Item(Uuid),
    Missing(String),
}
impl CanonicalRef {
    fn into_key(self) -> String {
        match self {
            Self::Item(id) => id.to_string(),
            Self::Missing(key) => key,
        }
    }
}

fn merge_personal(
    local: &Value,
    remote: &Value,
    titles: &Titles,
    output: &mut Value,
) -> Result<()> {
    let mut lists = Records::new();
    let mut members = BTreeMap::<String, Records>::new();
    for field in [
        "progress",
        "readerPreferences",
        "favoriteItemStates",
        "favoriteStates",
        "lists",
    ] {
        let mut records = Records::new();
        for doc in [remote, local] {
            if field == "favoriteStates" {
                let known: HashSet<_> = rows(doc, field)
                    .filter_map(|r| r["contributorKey"].as_str())
                    .collect();
                for name in rows(doc, "favoriteContributorKeys")
                    .filter_map(Value::as_str)
                    .filter(|s| !known.contains(s))
                {
                    insert(
                        &mut records,
                        name.into(),
                        json!({"contributorKey":name,"isFavorite":true,"syncVersion":legacy(doc,&Value::Null,name)?}),
                    );
                }
            }
            for original in rows(doc, field) {
                let mut row = original.clone();
                let key = match field {
                    "lists" => id(&row["id"])?.to_string(),
                    "favoriteStates" => row["contributorKey"]
                        .as_str()
                        .ok_or_else(|| invalid("Favorito de colaborador inválido."))?
                        .trim()
                        .to_owned(),
                    "progress" => {
                        let content = &mut row["content"];
                        let Some(key) = titles.canonical(&mut content["item"])? else {
                            continue;
                        };
                        serde_json::to_string(&(
                            key,
                            &content["unitKind"],
                            &content["displayNumber"],
                            &content["seasonNumber"],
                        ))?
                    }
                    _ => {
                        let allow_missing =
                            field == "favoriteItemStates" && row["isFavorite"] == false;
                        let Some(key) =
                            titles.canonical_with_missing(&mut row["item"], allow_missing)?
                        else {
                            continue;
                        };
                        key.into_key()
                    }
                };
                if row["syncVersion"].is_null() {
                    row["syncVersion"] = legacy(doc, &row, &format!("{field}:{key}"))?;
                }
                if field == "lists" {
                    let states = members.entry(key.clone()).or_default();
                    let mut tracked = HashSet::new();
                    for original in rows(&row, "itemStates") {
                        let mut state = original.clone();
                        if let Some(item) = titles.canonical(&mut state["item"])? {
                            tracked.insert(item);
                            insert(states, item.to_string(), state);
                        }
                    }
                    for item in rows(&row, "items") {
                        let mut reference = item.clone();
                        if let Some(item) = titles.canonical(&mut reference)? {
                            if !tracked.contains(&item) {
                                insert(
                                    states,
                                    item.to_string(),
                                    json!({"item":reference,"isMember":true,"syncVersion":row["syncVersion"]}),
                                );
                            }
                        }
                    }
                    row["itemStates"] = json!([]);
                    row["items"] = json!([]);
                    insert(&mut lists, key, row);
                } else {
                    insert(&mut records, key, row);
                }
            }
        }
        if field != "lists" {
            output[field] = values(records);
        }
    }
    for (key, list) in &mut lists {
        let states = members.remove(key).unwrap_or_default();
        if list["isDeleted"] != true {
            list["items"] = Value::Array(
                states
                    .values()
                    .filter(|s| s["isMember"] == true)
                    .map(|s| s["item"].clone())
                    .collect(),
            );
        }
        list["itemStates"] = values(states);
    }
    let mut active_keys: Vec<(i64, String)> = lists
        .iter()
        .filter(|(_, list)| list["isDeleted"] != true)
        .map(|(k, list)| {
            let created = list["createdAt"]
                .as_str()
                .and_then(|s| chrono::DateTime::parse_from_rfc3339(s).ok())
                .map(|dt| dt.timestamp_millis())
                .unwrap_or(i64::MAX);
            (created, k.clone())
        })
        .collect();
    active_keys.sort();
    let mut used_names = HashSet::new();
    for (_, key) in active_keys {
        if let Some(list) = lists.get_mut(&key) {
            let current = list["name"].as_str().unwrap_or("").trim().to_owned();
            let deduplicated = crate::user_state::unique_list_name(&current, &mut used_names);
            if deduplicated != current {
                list["name"] = json!(deduplicated);
            }
        }
    }
    output["lists"] = values(lists);
    output["favoriteContributorKeys"] = Value::Array(
        rows(output, "favoriteStates")
            .filter(|s| s["isFavorite"] == true)
            .map(|s| s["contributorKey"].clone())
            .collect(),
    );
    Ok(())
}

/// A copy of a backup without its catalog items. Merging personal state reads
/// only the personal collections, so copying every title with them held two
/// or three extra catalogs in memory during a sync.
fn without_items(document: &Value) -> Value {
    let Some(fields) = document.as_object() else {
        return document.clone();
    };
    Value::Object(
        fields
            .iter()
            .map(|(key, value)| {
                let copy = match value.as_object().filter(|_| key == "catalog") {
                    Some(catalog) => Value::Object(
                        catalog
                            .iter()
                            .filter(|(field, _)| *field != "items")
                            .map(|(field, value)| (field.clone(), value.clone()))
                            .collect(),
                    ),
                    None => value.clone(),
                };
                (key.clone(), copy)
            })
            .collect(),
    )
}

pub(crate) fn merge_patch_state(
    base: &Value,
    patch: &Value,
    catalog: &CatalogDocument,
    output: &mut Value,
) -> Result<()> {
    let mut left = without_items(base);
    let mut right = without_items(patch);
    bind_legacy_targets(&mut left, catalog)?;
    bind_legacy_targets(&mut right, catalog)?;
    let mut redirects = Records::new();
    for doc in [&left, &right] {
        for row in rows(doc, "catalogRedirects") {
            insert(&mut redirects, id(&row["fromId"])?.to_string(), row.clone());
        }
    }
    let mut titles = Titles {
        names: HashMap::new(),
        by_name: HashMap::new(),
        deleted: HashSet::new(),
        redirects: HashMap::new(),
    };
    for row in redirects.values() {
        titles
            .redirects
            .insert(id(&row["fromId"])?, id(&row["toId"])?);
    }
    for item in &catalog.items {
        let key = item.id()?;
        if item.deleted {
            titles.deleted.insert(key);
        } else {
            titles.index_item(key, item);
        }
    }
    output["catalogRedirects"] = values(redirects);
    merge_personal(&right, &left, &titles, output)
}

impl CatalogStore {
    /// SQLite counts this connection's writes and other connections' commits separately.
    pub fn sync_marker(&self) -> Result<(u64, i64)> {
        let db = self.0.lock().map_err(|_| Error::Lock)?;
        Ok((
            db.total_changes(),
            db.query_row("PRAGMA data_version", [], |r| r.get(0))?,
        ))
    }
    /// Merge under the same write lock as local edits. The caller uploads the
    /// returned document with the revision it read and retries on conflicts.
    pub fn reconcile_profile(&self, remote: Option<&Value>) -> Result<(bool, Option<Value>)> {
        self.reconcile_profile_with_limit(remote, crate::MAX_DOCUMENT_BYTES)
    }

    pub fn reconcile_profile_with_limit(
        &self,
        remote: Option<&Value>,
        max: usize,
    ) -> Result<(bool, Option<Value>)> {
        let remote_catalog = remote
            .map(|r| validate_backup_with_limit(r, max))
            .transpose()?;
        let mut db = self.0.lock().map_err(|_| Error::Lock)?;
        let tx = db.transaction_with_behavior(TransactionBehavior::Immediate)?;
        let mut local_catalog = Self::export_from(&tx)?;
        let live_ids: HashSet<Uuid> = local_catalog
            .items
            .iter()
            .map(crate::CatalogItem::id)
            .collect::<Result<_>>()?;
        for tombstone in Self::tombstones_from(&tx)? {
            let key = tombstone.id()?;
            if !live_ids.contains(&key) {
                local_catalog.items.push(tombstone);
            }
        }
        let shared: bool =
            tx.query_row("SELECT count(*)>1 FROM rust_profiles", [], |r| r.get(0))?;
        let original = read_state(&tx, self.1)?;
        let mut local = original.clone();
        bind_legacy_targets(&mut local, &local_catalog)?;
        let mut right = remote.map_or(Value::Null, without_items);
        if let Some(catalog) = &remote_catalog {
            bind_legacy_targets(&mut right, catalog)?;
        }
        // The catalog items of `output` are written once the merge is known.
        let mut output = remote.map_or_else(|| local.clone(), without_items);
        if remote.is_none() {
            let items = std::mem::take(&mut local_catalog.items);
            output["catalog"] = serde_json::to_value(&local_catalog)?;
            local_catalog.items = items;
        }
        let mut redirects = Records::new();
        for doc in [&right, &local] {
            for row in rows(doc, "catalogRedirects") {
                insert(&mut redirects, id(&row["fromId"])?.to_string(), row.clone());
            }
        }
        let mut titles = Titles {
            names: HashMap::new(),
            by_name: HashMap::new(),
            deleted: HashSet::new(),
            redirects: HashMap::new(),
        };
        for row in redirects.values() {
            titles
                .redirects
                .insert(id(&row["fromId"])?, id(&row["toId"])?);
        }
        let empty = CatalogDocument::new(vec![]);
        let merged = crate::reconcile_catalog::merge(
            &local_catalog,
            remote_catalog.as_ref().unwrap_or(&empty),
            titles.redirects,
        )?;
        titles.redirects = merged.redirects;
        for (from, to) in merged.new_redirects {
            let source = json!({"deviceId":"00000000000000000000000000000000","exportedAt":output["exportedAt"]});
            let version = legacy(
                &source,
                &Value::Null,
                &format!("redirect:{}", from.simple()),
            )?;
            redirects.insert(
                from.to_string(),
                json!({"fromId":from,"toId":to,"syncVersion":version}),
            );
        }
        output["catalogRedirects"] = values(redirects);
        let local_by_id = local_catalog
            .items
            .iter()
            .map(|i| Ok((i.id()?, i)))
            .collect::<Result<HashMap<_, _>>>()?;
        let remote_by_id = remote_catalog
            .iter()
            .flat_map(|d| &d.items)
            .enumerate()
            .map(|(n, i)| Ok((i.id()?, (n, i))))
            .collect::<Result<HashMap<_, _>>>()?;
        let mut additions = Vec::new();
        let mut outgoing = Vec::new();
        for item in &merged.items {
            let key = item.id()?;
            if item.deleted {
                titles.deleted.insert(key);
                if (!shared || !live_ids.contains(&key))
                    && local_by_id.get(&key).copied() != Some(item)
                {
                    let mut tomb = item.clone();
                    tomb.sync_id = Some(key);
                    additions.push(tomb);
                }
            } else {
                titles.index_item(key, item);
                if local_by_id.get(&key).copied() != Some(item) {
                    let mut added = item.clone();
                    added.sync_id = Some(key);
                    additions.push(added);
                }
            }
            let raw = remote_by_id
                .get(&key)
                .filter(|(_, r)| *r == item)
                .and_then(|(n, _)| Some(remote?["catalog"]["items"][*n].clone()));
            outgoing.push(raw.unwrap_or(serde_json::to_value(item)?));
        }
        output["catalog"]["items"] = Value::Array(outgoing);
        merge_personal(&local, &right, &titles, &mut output)?;
        output["version"] = json!(4);
        let upload = remote.is_none_or(|remote| {
            remote["version"] != output["version"]
                || remote["catalog"] != output["catalog"]
                || COLLECTIONS.iter().any(|key| {
                    remote[key] != output[key]
                        && comparable_rows(remote, key) != comparable_rows(&output, key)
                })
        });
        if upload {
            output["deviceId"] = local["deviceId"].clone();
            output["deviceName"] = local["deviceName"].clone();
            output["exportedAt"] = json!(now());
        }
        validate_backup_with_limit(&output, max)?;
        let mut state = original.clone();
        state["version"] = output["version"].clone();
        for field in COLLECTIONS {
            state[field] = output[field].clone();
        }
        let obsolete = live_ids
            .iter()
            .filter(|id| titles.redirects.contains_key(id))
            .copied()
            .collect::<Vec<_>>();
        let obsolete_tombstones = local_by_id
            .keys()
            .filter(|id| !live_ids.contains(id) && titles.redirects.contains_key(id))
            .copied()
            .collect::<Vec<_>>();
        let changed = state != original
            || !additions.is_empty()
            || !obsolete.is_empty()
            || !obsolete_tombstones.is_empty();
        if !additions.is_empty() {
            Self::import_into(&tx, &CatalogDocument::new(additions), false)?;
        }
        for old_tomb in &obsolete_tombstones {
            tx.execute(
                "DELETE FROM rust_catalog_tombstones WHERE id=?1",
                [old_tomb.to_string()],
            )?;
        }
        if !obsolete.is_empty() {
            let index = merged
                .items
                .iter()
                .filter(|i| !i.deleted)
                .map(|i| Ok((i.id()?, i)))
                .collect::<Result<HashMap<_, _>>>()?;
            let has_bindings:bool=tx.query_row("SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE name='rust_crawl_bindings' AND type='table')",[],|r|r.get(0))?;
            let mut others =
                tx.prepare("SELECT id FROM rust_profiles WHERE id<>?1 AND document IS NOT NULL")?;
            let profiles = others
                .query_map([self.1.to_string()], |r| r.get::<_, String>(0))?
                .collect::<rusqlite::Result<Vec<_>>>()?;
            for profile in profiles {
                let profile = Uuid::parse_str(&profile).map_err(|_| invalid("Perfil inválido."))?;
                let mut doc = read_state(&tx, profile)?;
                let previous = doc.clone();
                bind_legacy_targets(&mut doc, &local_catalog)?;
                for field in ["favoriteItemStates", "readerPreferences", "progress"] {
                    for row in doc[field].as_array_mut().into_iter().flatten() {
                        let reference = if field == "progress" {
                            &mut row["content"]["item"]
                        } else {
                            &mut row["item"]
                        };
                        crate::reconcile_catalog::replace_reference(
                            reference,
                            &titles.redirects,
                            &index,
                        )?;
                    }
                }
                for list in doc["lists"].as_array_mut().into_iter().flatten() {
                    for field in ["items", "itemStates"] {
                        for row in list[field].as_array_mut().into_iter().flatten() {
                            let reference = if field == "items" {
                                row
                            } else {
                                &mut row["item"]
                            };
                            crate::reconcile_catalog::replace_reference(
                                reference,
                                &titles.redirects,
                                &index,
                            )?;
                        }
                    }
                }
                if doc != previous {
                    let mut other_titles = Titles {
                        names: titles.names.clone(),
                        by_name: titles.by_name.clone(),
                        deleted: HashSet::new(),
                        redirects: titles.redirects.clone(),
                    };
                    for item in &local_catalog.items {
                        let key = item.id()?;
                        if !other_titles.names.contains_key(&key) {
                            other_titles.index_item(key, item);
                        }
                    }
                    let mut clean = doc.clone();
                    merge_personal(&doc, &Value::Null, &other_titles, &mut clean)?;
                    save_state(&tx, profile, &clean)?;
                }
            }
            for old in obsolete {
                let new = crate::reconcile_catalog::destination(old, &titles.redirects)?;
                if !index.contains_key(&new) {
                    continue;
                }
                let args = [new.to_string(), old.to_string()];
                tx.execute("INSERT OR IGNORE INTO rust_favorites(profile_id,id) SELECT profile_id,?1 FROM rust_favorites WHERE id=?2",rusqlite::params_from_iter(&args))?;
                if has_bindings {
                    tx.execute(
                        "UPDATE rust_crawl_bindings SET item_id=?1 WHERE item_id=?2",
                        rusqlite::params_from_iter(&args),
                    )?;
                }
                tx.execute("DELETE FROM rust_catalog WHERE id=?1", [old.to_string()])?;
            }
            tx.execute(
                "UPDATE rust_state SET revision=revision+1,updated_at=?1 WHERE singleton=1",
                [now()],
            )?;
        }
        let desired_favorites: HashSet<String> = rows(&state, "favoriteItemStates")
            .filter(|r| r["isFavorite"] == true)
            .filter_map(|r| id(&r["item"]["itemId"]).ok().map(|v| v.to_string()))
            .collect();
        let current_favorites: HashSet<String> = {
            let mut stmt =
                tx.prepare_cached("SELECT id FROM rust_favorites WHERE profile_id=?1")?;
            let rows = stmt
                .query_map([self.1.to_string()], |r| r.get::<_, String>(0))?
                .collect::<rusqlite::Result<HashSet<_>>>()?;
            rows
        };
        let mut changed = changed;
        if desired_favorites != current_favorites {
            tx.execute(
                "DELETE FROM rust_favorites WHERE profile_id=?1",
                [self.1.to_string()],
            )?;
            for fav_id in &desired_favorites {
                tx.execute(
                    "INSERT OR IGNORE INTO rust_favorites(profile_id,id)
                     SELECT ?1,?2 WHERE EXISTS(SELECT 1 FROM rust_catalog WHERE id=?2)",
                    params![self.1.to_string(), fav_id],
                )?;
            }
            changed = true;
        }
        // Persist the generated local device ID even for a pristine profile.
        let needs_identity: bool = tx.query_row(
            "SELECT document IS NULL FROM rust_profiles WHERE id=?1",
            [self.1.to_string()],
            |r| r.get(0),
        )?;
        if state != original || needs_identity {
            save_state(&tx, self.1, &state)?;
        }
        tx.commit()?;
        Ok((changed, upload.then_some(output)))
    }
}
