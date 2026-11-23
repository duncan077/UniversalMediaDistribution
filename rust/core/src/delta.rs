use crate::{Error, Result};
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};
use std::collections::{BTreeMap, HashSet};

const GROUPS: [&str; 7] = [
    "items",
    "progress",
    "readerPreferences",
    "favoriteItemStates",
    "favoriteStates",
    "lists",
    "catalogRedirects",
];

fn rows<'a>(doc: &'a Value, group: &str) -> impl Iterator<Item = &'a Value> {
    let source = if group == "items" {
        &doc["catalog"]
    } else {
        doc
    };
    source[group].as_array().into_iter().flatten()
}

fn key(group: &str, row: &Value) -> Option<String> {
    let id = match group {
        "items" => &row["syncId"],
        "progress" => &row["content"]["item"]["itemId"],
        "favoriteStates" => &row["contributorKey"],
        "lists" => &row["id"],
        "catalogRedirects" => &row["fromId"],
        _ => &row["item"]["itemId"],
    }
    .as_str();
    let Some(id) = id else {
        if group == "favoriteItemStates" && row["isFavorite"] == false {
            let item = &row["item"];
            let media = item["mediaType"].as_str()?.trim().to_ascii_lowercase();
            let name = crate::normalize(item["name"].as_str()?);
            let sub = item["subtype"]
                .as_str()
                .map(|s| crate::user_state::canonical_subtype(&media, s))
                .unwrap_or_default();
            return Some(format!("{group}:missing:{media}:{name}:{sub}"));
        }
        return None;
    };
    if group == "progress" {
        let unit = &row["content"];
        return Some(
            json!([
                group,
                id,
                unit["unitKind"],
                unit["displayNumber"],
                unit["seasonNumber"]
            ])
            .to_string(),
        );
    }
    Some(format!("{group}:{id}"))
}

fn records(doc: &Value) -> Option<BTreeMap<String, &Value>> {
    if doc["version"] != 4 {
        return None;
    }
    let mut result = BTreeMap::new();
    for group in GROUPS {
        for row in rows(doc, group) {
            if result.insert(key(group, row)?, row).is_some() {
                return None;
            }
            if group == "lists" {
                let mut tracked = HashSet::new();
                for state in rows(row, "itemStates") {
                    let item = state["item"]["itemId"].as_str()?;
                    tracked.insert(item);
                    if result.insert(member_key(row, item)?, state).is_some() {
                        return None;
                    }
                }
                if row["items"]
                    .as_array()?
                    .iter()
                    .any(|i| !i["itemId"].as_str().is_some_and(|id| tracked.contains(id)))
                {
                    return None;
                }
            }
        }
    }
    // Old exports can carry a contributor favorite without its versioned row.
    let contributors: HashSet<_> = rows(doc, "favoriteStates")
        .filter(|v| v["isFavorite"] == true)
        .filter_map(|v| v["contributorKey"].as_str())
        .collect();
    if rows(doc, "favoriteContributorKeys")
        .any(|v| !v.as_str().is_some_and(|k| contributors.contains(k)))
    {
        return None;
    }
    Some(result)
}

fn member_key(list: &Value, item: &str) -> Option<String> {
    Some(format!("member:{}:{item}", list["id"].as_str()?))
}

fn same_row(key: &str, before: Option<&&Value>, after: &Value) -> bool {
    let Some(before) = before else { return false };
    if !key.starts_with("lists:") {
        return **before == *after;
    }
    let Some(a) = before.as_object() else {
        return false;
    };
    let Some(b) = after.as_object() else {
        return false;
    };
    a.iter()
        .filter(|(k, _)| k.as_str() != "items" && k.as_str() != "itemStates")
        .eq(b
            .iter()
            .filter(|(k, _)| k.as_str() != "items" && k.as_str() != "itemStates"))
}

#[derive(Default, Serialize, Deserialize)]
pub struct BackupRevisionIndex {
    // A complete replacement can physically remove rows. Only a full snapshot
    // can describe that removal to a client whose baseline predates it.
    floor: i64,
    #[serde(default)]
    indexed: bool,
    revisions: BTreeMap<String, i64>,
}

impl BackupRevisionIndex {
    pub fn advance(&mut self, previous: Option<&Value>, next: &Value, revision: i64) {
        let Some(current) = records(next) else {
            self.floor = revision;
            self.indexed = false;
            self.revisions.clear();
            return;
        };
        let old = previous.and_then(records);
        if !self.indexed
            || old.as_ref().is_none_or(|old| {
                old.len() != self.revisions.len()
                    || old
                        .keys()
                        .any(|k| !self.revisions.contains_key(k) || !current.contains_key(k))
            })
        {
            self.floor = revision;
            self.revisions.clear();
        }
        self.indexed = true;
        self.revisions = current
            .into_iter()
            .map(|(key, row)| {
                let stamp = if same_row(&key, old.as_ref().and_then(|o| o.get(&key)), row) {
                    self.revisions.get(&key).copied().unwrap_or(revision)
                } else {
                    revision
                };
                (key, stamp)
            })
            .collect();
    }

    pub fn changes(&self, doc: &Value, since: i64) -> Option<Value> {
        if !self.indexed || since < self.floor || doc["version"] != 4 {
            return None;
        }
        extract(doc, |k| self.revisions.get(k).is_some_and(|r| *r > since))
    }

    pub fn empty_changes(&self, doc: &Value) -> Option<Value> {
        self.indexed.then(|| envelope(doc))
    }
}

/// A physical removal or a legacy identity requires a revision-checked full upload.
pub fn backup_patch(previous: &Value, next: &Value) -> Option<Value> {
    let old = records(previous)?;
    let current = records(next)?;
    if old.keys().any(|k| !current.contains_key(k)) {
        return None;
    }
    extract(next, |k| {
        current.get(k).is_some_and(|r| !same_row(k, old.get(k), r))
    })
}

fn envelope(doc: &Value) -> Value {
    let mut header = json!({});
    if let Some(fields) = doc.as_object() {
        for (name, value) in fields {
            if name != "catalog"
                && name != "favoriteContributorKeys"
                && !GROUPS.contains(&name.as_str())
            {
                header[name] = value.clone();
            }
        }
    }
    if let Some(fields) = doc["catalog"].as_object() {
        for (name, value) in fields {
            if name != "items" {
                header["catalog"][name] = value.clone();
            }
        }
    }
    header["catalog"]["items"] = json!([]);
    for group in GROUPS.into_iter().filter(|g| *g != "items") {
        header[group] = json!([]);
    }
    header["favoriteContributorKeys"] = json!([]);
    header
}

fn extract(doc: &Value, changed: impl Fn(&str) -> bool) -> Option<Value> {
    let mut output = envelope(doc);
    let mut required = HashSet::new();
    for group in GROUPS.into_iter().filter(|g| *g != "items") {
        let mut selected = Vec::new();
        for row in rows(doc, group) {
            let modified = changed(&key(group, row)?);
            if group == "lists" {
                let mut members = Vec::new();
                for state in rows(row, "itemStates") {
                    let item = state["item"]["itemId"].as_str()?;
                    if changed(&member_key(row, item)?) {
                        required.insert(item);
                        members.push(state.clone());
                    }
                }
                if modified || !members.is_empty() {
                    let mut list = row.clone();
                    list["items"] = json!([]);
                    list["itemStates"] = Value::Array(members);
                    selected.push(list);
                }
                continue;
            }
            if !modified {
                continue;
            }
            match group {
                "progress" => {
                    required.insert(row["content"]["item"]["itemId"].as_str()?);
                }
                "readerPreferences" => {
                    required.insert(row["item"]["itemId"].as_str()?);
                }
                "favoriteItemStates" => {
                    if row["isFavorite"] == true {
                        required.insert(row["item"]["itemId"].as_str()?);
                    } else if let Some(item_id) = row["item"]["itemId"].as_str() {
                        if rows(doc, "items").any(|i| i["syncId"].as_str() == Some(item_id)) {
                            required.insert(item_id);
                        }
                    }
                }
                _ => {}
            }
            selected.push(row.clone());
        }
        output[group] = Value::Array(selected);
    }
    let mut titles = Vec::new();
    for item in rows(doc, "items") {
        let id = item["syncId"].as_str()?;
        if required.remove(id) || changed(&key("items", item)?) {
            titles.push(item.clone());
        }
    }
    if !required.is_empty() {
        return None;
    }
    output["catalog"]["items"] = Value::Array(titles);
    output["favoriteContributorKeys"] = json!([]);
    Some(output)
}

pub fn backup_patch_empty(doc: &Value) -> bool {
    GROUPS.iter().all(|g| rows(doc, g).next().is_none())
        && rows(doc, "favoriteContributorKeys").next().is_none()
}

/// The server delta describes its current rows, including an intentional
/// restore of older progress. Do not choose a winner by clock when rebuilding
/// the remote baseline. Local edits are reconciled against it afterwards.
pub fn apply_received_changes(base: &Value, delta: &Value) -> Result<Option<Value>> {
    crate::validate_backup(delta)?;
    if base["version"] != 4
        || delta["version"] != 4
        || rows(delta, "catalogRedirects").next().is_some()
    {
        return Ok(None);
    }
    let mut result = base.clone();
    for group in GROUPS {
        if rows(delta, group).next().is_none() {
            continue;
        }
        let Some(mut combined) = rows(base, group)
            .map(|r| Some((key(group, r)?, r.clone())))
            .collect::<Option<BTreeMap<_, _>>>()
        else {
            return Ok(None);
        };
        for row in rows(delta, group) {
            let Some(key) = key(group, row) else {
                return Ok(None);
            };
            let mut value = row.clone();
            if group == "lists" {
                let mut members = BTreeMap::new();
                for list in combined.get(&key).into_iter().chain(std::iter::once(row)) {
                    let mut tracked = HashSet::new();
                    for state in rows(list, "itemStates") {
                        let Some(id) = state["item"]["itemId"].as_str() else {
                            return Ok(None);
                        };
                        tracked.insert(id);
                        members.insert(id, state);
                    }
                    if list["items"]
                        .as_array()
                        .into_iter()
                        .flatten()
                        .any(|i| !i["itemId"].as_str().is_some_and(|id| tracked.contains(id)))
                    {
                        return Ok(None);
                    }
                }
                value["items"] = json!(members
                    .values()
                    .filter(|m| value["isDeleted"] != true && m["isMember"] == true)
                    .map(|m| &m["item"])
                    .collect::<Vec<_>>());
                value["itemStates"] = json!(members.values().collect::<Vec<_>>());
            }
            combined.insert(key, value);
        }
        let values = Value::Array(combined.into_values().collect());
        if group == "items" {
            result["catalog"]["items"] = values;
        } else {
            result[group] = values;
        }
    }
    result["favoriteContributorKeys"] = json!(rows(&result, "favoriteStates")
        .filter(|r| r["isFavorite"] == true)
        .map(|r| &r["contributorKey"])
        .collect::<Vec<_>>());
    for field in ["exportedAt", "deviceId", "deviceName"] {
        result[field] = delta[field].clone();
    }
    result["catalog"]["exportedAt"] = delta["catalog"]["exportedAt"].clone();
    if !delta["catalog"]["sourceLabel"].is_null() {
        result["catalog"]["sourceLabel"] = delta["catalog"]["sourceLabel"].clone();
    }
    crate::validate_backup(&result)?;
    Ok(Some(result))
}

/// Apply versioned personal rows with the same conflict rules as desktop sync.
/// Conflicting catalog descriptions need the full catalog reconciliation path.
pub fn merge_backup_patch(base: &Value, patch: &Value) -> Result<Option<Value>> {
    merge_backup_patch_with_limit(base, patch, crate::MAX_DOCUMENT_BYTES)
}

pub fn merge_backup_patch_with_limit(
    base: &Value,
    patch: &Value,
    max: usize,
) -> Result<Option<Value>> {
    let mut catalog = crate::validate_backup_with_limit(base, max)?;
    let incoming = crate::validate_backup_with_limit(patch, max)?;
    if base["version"] != 4 || patch["version"] != 4 {
        return Ok(None);
    }
    let mut merged = base.clone();
    let known = catalog
        .items
        .iter()
        .enumerate()
        .map(|(i, item)| Ok((item.id()?, i)))
        .collect::<Result<BTreeMap<_, _>>>()?;
    let mut names: HashSet<_> = catalog.items.iter().map(|i| i.key()).collect();
    let titles = merged["catalog"]["items"]
        .as_array_mut()
        .ok_or_else(|| Error::Invalid("Catálogo de respaldo inválido.".into()))?;
    for (i, item) in incoming.items.iter().enumerate() {
        if let Some(index) = known.get(&item.id()?) {
            if titles[*index] != patch["catalog"]["items"][i] {
                return Ok(None);
            }
        } else {
            if !names.insert(item.key()) {
                return Ok(None);
            }
            titles.push(patch["catalog"]["items"][i].clone());
            catalog.items.push(item.clone());
        }
    }
    let mut redirect_sources = HashSet::new();
    for doc in [base, patch] {
        for row in rows(doc, "catalogRedirects") {
            if let Some(id) = row["fromId"]
                .as_str()
                .and_then(|s| uuid::Uuid::parse_str(s).ok())
            {
                redirect_sources.insert(id);
            }
        }
    }
    if catalog
        .items
        .iter()
        .any(|item| item.id().is_ok_and(|id| redirect_sources.contains(&id)))
    {
        return Ok(None);
    }
    crate::reconcile::merge_patch_state(base, patch, &catalog, &mut merged)?;
    for field in ["exportedAt", "deviceId", "deviceName"] {
        merged[field] = patch[field].clone();
    }
    crate::validate_backup_with_limit(&merged, max)?;
    Ok(Some(merged))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{CatalogDocument, CatalogStore, ListEdit};
    use uuid::Uuid;

    #[test]
    fn list_delta_transfers_only_changed_members_and_preserves_other_members() {
        let a = Uuid::new_v4();
        let b = Uuid::new_v4();
        let library = CatalogStore::in_memory().unwrap();
        let doc = json!({"format":"universal-media-distribution.catalog","version":1,"items":[
            {"syncId":a,"mediaType":"movie","subtype":"movie","name":"Primera"},
            {"syncId":b,"mediaType":"movie","subtype":"movie","name":"Segunda"}]});
        library
            .import(
                &CatalogDocument::parse(&serde_json::to_vec(&doc).unwrap()).unwrap(),
                false,
            )
            .unwrap();
        library
            .edit_list(ListEdit::Create {
                name: "Compartida".into(),
            })
            .unwrap();
        let id =
            Uuid::parse_str(library.personal_lists().unwrap()[0]["id"].as_str().unwrap()).unwrap();
        let member = |item_id, enabled| {
            library
                .edit_list(ListEdit::Member {
                    id,
                    item_id,
                    enabled,
                })
                .unwrap()
        };
        member(a, true);
        let base = library.backup().unwrap();
        member(b, true);
        let both = library.backup().unwrap();
        let addition = backup_patch(&base, &both).unwrap();
        assert_eq!(
            addition["lists"][0]["itemStates"].as_array().unwrap().len(),
            1
        );
        assert_eq!(addition["catalog"]["items"].as_array().unwrap().len(), 1);
        assert_eq!(addition["catalog"]["items"][0]["syncId"], b.to_string());
        let saved = merge_backup_patch(&base, &addition).unwrap().unwrap();
        let downloaded = apply_received_changes(&base, &addition).unwrap().unwrap();
        for combined in [saved, downloaded] {
            assert_eq!(combined["lists"][0]["items"].as_array().unwrap().len(), 2);
        }
        member(a, false);
        let next = library.backup().unwrap();
        let mut index = BackupRevisionIndex::default();
        index.advance(None, &both, 1);
        index.advance(Some(&both), &next, 2);
        let removal = index.changes(&next, 1).unwrap();
        assert_eq!(removal["lists"][0]["itemStates"][0]["isMember"], false);
        let current = apply_received_changes(&both, &removal).unwrap().unwrap();
        assert_eq!(current["lists"][0]["items"][0]["itemId"], b.to_string());
        assert_eq!(current["lists"][0]["items"].as_array().unwrap().len(), 1);
        // A restored backup can omit the old membership's tombstone entirely.
        index.advance(Some(&next), &base, 3);
        assert!(index.changes(&base, 2).is_none());
    }
}
