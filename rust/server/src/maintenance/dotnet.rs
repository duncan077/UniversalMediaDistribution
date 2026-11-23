use super::{plain, snapshot, tree, verified, Result};
use base64::{engine::general_purpose::STANDARD, Engine};
use rusqlite::{functions::FunctionFlags, params, Connection};
use std::{fs, path::Path};
use umd_core::{CatalogItem, CatalogStore};
use umd_server::{AccountStore, Admin};
use uuid::Uuid;

fn attached(dir: &Path, name: &str) -> Result<Connection> {
    let db = Connection::open(dir.join(name))?;
    db.execute_batch("PRAGMA foreign_keys=ON; PRAGMA trusted_schema=OFF")?;
    db.execute(
        "ATTACH DATABASE ?1 AS old",
        [dir.join("source.db").to_str().ok_or("Ruta inválida")?],
    )?;
    let flags = FunctionFlags::SQLITE_UTF8 | FunctionFlags::SQLITE_DETERMINISTIC;
    db.create_scalar_function("guid", 1, flags, |ctx| {
        Uuid::parse_str(&ctx.get::<String>(0)?)
            .map(|id| id.to_string())
            .map_err(|e| rusqlite::Error::UserFunctionError(Box::new(e)))
    })?;
    db.create_scalar_function("unbase64", 1, flags, |ctx| {
        STANDARD
            .decode(ctx.get::<String>(0)?)
            .map_err(|e| rusqlite::Error::UserFunctionError(Box::new(e)))
    })?;
    Ok(db)
}
fn catalog(dir: &Path) -> Result<()> {
    drop(CatalogStore::open(dir.join("catalog.db"))?);
    let mut db = attached(dir, "catalog.db")?;
    let tx = db.transaction()?;
    {
        let mut stmt = tx.prepare("SELECT guid(id),payload FROM old.catalog_items")?;
        let mut items = stmt.query([])?;
        let mut insert=tx.prepare("INSERT INTO rust_catalog(id,name_key,media_type,search_text,payload) VALUES(?,?,?,?,?)")?;
        while let Some(row) = items.next()? {
            let id: String = row.get(0)?;
            let mut item: CatalogItem = serde_json::from_str(&row.get::<_, String>(1)?)?;
            let id = Uuid::parse_str(&id)?;
            if item.sync_id.is_some_and(|v| v != id) || item.deleted {
                return Err("Identidad de catálogo inconsistente.".into());
            }
            item.sync_id = Some(id);
            item.validate()?;
            insert.execute(params![
                id.to_string(),
                item.key(),
                item.media_type.to_ascii_lowercase(),
                item.search_text(),
                serde_json::to_string(&item)?
            ])?;
        }
    }
    tx.execute_batch("UPDATE rust_state SET (revision,updated_at,source_label)=(SELECT revision,updated_at,source_label FROM old.catalog_state WHERE singleton=1) WHERE EXISTS(SELECT 1 FROM old.catalog_state WHERE singleton=1);
    INSERT INTO rust_catalog_summary(id,summary) SELECT id,json_remove(payload,'$.units','$.seasons','$.sources','$.distributionRights') FROM rust_catalog WHERE id NOT IN (SELECT id FROM rust_catalog_summary);
    CREATE TABLE IF NOT EXISTS rust_crawl_bindings(provider TEXT NOT NULL,external TEXT NOT NULL,item_id TEXT NOT NULL REFERENCES rust_catalog(id) ON DELETE CASCADE,PRIMARY KEY(provider,external));
    INSERT INTO rust_crawl_bindings SELECT provider_id,external_id,guid(item_id) FROM old.crawler_titles WHERE item_id IS NOT NULL;")?;
    tx.commit()?;
    verified(&db)
}
fn accounts(dir: &Path) -> Result<()> {
    drop(AccountStore::open(dir.join("accounts.db"))?);
    let mut db = attached(dir, "accounts.db")?;
    let tx = db.transaction()?;
    tx.execute_batch(include_str!("accounts.sql"))?;
    if tx.prepare("SELECT 1 FROM accounts WHERE iterations<100000 OR iterations>2147483647 OR length(hash)!=32 OR length(salt)=0")?.exists([])?{
        return Err("Credencial .NET inválida.".into());
    }
    tx.commit()?;
    verified(&db)
}
fn crawler(dir: &Path) -> Result<()> {
    drop(Admin::open(dir)?);
    let mut db = attached(dir, "crawler.db")?;
    let tx = db.transaction()?;
    tx.execute_batch("INSERT INTO providers(id,cursor,complete,discovery,updates,success,failures,kind,retry)
       SELECT provider_id,cursor,backfill_complete,unixepoch(last_discovery),unixepoch(last_updates),unixepoch(last_success),failures,failure_kind,unixepoch(retry_at) FROM old.crawler_providers WHERE true
       ON CONFLICT(id) DO UPDATE SET cursor=excluded.cursor,complete=excluded.complete,discovery=excluded.discovery,updates=excluded.updates,success=excluded.success,failures=excluded.failures,kind=excluded.kind,retry=excluded.retry;
       UPDATE providers SET interval=(SELECT interval_seconds FROM old.crawler_provider_settings WHERE provider_id=id) WHERE id IN (SELECT provider_id FROM old.crawler_provider_settings);
       UPDATE providers SET attempt=(SELECT unixepoch(last_attempt) FROM old.crawler_provider_turns WHERE provider_id=id) WHERE id IN (SELECT provider_id FROM old.crawler_provider_turns);
       INSERT INTO titles(provider,external,preview,due,success,failures) SELECT provider_id,external_id,preview,unixepoch(due_at),unixepoch(last_success),failures FROM old.crawler_titles;
       INSERT INTO runs SELECT id,unixepoch(started_at),coalesce(unixepoch(finished_at),unixepoch()),CASE WHEN finished_at IS NULL THEN 'interrupted' ELSE status END,titles_updated,failures FROM old.crawler_runs;
       INSERT INTO events SELECT id,unixepoch(occurred_at),level,provider_id,run_id,stage,status,message,http_status FROM old.server_events ORDER BY id;")?;
    tx.commit()?;
    verified(&db)
}
fn releases(source: &Path, dir: &Path) -> Result<()> {
    let packages = source.join("packages");
    if packages.try_exists()? {
        tree(&packages, &dir.join("packages"))?;
    }
    let db = super::readonly(&dir.join("source.db"))?;
    let mut stmt =
        db.prepare("SELECT key,payload FROM server_documents WHERE key GLOB 'releases/*'")?;
    let mut rows = stmt.query([])?;
    while let Some(row) = rows.next()? {
        let key: String = row.get(0)?;
        let parts: Vec<_> = key.split('/').collect();
        if parts.len() != 4
            || parts[3] != "latest.json"
            || !parts[1..3].iter().all(|p| {
                !p.is_empty()
                    && p.len() <= 64
                    && !p.ends_with('.')
                    && p.bytes()
                        .all(|b| b.is_ascii_alphanumeric() || matches!(b, b'-' | b'.'))
            })
        {
            return Err("Ruta de manifiesto .NET inválida.".into());
        }
        let path = dir.join(key);
        fs::create_dir_all(path.parent().unwrap())?;
        fs::write(path, row.get::<_, String>(1)?)?;
    }
    Ok(())
}
pub(super) fn convert(source: &Path, dir: &Path) -> Result<()> {
    let legacy = source.join("server.db");
    plain(&legacy)?;
    let copy = dir.join("source.db");
    snapshot(&legacy, &copy)?;
    if super::readonly(&copy)?.query_row("SELECT max(version) FROM server_schema", [], |r| {
        r.get::<_, i64>(0)
    })? != 2
    {
        return Err("Se requiere una base del servidor .NET con esquema 2.".into());
    }
    // These diagnostics tables were added without bumping the .NET schema version.
    // Supply empty ones only in the private snapshot of an older installation.
    Connection::open(&copy)?.execute_batch("CREATE TABLE IF NOT EXISTS crawler_provider_settings(provider_id TEXT PRIMARY KEY,interval_seconds INTEGER,updated_at TEXT);
        CREATE TABLE IF NOT EXISTS server_events(id INTEGER PRIMARY KEY,occurred_at TEXT,level TEXT,source TEXT,provider_id TEXT,run_id TEXT,stage TEXT,status TEXT,message TEXT,http_status INTEGER);")?;
    catalog(dir)?;
    accounts(dir)?;
    crawler(dir)?;
    releases(source, dir)?;
    fs::remove_file(copy)?;
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;
    #[test]
    fn converts_legacy_identity_settings_and_published_list_snapshot() -> Result<()> {
        let dir = tempfile::tempdir()?;
        let db = Connection::open(dir.path().join("source.db"))?;
        db.execute_batch(
            "CREATE TABLE server_documents(key TEXT PRIMARY KEY,parent TEXT,payload TEXT);",
        )?;
        let id = Uuid::new_v4();
        let compact = id.simple();
        let list = Uuid::new_v4();
        let stamp = "2026-01-01T00:00:00Z";
        let catalog =
            json!({"format":"universal-media-distribution.catalog","version":1,"items":[]});
        for (key, payload) in [
            (
                format!("accounts/by-id/{compact}.json"),
                json!({"accountId":id,"username":"tester","displayName":"User","passwordSalt":STANDARD.encode([7;16]),"passwordHash":STANDARD.encode([42;32]),"passwordIterations":310000,"createdAt":stamp}),
            ),
            (
                format!("user-sync/{compact}/latest.json"),
                json!({"revision":19,"updatedAt":stamp,"updatedByDeviceId":"local","document":{"catalog":catalog,"lists":[]}}),
            ),
            (
                format!("friends/visibility/{compact}.json"),
                json!({"shareProgress":false,"discoverableByUsername":false,"hiddenItemKeys":["book.a"]}),
            ),
            (
                format!("friends/activity/{compact}.json"),
                json!({"accountId":id,"entries":[{"itemKey":"book.a","hasProgress":true}]}),
            ),
            (
                "shared-lists/public/abc.json".into(),
                json!({"shareId":"abc","sourceListId":list,"ownerAccountId":id,"name":"Prueba","createdAt":stamp,"updatedAt":stamp,"catalog":catalog}),
            ),
        ] {
            db.execute(
                "INSERT INTO server_documents VALUES(?,?,?)",
                params![key, key.rsplit_once('/').unwrap().0, payload.to_string()],
            )?;
        }
        drop(db);
        accounts(dir.path())?;
        let migrated = Connection::open(dir.path().join("accounts.db"))?;
        assert_eq!(
            migrated.query_row(
                "SELECT iterations FROM accounts WHERE id=?",
                [id.to_string()],
                |r| r.get::<_, i64>(0)
            )?,
            310000
        );
        assert_eq!(
            migrated.query_row(
                "SELECT revision FROM snapshots WHERE account_id=?",
                [id.to_string()],
                |r| r.get::<_, i64>(0)
            )?,
            19
        );
        assert!(!migrated
            .prepare("SELECT 1 FROM friend_activity")?
            .exists([])?);
        assert_eq!(
            serde_json::from_str::<serde_json::Value>(&migrated.query_row(
                "SELECT document FROM migrated_list_catalogs WHERE share_id='abc'",
                [],
                |r| r.get::<_, String>(0)
            )?)?,
            catalog
        );
        Ok(())
    }
}
