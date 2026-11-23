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
    let favorites = rows(doc, "favoriteItemStates")
        .filter(|r| r["isFavorite"] == true)
        .filter_map(|r| key(&r["item"]))
        .collect::<HashSet<_>>();
    let subtypes: BTreeMap<_, _> = rows(&doc["catalog"], "items")
        .filter(|i| i["deleted"] != true)
        .filter_map(|i| Some((key(i)?, &i["subtype"])))
        .collect();
    let mut titles = BTreeMap::new();
    for row in rows(doc, "progress") {
        if row["isCompleted"] != true && row["position"].as_f64().unwrap_or(0.0) <= 0.0 {
            continue;
        }
        let content = &row["content"];
        let item = &content["item"];
        let Some(k) = key(item) else { continue };
        let entry = json!({"itemKey":k,"title":item["name"],"unitKind":content["unitKind"],"displayNumber":content["displayNumber"],
            "seasonNumber":content["seasonNumber"],"isCompleted":row["isCompleted"],"updatedAt":row["updatedAt"],
            "isFavorite":favorites.contains(&k),"subtype":subtypes.get(&k)});
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
    let Some(doc) = doc else { return Ok(()) };
    let visibility = visibility_of(db, owner)?;
    if !visibility.share_progress {
        return Ok(());
    }
    let hidden = visibility
        .hidden_item_keys
        .into_iter()
        .collect::<HashSet<_>>();
    let mut stmt = db.prepare("INSERT INTO friend_activity VALUES(?1,?2,?3)")?;
    for (key, entry) in reduce(doc).titles {
        if !hidden.contains(&key) {
            stmt.execute(params![owner, key, entry.to_string()])?;
        }
    }
    Ok(())
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
        if let Some(doc) = snapshot(db, me)? {
            let mut viewer = reduce(&doc);
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
                let mut v = json!({"itemKey":key,"title":title,"unitKind":their["unitKind"],"displayNumber":their["displayNumber"],
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
                    mine.push(json!({"itemKey":key,"title":own["title"],"viewerFavorite":own["isFavorite"],"friendFavorite":false,
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
            JOIN friend_activity f ON f.owner=u.id AND f.item_key IN (SELECT value FROM json_each(?2))
            WHERE (p.a=?1 OR p.b=?1) AND p.accepted AND CASE WHEN p.a=?1 THEN p.share_b ELSE p.share_a END ORDER BY u.display_name,f.item_key")?;
    let mut rows = stmt.query(params![me, keys])?;
    let mut matches = Vec::new();
    while let Some(r) = rows.next()? {
        let v: Value =
            serde_json::from_str(&r.get::<_, String>(3)?).map_err(umd_core::Error::from)?;
        matches.push(json!({"accountId":r.get::<_,String>(0)?,"username":r.get::<_,String>(1)?,"displayName":r.get::<_,String>(2)?,
                "itemKey":v["itemKey"],"unitKind":v["unitKind"],"displayNumber":v["displayNumber"],"seasonNumber":v["seasonNumber"],
                "isCompleted":v["isCompleted"],"updatedAt":v["updatedAt"]}));
    }
    Ok(Json(json!({"matches":matches})).into_response())
}
