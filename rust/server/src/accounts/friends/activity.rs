use super::{account, bad, pair, visibility_of, ApiError, ApiResult, ServerState};
use axum::{
    extract::{Path, State},
    http::HeaderMap,
    response::IntoResponse,
    Json,
};
use rusqlite::{params, Connection, OptionalExtension};
use serde::Deserialize;
use serde_json::{json, Value};
use std::collections::{BTreeMap, HashSet};
use uuid::Uuid;

fn rows<'a>(doc: &'a Value, field: &str) -> impl Iterator<Item = &'a Value> {
    doc[field].as_array().into_iter().flatten()
}
fn key(item: &Value) -> Option<String> {
    Some(umd_core::friend_item_key(
        item["mediaType"].as_str()?,
        item["name"].as_str()?,
    ))
}
fn time(row: &Value) -> Option<chrono::DateTime<chrono::FixedOffset>> {
    chrono::DateTime::parse_from_rfc3339(row["updatedAt"].as_str()?).ok()
}
fn number(row: &Value) -> Option<f64> {
    row["displayNumber"]
        .as_str()?
        .trim()
        .parse::<f64>()
        .ok()
        .filter(|v| v.is_finite())
}
fn further(candidate: &Value, existing: &Value) -> bool {
    let season = |r: &Value| r["seasonNumber"].as_i64().unwrap_or(0);
    match season(candidate).cmp(&season(existing)) {
        std::cmp::Ordering::Equal => match (number(candidate), number(existing)) {
            (Some(a), Some(b)) => a > b,
            _ => time(candidate) > time(existing),
        },
        order => order.is_gt(),
    }
}
struct Progress {
    titles: BTreeMap<String, Value>,
    favorites: HashSet<String>,
}
fn reduce(doc: &Value) -> Progress {
    let mut subtypes = BTreeMap::<String, Option<&str>>::new();
    let mut by_id = BTreeMap::<Uuid, &str>::new();
    for item in rows(&doc["catalog"], "items").filter(|item| item["deleted"] != true) {
        let (Some(k), Some(subtype)) = (key(item), item["subtype"].as_str()) else {
            continue;
        };
        if let Some(id) = item["syncId"]
            .as_str()
            .and_then(|id| Uuid::parse_str(id).ok())
        {
            by_id.insert(id, subtype);
        }
        subtypes
            .entry(k)
            .and_modify(|previous| {
                if *previous != Some(subtype) {
                    *previous = None;
                }
            })
            .or_insert(Some(subtype));
    }
    let identity = |item: &Value| {
        let base = key(item)?;
        let media = item["mediaType"].as_str()?;
        let name = item["name"].as_str()?;
        let subtype = item["subtype"]
            .as_str()
            .or_else(|| {
                item["itemId"]
                    .as_str()
                    .and_then(|id| Uuid::parse_str(id).ok())
                    .and_then(|id| by_id.get(&id).copied())
            })
            .or_else(|| subtypes.get(&base).copied().flatten());
        Some((
            umd_core::friend_scoped_item_key(media, name, subtype),
            base,
            subtype.map(str::to_owned),
        ))
    };
    let favorites = rows(doc, "favoriteItemStates")
        .filter(|r| r["isFavorite"] == true)
        .filter_map(|r| identity(&r["item"]).map(|(key, _, _)| key))
        .collect::<HashSet<_>>();
    let mut titles = BTreeMap::new();
    for row in rows(doc, "progress") {
        if row["isCompleted"] != true && row["position"].as_f64().unwrap_or(0.0) <= 0.0 {
            continue;
        }
        let content = &row["content"];
        let item = &content["item"];
        let Some((k, legacy, subtype)) = identity(item) else {
            continue;
        };
        let entry = json!({"itemKey":k,"legacyItemKey":legacy,"mediaType":item["mediaType"],"title":item["name"],"unitKind":content["unitKind"],"displayNumber":content["displayNumber"],
            "seasonNumber":content["seasonNumber"],"isCompleted":row["isCompleted"],"updatedAt":row["updatedAt"],
            "isFavorite":favorites.contains(&k),"subtype":subtype});
        if titles
            .get(&k)
            .is_none_or(|previous| further(&entry, previous))
        {
            titles.insert(k, entry);
        }
    }
    Progress { titles, favorites }
}
pub(super) fn snapshot(db: &Connection, id: &str) -> Result<Option<Value>, ApiError> {
    let raw: Option<String> = db
        .query_row(
            "SELECT document FROM snapshots WHERE account_id=?1",
            [id],
            |r| r.get(0),
        )
        .optional()?
        .flatten();
    Ok(raw
        .map(|s| serde_json::from_str(&s))
        .transpose()
        .map_err(umd_core::Error::from)?)
}
pub(in crate::accounts) fn refresh(
    db: &Connection,
    owner: &str,
    doc: Option<&Value>,
) -> Result<(), ApiError> {
    db.execute("DELETE FROM friend_activity WHERE owner=?1", [owner])?;
    db.execute("DELETE FROM account_activity WHERE owner=?1", [owner])?;
    db.execute(
        "INSERT OR IGNORE INTO account_activity_state VALUES(?1)",
        [owner],
    )?;
    let Some(doc) = doc else { return Ok(()) };
    let visibility = visibility_of(db, owner)?;
    let hidden = visibility
        .hidden_item_keys
        .into_iter()
        .collect::<HashSet<_>>();
    let Progress {
        titles,
        mut favorites,
    } = reduce(doc);
    let mut shared = db.prepare("INSERT INTO friend_activity VALUES(?1,?2,?3)")?;
    let mut personal = db.prepare("INSERT INTO account_activity VALUES(?1,?2,?3,?4)")?;
    for (key, entry) in titles {
        let data = entry.to_string();
        personal.execute(params![owner, key, data, favorites.remove(&key)])?;
        if visibility.share_progress
            && !hidden.contains(&key)
            && entry["legacyItemKey"]
                .as_str()
                .is_none_or(|legacy| !hidden.contains(legacy))
        {
            shared.execute(params![owner, key, data])?;
        }
    }
    for key in favorites {
        personal.execute(params![owner, key, Option::<String>::None, true])?;
    }
    Ok(())
}

fn own_progress(db: &Connection, owner: &str) -> Result<Progress, ApiError> {
    let indexed: bool = db.query_row(
        "SELECT EXISTS(SELECT 1 FROM account_activity_state WHERE owner=?1)",
        [owner],
        |row| row.get(0),
    )?;
    if !indexed {
        return Ok(snapshot(db, owner)?.as_ref().map_or_else(
            || Progress {
                titles: BTreeMap::new(),
                favorites: HashSet::new(),
            },
            reduce,
        ));
    }
    let mut statement =
        db.prepare("SELECT item_key, entry, favorite FROM account_activity WHERE owner=?1")?;
    let mut rows = statement.query([owner])?;
    let mut titles = BTreeMap::new();
    let mut favorites = HashSet::new();
    while let Some(row) = rows.next()? {
        let key: String = row.get(0)?;
        if row.get::<_, bool>(2)? {
            favorites.insert(key.clone());
        }
        if let Some(entry) = row.get::<_, Option<String>>(1)? {
            titles.insert(
                key,
                serde_json::from_str(&entry).map_err(umd_core::Error::from)?,
            );
        }
    }
    Ok(Progress { titles, favorites })
}
fn can_read(db: &Connection, me: &str, other: &str) -> Result<bool, ApiError> {
    let (a, b) = pair(me, other);
    Ok(db.query_row("SELECT EXISTS(SELECT 1 FROM friend_pairs p JOIN friend_settings v ON v.owner=?3
        JOIN snapshots s ON s.account_id=v.owner WHERE p.a=?1 AND p.b=?2 AND p.accepted AND v.sharing AND s.document IS NOT NULL
        AND CASE WHEN p.a=?3 THEN p.share_a ELSE p.share_b END)",params![a,b,other],|r|r.get(0))?)
}
fn compare(db: &Connection, me: &str, other: &str, only_shared: bool) -> Result<Value, ApiError> {
    let mut shared = Vec::new();
    let mut common = Vec::new();
    let mut theirs = Vec::new();
    let mut mine = Vec::new();
    if can_read(db, me, other)? {
        if db.query_row(
            "SELECT EXISTS(SELECT 1 FROM snapshots WHERE account_id=?1 AND document IS NOT NULL)",
            [me],
            |row| row.get::<_, bool>(0),
        )? {
            let mut viewer = own_progress(db, me)?;
            let mut stmt = db.prepare(
                "SELECT item_key,entry FROM friend_activity WHERE owner=?1 ORDER BY item_key",
            )?;
            let mut rows = stmt.query([other])?;
            while let Some(row) = rows.next()? {
                let key: String = row.get(0)?;
                let their: Value = serde_json::from_str(&row.get::<_, String>(1)?)
                    .map_err(umd_core::Error::from)?;
                let own = viewer.titles.remove(&key);
                let title = own.as_ref().map_or(&their["title"], |r| &r["title"]);
                let mut v = json!({"itemKey":key,"title":title,"mediaType":their["mediaType"],"unitKind":their["unitKind"],"displayNumber":their["displayNumber"],
                    "seasonNumber":their["seasonNumber"],"isCompleted":their["isCompleted"],"updatedAt":their["updatedAt"]});
                if only_shared {
                    if own.is_some() {
                        shared.push(v);
                    }
                    continue;
                }
                v["viewerFavorite"] = json!(viewer.favorites.contains(&key));
                v["friendFavorite"] = their["isFavorite"].clone();
                v["friendSeen"] = json!(true);
                v["viewerSeen"] = json!(own.is_some());
                v["subtype"] = their["subtype"].clone();
                if own.is_some() {
                    common.push(v);
                } else {
                    theirs.push(v);
                }
            }
            if !only_shared {
                for (key, own) in viewer.titles {
                    mine.push(json!({"itemKey":key,"title":own["title"],"mediaType":own["mediaType"],"viewerFavorite":own["isFavorite"],"friendFavorite":false,
                    "viewerSeen":true,"friendSeen":false,"unitKind":own["unitKind"],"displayNumber":own["displayNumber"],"seasonNumber":own["seasonNumber"],"isCompleted":own["isCompleted"],
                    "updatedAt":own["updatedAt"],"subtype":own["subtype"]}));
                }
            }
        }
    }
    for group in [&mut shared, &mut common, &mut theirs, &mut mine] {
        group.sort_by_cached_key(|v| std::cmp::Reverse(time(v)));
    }
    if only_shared {
        return Ok(json!({"titles":shared}));
    }
    for group in [&mut common, &mut theirs, &mut mine] {
        group.truncate(300);
    }
    Ok(json!({"inCommon":common,"friendOnly":theirs,"viewerOnly":mine}))
}
pub(super) async fn shared(
    State(s): State<ServerState>,
    h: HeaderMap,
    Path(id): Path<Uuid>,
) -> ApiResult {
    account(s, h, false, move |db, me| {
        Ok(Json(compare(db, me, &id.to_string(), true)?).into_response())
    })
    .await
}
pub(super) async fn comparison(
    State(s): State<ServerState>,
    h: HeaderMap,
    Path(id): Path<Uuid>,
) -> ApiResult {
    account(s, h, false, move |db, me| {
        Ok(Json(compare(db, me, &id.to_string(), false)?).into_response())
    })
    .await
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
pub(super) struct Lookup {
    item_keys: Vec<String>,
}
pub(super) async fn lookup(
    State(s): State<ServerState>,
    h: HeaderMap,
    Json(input): Json<Lookup>,
) -> ApiResult {
    account(s, h, false, move |db, me| lookup_activity(db, me, input)).await
}
fn lookup_activity(db: &Connection, me: &str, input: Lookup) -> super::TaskResult {
    if input.item_keys.len() > 200 {
        return Err(bad("Podés consultar hasta 200 títulos por vez."));
    }
    let keys = serde_json::to_string(&input.item_keys.iter().map(|s| s.trim()).collect::<Vec<_>>())
        .map_err(umd_core::Error::from)?;
    let mut stmt=db.prepare("SELECT u.id,u.username,u.display_name,f.entry FROM friend_pairs p
            JOIN accounts u ON u.id=CASE WHEN p.a=?1 THEN p.b ELSE p.a END
            JOIN friend_settings v ON v.owner=u.id AND v.sharing
            JOIN friend_activity f ON f.owner=u.id AND (f.item_key IN (SELECT value FROM json_each(?2))
                OR f.entry->>'legacyItemKey' IN (SELECT value FROM json_each(?2)))
            WHERE (p.a=?1 OR p.b=?1) AND p.accepted AND CASE WHEN p.a=?1 THEN p.share_b ELSE p.share_a END ORDER BY u.display_name,f.item_key")?;
    let mut rows = stmt.query(params![me, keys])?;
    let mut matches = Vec::new();
    while let Some(r) = rows.next()? {
        let v: Value =
            serde_json::from_str(&r.get::<_, String>(3)?).map_err(umd_core::Error::from)?;
        matches.push(json!({"accountId":r.get::<_,String>(0)?,"username":r.get::<_,String>(1)?,"displayName":r.get::<_,String>(2)?,
                "itemKey":v["itemKey"],"subtype":v["subtype"],"unitKind":v["unitKind"],"displayNumber":v["displayNumber"],"seasonNumber":v["seasonNumber"],
                "isCompleted":v["isCompleted"],"updatedAt":v["updatedAt"]}));
    }
    Ok(Json(json!({"matches":matches})).into_response())
}

pub(in crate::accounts) fn rebuild_legacy_books(db: &Connection) -> Result<(), ApiError> {
    let tx = db.unchecked_transaction()?;
    let owners = {
        let mut stmt = tx.prepare("SELECT DISTINCT owner FROM friend_activity WHERE item_key LIKE 'book:%' AND entry->>'legacyItemKey' IS NULL
            UNION SELECT DISTINCT owner FROM account_activity WHERE item_key LIKE 'book:%' AND entry IS NOT NULL AND entry->>'legacyItemKey' IS NULL")?;
        let found = stmt
            .query_map([], |row| row.get::<_, String>(0))?
            .collect::<rusqlite::Result<Vec<_>>>()?;
        found
    };
    for owner in owners {
        if let Some(doc) = snapshot(&tx, &owner)? {
            refresh(&tx, &owner, Some(&doc))?;
        }
    }
    tx.commit()?;
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn activity_separates_book_adaptations_with_the_same_name() {
        let work = |subtype| json!({"name":"Origen","mediaType":"book","subtype":subtype});
        let progress = |subtype, number| {
            json!({
                "position":37,"content":{"item":work(subtype),"unitKind":"chapter","displayNumber":number}
            })
        };
        let data = reduce(&json!({
            "progress":[progress("manga","8"), progress("book.novel","12")],
            "favoriteItemStates":[{"item":work("book.manga"),"isFavorite":true}]
        }));
        assert_eq!(data.titles.len(), 2);
        let manga = &data.titles["book.manga:ORIGEN"];
        let novel = &data.titles["book.novel:ORIGEN"];
        assert_eq!(manga["displayNumber"], "8");
        assert_eq!(novel["displayNumber"], "12");
        assert_eq!(manga["isFavorite"], true);
        assert_eq!(novel["isFavorite"], false);
        assert_eq!(manga["legacyItemKey"], novel["legacyItemKey"]);
    }
}
