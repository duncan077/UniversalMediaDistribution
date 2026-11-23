use crate::{CatalogDocument, CatalogItem, Error, Result};
use serde_json::{json, Value};
use std::collections::{BTreeMap, HashMap, HashSet};
use uuid::Uuid;

pub(super) fn destination(mut id: Uuid, aliases: &HashMap<Uuid, Uuid>) -> Result<Uuid> {
    let mut visited = HashSet::new();
    while let Some(&next) = aliases.get(&id) {
        if !visited.insert(id) {
            return Err(Error::Invalid(
                "El respaldo tiene redirecciones circulares.".into(),
            ));
        }
        id = next;
    }
    Ok(id)
}
fn unique_titles(doc: &CatalogDocument) -> Result<HashMap<String, Option<&CatalogItem>>> {
    let mut names = HashMap::new();
    for item in &doc.items {
        if !item.deleted {
            names
                .entry(item.key())
                .and_modify(|v| *v = None)
                .or_insert(Some(item));
        }
    }
    Ok(names)
}
const COLLECTIONS: [&str; 10] = [
    "alternativeNames",
    "aliases",
    "units",
    "seasons",
    "genres",
    "categories",
    "contributors",
    "distributionRights",
    "sources",
    "onlineProviders",
];

fn priority(item: &CatalogItem) -> (i64, String, String, bool, String) {
    let v = item.metadata.get("syncVersion").unwrap_or(&Value::Null);
    let scalar = match serde_json::to_value(item) {
        Ok(mut scalar) => {
            if let Some(fields) = scalar.as_object_mut() {
                for key in COLLECTIONS {
                    fields.remove(key);
                }
                fields.remove("syncId");
            }
            scalar.to_string()
        }
        Err(_) => String::new(),
    };
    (
        v["clock"].as_i64().unwrap_or_default(),
        v["deviceId"].as_str().unwrap_or_default().into(),
        v["operationId"].as_str().unwrap_or_default().into(),
        item.deleted,
        scalar,
    )
}
fn order_collection(v: &mut Value) {
    if let Value::Array(rows) = v {
        for row in rows.iter_mut() {
            if let Some(obj) = row.as_object_mut() {
                for child in ["units", "sources", "credits"] {
                    if let Some(list) = obj.get_mut(child) {
                        order_collection(list)
                    }
                }
            }
        }
        rows.sort_by_cached_key(|v| (v["sortOrder"].as_i64().unwrap_or_default(), v.to_string()));
    }
}
fn fold(left: &CatalogItem, right: &CatalogItem) -> CatalogItem {
    if left == right {
        return left.clone();
    }
    let (winner, other) = if priority(left)
        .cmp(&priority(right))
        .then_with(|| {
            serde_json::to_string(&left.metadata)
                .unwrap_or_default()
                .cmp(&serde_json::to_string(&right.metadata).unwrap_or_default())
        })
        .is_ge()
    {
        (left, right)
    } else {
        (right, left)
    };
    let mut result = winner.clone();
    if winner.deleted || other.deleted {
        return result;
    }
    if winner
        .metadata
        .get("syncVersion")
        .is_none_or(Value::is_null)
    {
        for (key, value) in &other.metadata {
            if result.metadata.get(key).is_none_or(Value::is_null) {
                result.metadata.insert(key.clone(), value.clone());
            }
        }
    }
    for key in COLLECTIONS {
        if let Some(older) = other.metadata.get(key) {
            let mut combined = older.clone();
            if let Some(current) = winner.metadata.get(key) {
                if current == older {
                    continue;
                }
                crate::scraped::overlay(key, &mut combined, current.clone());
            }
            order_collection(&mut combined);
            result.metadata.insert(key.into(), combined);
        }
    }
    result
}

pub(super) struct Merge {
    pub items: Vec<CatalogItem>,
    pub redirects: HashMap<Uuid, Uuid>,
    pub new_redirects: BTreeMap<Uuid, Uuid>,
}
pub(super) fn merge(
    local: &CatalogDocument,
    remote: &CatalogDocument,
    mut redirects: HashMap<Uuid, Uuid>,
) -> Result<Merge> {
    for key in redirects.keys() {
        destination(*key, &redirects)?;
    }
    let local_names = unique_titles(local)?;
    let remote_names = unique_titles(remote)?;
    let mut pairs = Vec::new();
    for (name, left) in local_names {
        let Some(left) = left else { continue };
        if let Some(Some(right)) = remote_names.get(&name) {
            let year = |i: &CatalogItem| {
                i.metadata
                    .get("releaseYear")
                    .and_then(Value::as_i64)
                    .or_else(|| {
                        i.metadata
                            .get("releaseDate")?
                            .as_str()?
                            .trim()
                            .get(..4)?
                            .parse()
                            .ok()
                    })
            };
            if year(left).zip(year(right)).is_some_and(|(a, b)| a != b) {
                continue;
            }
            pairs.push((left.id()?, right.id()?));
        }
    }
    pairs.sort_unstable();
    let mut new_redirects = BTreeMap::new();
    for (a, b) in pairs {
        let a = destination(a, &redirects)?;
        let b = destination(b, &redirects)?;
        if a != b {
            let winner = a.min(b);
            let loser = a.max(b);
            redirects.insert(loser, winner);
            new_redirects.insert(loser, winner);
        }
    }
    let mut items: Vec<CatalogItem> = vec![];
    let mut positions = HashMap::<Uuid, usize>::new();
    for item in remote.items.iter().chain(&local.items) {
        let id = item.id()?;
        let target = destination(id, &redirects)?;
        // A redirect supersedes the old record's tombstone, not the surviving title.
        if id != target && item.deleted {
            continue;
        }
        let mut current = item.clone();
        if id != target {
            current.sync_id = Some(target);
        }
        if let Some(&i) = positions.get(&target) {
            items[i] = fold(&items[i], &current);
        } else {
            positions.insert(target, items.len());
            items.push(current);
        }
    }
    Ok(Merge {
        items,
        redirects,
        new_redirects,
    })
}

pub(super) fn replace_reference(
    reference: &mut Value,
    aliases: &HashMap<Uuid, Uuid>,
    items: &HashMap<Uuid, &CatalogItem>,
) -> Result<()> {
    let Some(id) = reference["itemId"].as_str().and_then(|s| s.parse().ok()) else {
        return Ok(());
    };
    if !aliases.contains_key(&id) {
        return Ok(());
    }
    let dest = destination(id, aliases)?;
    if let Some(title) = items.get(&dest) {
        reference["itemId"] = json!(dest);
        reference["mediaType"] = json!(title.media_type);
        reference["subtype"] = json!(title.subtype);
        reference["name"] = json!(title.name);
    }
    Ok(())
}
