use crate::{
    check_library_backup, friend_item_key, normalize, user_state, CatalogDocument, CatalogItem,
    CatalogStore, Error, Result,
};
use base64::{engine::general_purpose::STANDARD, Engine};
use rusqlite::{
    functions::FunctionFlags, params, Connection, OpenFlags, OptionalExtension, TransactionBehavior,
};
use serde_json::{json, Value};
use std::{collections::HashMap, path::Path};
use uuid::Uuid;

const DEFAULT_PROFILE: &str = "00000000-0000-0000-0000-000000000001";
fn invalid(message: &str) -> Error {
    Error::Invalid(message.into())
}

/// Validate a Rust backup in place, or translate a .NET backup into a separate,
/// caller-owned empty file. Only the returned choice may be staged for restore.
pub fn prepare_library_restore(source: &Path, converted: &Path) -> Result<bool> {
    let db = Connection::open_with_flags(source, OpenFlags::SQLITE_OPEN_READ_ONLY)?;
    db.execute_batch("PRAGMA trusted_schema=OFF")?;
    if !db.query_row(
        "SELECT EXISTS(SELECT 1 FROM sqlite_schema WHERE type='table' AND name='schema_info')",
        [],
        |r| r.get::<_, bool>(0),
    )? {
        check_library_backup(source)?;
        return Ok(false);
    }
    let app_id: Option<String> = db
        .query_row(
            "SELECT value FROM schema_info WHERE key='application_id'",
            [],
            |r| r.get(0),
        )
        .optional()?;
    if db.query_row("SELECT EXISTS(SELECT 1 FROM pragma_table_list WHERE schema='main' AND type IN ('view','virtual','shadow'))", [], |r| r.get::<_,bool>(0))?
        || app_id.as_deref() != Some("universal-media-distribution") {
        return Err(invalid("El archivo no es una biblioteca SQLite de UMD."));
    }
    let version: i32 = db.query_row("PRAGMA user_version", [], |r| r.get(0))?;
    if !(1..=15).contains(&version) {
        return Err(invalid(
            "Versión del respaldo SQLite de .NET no compatible.",
        ));
    }
    if db.query_row("PRAGMA integrity_check(1)", [], |r| r.get::<_, String>(0))? != "ok"
        || db.prepare("PRAGMA foreign_key_check")?.exists([])?
    {
        return Err(invalid(
            "El respaldo de .NET está dañado o tiene referencias incompletas.",
        ));
    }
    if !std::fs::metadata(converted).is_ok_and(|m| m.is_file() && m.len() == 0) {
        return Err(invalid("La conversión necesita un archivo temporal vacío."));
    }
    let db = db.unchecked_transaction()?;
    compatibility_views(&db, version)?;
    db.create_scalar_function(
        "umd_base64",
        1,
        FunctionFlags::SQLITE_UTF8
            | FunctionFlags::SQLITE_DETERMINISTIC
            | FunctionFlags::SQLITE_INNOCUOUS,
        |ctx| {
            Ok(ctx
                .get::<Option<Vec<u8>>>(0)?
                .map(|bytes| STANDARD.encode(bytes)))
        },
    )?;
    db.execute_batch(include_str!("catalog.sql"))?;
    let catalog = catalog(&db, version)?;
    db.execute_batch(include_str!("personal.sql"))?;
    {
        let store = CatalogStore::open(converted)?;
        let mut target = store.0.lock().map_err(|_| Error::Lock)?;
        let tx = target.transaction_with_behavior(TransactionBehavior::Immediate)?;
        CatalogStore::import_into(&tx, &catalog, true)?;
        profiles(&db, &tx, &catalog)?;
        tx.commit()?;
    }
    check_library_backup(converted)?;
    Ok(true)
}

// Old clients used a single profile and acquired columns over several releases.
// These read projections expose their defaults without modifying the input file.
fn compatibility_views(db: &Connection, version: i32) -> Result<()> {
    if version < 2 {
        db.execute_batch(
            "CREATE TEMP TABLE application_settings(key TEXT PRIMARY KEY,value TEXT)",
        )?;
    }
    if version < 14 {
        let mut defaults = vec![
            "NULL AS sync_clock",
            "NULL AS sync_device",
            "NULL AS sync_operation",
            "NULL AS sync_modified",
        ];
        if version < 13 {
            defaults.push("NULL AS sync_id");
        }
        if version < 9 {
            defaults.push("CASE WHEN CAST(substr(release_date,1,4) AS INTEGER) BETWEEN 1800 AND 2200 THEN CAST(substr(release_date,1,4) AS INTEGER) END AS release_year");
        }
        db.execute_batch(&format!(
            "CREATE TEMP VIEW catalog_items AS SELECT *,{} FROM main.catalog_items",
            defaults.join(",")
        ))?;
    }
    if version < 13 {
        db.execute_batch("CREATE TEMP TABLE catalog_redirects(from_id,to_id,clock,device_id,operation_id,modified_at)")?;
    }
    if version < 3 {
        db.execute_batch("CREATE TEMP VIEW catalog_alternative_names AS SELECT * FROM main.catalog_aliases;
            CREATE TEMP TABLE categories(id,name); CREATE TEMP TABLE catalog_item_categories(catalog_item_id,category_id)")?;
    }
    if version < 4 {
        db.execute_batch(
            "CREATE TEMP VIEW tracker_endpoints AS SELECT *, NULL AS credential_key FROM main.tracker_endpoints",
        )?;
    }
    if version < 5 {
        db.execute_batch("CREATE TEMP VIEW torrent_packages AS SELECT *, 'und' AS language FROM main.torrent_packages;
            CREATE TEMP VIEW contributors AS SELECT *, NULL AS url FROM main.contributors;
            CREATE TEMP TABLE favorite_contributors(profile_id,contributor_key,added_at)")?;
    }
    if version < 6 {
        db.execute_batch(
            "CREATE TEMP TABLE user_media_lists(profile_id,id,name,created_at,updated_at);
            CREATE TEMP TABLE user_media_list_items(list_id,catalog_item_id,added_at)",
        )?;
    }
    if version < 8 {
        db.execute_batch("CREATE TEMP TABLE user_sync_records(profile_id,record_kind,record_key,sync_clock,sync_device_id,
            sync_operation_id,modified_at,is_deleted,item_media_type,item_name)")?;
    }
    if version < 11 {
        db.execute_batch("CREATE TEMP TABLE profiles(id,name,sort_order,created_at,last_used_at)")?;
        db.execute(
            "INSERT INTO profiles VALUES(?1,'Principal',0,'',NULL)",
            [DEFAULT_PROFILE],
        )?;
        for (table, since) in [
            ("playback_progress", 1),
            ("reader_preferences", 1),
            ("library_entries", 1),
            ("favorite_contributors", 5),
            ("user_media_lists", 6),
            ("user_sync_records", 8),
        ] {
            if version >= since {
                db.execute_batch(&format!("CREATE TEMP VIEW {table} AS SELECT *, '{DEFAULT_PROFILE}' AS profile_id FROM main.{table}"))?;
            }
        }
    }
    Ok(())
}

fn catalog(db: &Connection, version: i32) -> Result<CatalogDocument> {
    db.execute_batch("CREATE TEMP TABLE identities(local_id TEXT PRIMARY KEY,record_key TEXT NOT NULL,identity TEXT NOT NULL);
        CREATE INDEX temp.identity_key ON identities(record_key)")?;
    let mut insert = db.prepare("INSERT INTO identities VALUES(?1,?2,?3)")?;
    let mut select = db.prepare("SELECT c.local_id,c.payload,s.value FROM catalog_export c
        LEFT JOIN application_settings s ON s.key='api_title_binding:' || lower(replace(c.local_id,'-',''))")?;
    let mut rows = select.query([])?;
    let mut items = Vec::new();
    while let Some(row) = rows.next()? {
        let mut item: CatalogItem = serde_json::from_str(&row.get::<_, String>(1)?)?;
        if let Some(raw) = row.get::<_, Option<String>>(2)? {
            // The legacy client ignores malformed optional bindings. Read only
            // external title IDs, never provider settings or session information.
            if let Ok(bindings) = serde_json::from_str::<serde_json::Map<String, Value>>(&raw) {
                let providers = bindings
                    .into_iter()
                    .filter_map(|(site, b)| {
                        let external = b["externalId"]
                            .as_str()
                            .or_else(|| b["ExternalId"].as_str())?;
                        (!external.trim().is_empty()).then(|| (site, json!(external)))
                    })
                    .collect::<serde_json::Map<_, _>>();
                if !providers.is_empty() {
                    item.metadata
                        .insert("onlineProviders".into(), Value::Object(providers));
                }
            }
        }
        let key = if version >= 15 && item.media_type == "book" {
            friend_item_key(&format!("book.{}", item.subtype), &item.name)
        } else {
            friend_item_key(&item.media_type, &item.name)
        };
        item.sync_id = Some(item.id()?);
        insert.execute(params![
            row.get::<_, String>(0)?,
            key,
            user_state::identity(&item)?.to_string()
        ])?;
        items.push(item);
    }
    let catalog = CatalogDocument::new(items);
    catalog.validate()?;
    Ok(catalog)
}

fn profiles(source: &Connection, target: &Connection, catalog: &CatalogDocument) -> Result<()> {
    let redirects = source.prepare("SELECT json_object('fromId',from_id,'toId',to_id,'syncVersion',
        json_object('clock',clock,'deviceId',device_id,'operationId',operation_id,'modifiedAt',modified_at)) FROM catalog_redirects")?
        .query_map([],|r|r.get::<_,String>(0))?.map(|row|Ok(serde_json::from_str::<Value>(&row?)?)).collect::<Result<Vec<_>>>()?;
    target.execute_batch("DELETE FROM rust_profile_selection; DELETE FROM rust_profiles")?;
    let mut select = source.prepare("SELECT id,name FROM profiles ORDER BY julianday(last_used_at) DESC,sort_order,created_at,name")?;
    let mut rows = select.query([])?;
    let mut active = None;
    while let Some(row) = rows.next()? {
        let raw: String = row.get(0)?;
        let id =
            Uuid::parse_str(&raw).map_err(|_| invalid("Identidad de perfil .NET inválida."))?;
        let name: String = row.get(1)?;
        active.get_or_insert(id);
        // .NET permits accented names that the Rust search normalizer considers
        // equal. Keep both original display names and separate their internal keys.
        let key = normalize(&name);
        target.execute(
            "INSERT INTO rust_profiles(id,name,name_key)
            VALUES(?1,?2,CASE WHEN EXISTS(SELECT 1 FROM rust_profiles WHERE name_key=?3)
                THEN ?3 || char(0) || ?1 ELSE ?3 END)",
            params![id.to_string(), name, key],
        )?;
        let mut doc = user_state::read_state(target, id)?;
        doc["catalogRedirects"] = json!(redirects);
        personal(source, &raw, &mut doc)?;
        user_state::validate_personal(&doc, catalog)?;
        for row in doc["favoriteItemStates"]
            .as_array()
            .into_iter()
            .flatten()
            .filter(|r| r["isFavorite"] == true)
        {
            target.execute(
                "INSERT INTO rust_favorites VALUES(?1,?2)",
                params![id.to_string(), row["item"]["itemId"].as_str()],
            )?;
        }
        user_state::save_state(target, id, &doc)?;
    }
    target.execute(
        "INSERT INTO rust_profile_selection VALUES(1,?1)",
        [active
            .ok_or_else(|| invalid("El respaldo no tiene perfiles."))?
            .to_string()],
    )?;
    Ok(())
}

fn normalize_timestamp(raw: &str) -> String {
    let trimmed = raw.trim();
    if trimmed.is_empty() {
        return crate::now();
    }
    // chrono also reads a space between the date and the time, so the value
    // is rewritten before it is tested: other readers of a backup require `T`.
    let with_t = trimmed.replace(' ', "T");
    if chrono::DateTime::parse_from_rfc3339(&with_t).is_ok() {
        return with_t;
    }
    let with_z = format!("{with_t}Z");
    if chrono::DateTime::parse_from_rfc3339(&with_z).is_ok() {
        return with_z;
    }
    if chrono::NaiveDate::parse_from_str(trimmed, "%Y-%m-%d").is_ok() {
        return format!("{trimmed}T00:00:00Z");
    }
    crate::now()
}

fn personal(db: &Connection, profile: &str, doc: &mut Value) -> Result<()> {
    let mut stmt=db.prepare("WITH entries AS (SELECT * FROM personal_rows UNION ALL SELECT * FROM removed_rows)
        SELECT e.kind,e.key,e.stamp,e.payload,CASE WHEN r.sync_clock IS NOT NULL THEN json_object('clock',r.sync_clock,
            'deviceId',r.sync_device_id,'operationId',r.sync_operation_id,'modifiedAt',r.modified_at) END
        FROM entries e LEFT JOIN user_sync_records r ON r.profile_id=e.profile_id AND r.record_kind=e.kind AND r.record_key=e.key
        WHERE e.profile_id=?1 ORDER BY CASE e.kind WHEN 'list_item' THEN 1 ELSE 0 END,e.stamp,e.key")?;
    let mut rows = stmt.query([profile])?;
    let mut lists = HashMap::<String, usize>::new();
    while let Some(row) = rows.next()? {
        let kind: String = row.get(0)?;
        let key: String = row.get(1)?;
        let mut entry: Value = serde_json::from_str(&row.get::<_, String>(3)?)?;
        let stamp: String = row.get::<_, Option<String>>(2)?.unwrap_or_default();
        // Older database defaults used SQLite UTC timestamps with a space or offset.
        entry["updatedAt"] = json!(normalize_timestamp(&stamp));
        entry["syncVersion"] = match row.get::<_, Option<String>>(4)? {
            Some(raw) => serde_json::from_str(&raw)?,
            None => crate::reconcile::legacy(
                &json!({"deviceId":"umd-dotnet"}),
                &entry,
                &format!("{profile}/{kind}/{key}"),
            )?,
        };
        let field = match kind.as_str() {
            "progress" => "progress",
            "reader_preference" => "readerPreferences",
            "favorite_item" => "favoriteItemStates",
            "favorite" => {
                if entry["isFavorite"] == true {
                    doc["favoriteContributorKeys"]
                        .as_array_mut()
                        .ok_or_else(|| invalid("Favoritos inválidos."))?
                        .push(entry["contributorKey"].clone());
                }
                "favoriteStates"
            }
            "list" => {
                let id = Uuid::parse_str(&key).map_err(|_| invalid("Lista .NET inválida."))?;
                entry["id"] = json!(id);
                let normalized_key = key.to_ascii_lowercase().replace('-', "");
                let index = doc["lists"]
                    .as_array()
                    .ok_or_else(|| invalid("Listas inválidas."))?
                    .len();
                lists.insert(normalized_key, index);
                "lists"
            }
            "list_item" => {
                let (list_id, _) = key
                    .split_once('|')
                    .ok_or_else(|| invalid("Referencia de lista .NET inválida."))?;
                let normalized_list_id = list_id.to_ascii_lowercase().replace('-', "");
                let Some(&index) = lists.get(&normalized_list_id) else {
                    if entry["isMember"] != true {
                        continue;
                    }
                    return Err(invalid("Falta una lista de la biblioteca .NET."));
                };
                let list = &mut doc["lists"][index];
                if entry["isMember"] == true {
                    list["items"]
                        .as_array_mut()
                        .ok_or_else(|| invalid("Lista inválida."))?
                        .push(entry["item"].clone());
                }
                list["itemStates"]
                    .as_array_mut()
                    .ok_or_else(|| invalid("Estado de lista inválido."))?
                    .push(entry);
                continue;
            }
            _ => return Err(invalid("Tipo de dato personal desconocido.")),
        };
        doc[field]
            .as_array_mut()
            .ok_or_else(|| invalid("Colección de respaldo inválida."))?
            .push(entry);
    }
    Ok(())
}
