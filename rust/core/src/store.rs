use crate::*;
use rusqlite::{params, Connection};
use sha2::{Digest, Sha256};
use std::{
    path::Path,
    sync::{Arc, Mutex},
    time::Duration,
};
use uuid::Uuid;

#[derive(Clone)]
pub struct CatalogStore(
    pub(crate) Arc<Mutex<Connection>>,
    pub(crate) Uuid,
    pub(crate) Arc<Mutex<Option<(i64, CatalogMetadata)>>>,
);

impl CatalogStore {
    pub fn open(path: impl AsRef<Path>) -> Result<Self> {
        let connection = Connection::open(path)?;
        Self::initialize(connection)
    }

    pub fn in_memory() -> Result<Self> {
        Self::initialize(Connection::open_in_memory()?)
    }

    /// A separate reader can use the last committed WAL snapshot while the
    /// account or an import is updating the library on its write connection.
    pub fn open_read_only(path: impl AsRef<Path>) -> Result<Self> {
        let connection =
            Connection::open_with_flags(path, rusqlite::OpenFlags::SQLITE_OPEN_READ_ONLY)?;
        Self::configure_connection(&connection)?;
        let profile: String = connection.query_row(
            "SELECT profile_id FROM rust_profile_selection WHERE singleton=1",
            [],
            |row| row.get(0),
        )?;
        Ok(Self(
            Arc::new(Mutex::new(connection)),
            Uuid::parse_str(&profile).map_err(|_| Error::Invalid("Perfil inválido.".into()))?,
            Arc::new(Mutex::new(None)),
        ))
    }

    fn configure_connection(connection: &Connection) -> Result<()> {
        crate::filters::register(connection)?;
        connection.busy_timeout(Duration::from_secs(10))?;
        connection.set_prepared_statement_cache_capacity(64);
        connection.execute_batch("PRAGMA temp_store=MEMORY; PRAGMA cache_size=-8000;")?;
        Ok(())
    }

    fn initialize(mut connection: Connection) -> Result<Self> {
        Self::configure_connection(&connection)?;
        // WAL with synchronous=NORMAL stays consistent after a crash or power
        // loss and no longer waits for the disk on every commit, which matters
        // for the position saved every few seconds during playback.
        connection.execute_batch(
            "PRAGMA foreign_keys=ON;
             PRAGMA journal_mode=WAL;
             PRAGMA synchronous=NORMAL;
             CREATE TABLE IF NOT EXISTS rust_catalog(
                id TEXT PRIMARY KEY, name_key TEXT NOT NULL,
                media_type TEXT NOT NULL, search_text TEXT NOT NULL, payload TEXT NOT NULL);
             CREATE TABLE IF NOT EXISTS rust_catalog_tombstones(
                id TEXT PRIMARY KEY, payload TEXT NOT NULL);
             CREATE TABLE IF NOT EXISTS rust_favorites(
                id TEXT PRIMARY KEY REFERENCES rust_catalog(id) ON DELETE CASCADE);
             CREATE TABLE IF NOT EXISTS rust_settings(key TEXT PRIMARY KEY, value TEXT NOT NULL);
             CREATE TABLE IF NOT EXISTS rust_backup_revisions(
                server TEXT NOT NULL, account TEXT NOT NULL, revision INTEGER NOT NULL,
                PRIMARY KEY(server, account));
             CREATE TABLE IF NOT EXISTS rust_state(
                singleton INTEGER PRIMARY KEY CHECK(singleton=1),
                revision INTEGER NOT NULL, updated_at TEXT NOT NULL, source_label TEXT);
             INSERT OR IGNORE INTO rust_state VALUES(1,0,'1970-01-01T00:00:00.000Z',NULL);",
        )?;
        // Previous databases enforced uniqueness by title. The public .NET
        // catalog distinguishes remakes with the same title by sync ID.
        let tx = connection.transaction_with_behavior(rusqlite::TransactionBehavior::Immediate)?;
        let unique_name: bool = tx.query_row(
            "SELECT EXISTS(SELECT 1 FROM pragma_index_list('rust_catalog') WHERE origin='u')",
            [],
            |row| row.get(0),
        )?;
        if unique_name {
            tx.execute_batch("ALTER TABLE rust_favorites RENAME TO rust_old_favorites;
                ALTER TABLE rust_catalog RENAME TO rust_old_catalog;
                CREATE TABLE rust_catalog(id TEXT PRIMARY KEY,name_key TEXT NOT NULL,
                    media_type TEXT NOT NULL,search_text TEXT NOT NULL,payload TEXT NOT NULL);
                INSERT INTO rust_catalog SELECT * FROM rust_old_catalog;
                CREATE TABLE rust_favorites(id TEXT PRIMARY KEY REFERENCES rust_catalog(id) ON DELETE CASCADE);
                INSERT INTO rust_favorites SELECT * FROM rust_old_favorites;
                DROP TABLE rust_old_favorites;
                DROP TABLE rust_old_catalog;")?;
        }
        // `rust_catalog_arrivals` keeps the order in which titles first arrived.
        // `import_into` records it; a trigger did so at first, but a library
        // copy must contain no triggers (see `check_library_backup`), so one
        // left by that build is dropped here.
        // Filters read `rust_catalog_summary`: the title's metadata without its
        // chapters, seasons and sources. A payload can carry thousands of
        // chapters, and parsing every one to test a genre made each page of the
        // library cost a pass over the whole catalog's JSON. `import_into` is
        // the only writer of payloads and keeps the two in step; the statements
        // below also repair a library written by an older version.
        tx.execute_batch(&format!(
            "CREATE INDEX IF NOT EXISTS rust_catalog_name ON rust_catalog(name_key);
             CREATE INDEX IF NOT EXISTS rust_catalog_media ON rust_catalog(media_type,id);
             CREATE TABLE IF NOT EXISTS rust_catalog_arrivals(id TEXT PRIMARY KEY);
             CREATE TABLE IF NOT EXISTS rust_catalog_changes(id TEXT PRIMARY KEY REFERENCES rust_catalog(id) ON DELETE CASCADE, changed_at TEXT NOT NULL);
             DROP TRIGGER IF EXISTS rust_catalog_arrival;
             INSERT INTO rust_catalog_arrivals SELECT id FROM rust_catalog
                WHERE id NOT IN (SELECT id FROM rust_catalog_arrivals) ORDER BY rowid;
             CREATE TABLE IF NOT EXISTS rust_catalog_summary(
                id TEXT PRIMARY KEY REFERENCES rust_catalog(id) ON DELETE CASCADE,
                summary TEXT NOT NULL);
             DELETE FROM rust_catalog_summary WHERE id NOT IN (SELECT id FROM rust_catalog);
             INSERT INTO rust_catalog_summary(id,summary)
                SELECT id,{SUMMARY_SQL} FROM rust_catalog
                WHERE id NOT IN (SELECT id FROM rust_catalog_summary);"
        ))?;
        let profile = crate::profiles::initialize(&tx)?;
        tx.execute_batch(
            "CREATE TABLE IF NOT EXISTS rust_sync_origins(
            profile_id TEXT NOT NULL REFERENCES rust_profiles(id) ON DELETE CASCADE,
            server TEXT NOT NULL, account TEXT NOT NULL, PRIMARY KEY(profile_id,server,account));",
        )?;
        crate::user_state::initialize(&tx)?;
        crate::scraped::initialize(&tx)?;
        tx.commit()?;
        Ok(Self(
            Arc::new(Mutex::new(connection)),
            profile,
            Arc::new(Mutex::new(None)),
        ))
    }

    pub fn import(&self, document: &CatalogDocument, replace: bool) -> Result<usize> {
        document.validate()?;
        let mut connection = self.0.lock().map_err(|_| Error::Lock)?;
        let transaction =
            connection.transaction_with_behavior(rusqlite::TransactionBehavior::Immediate)?;
        Self::import_into(&transaction, document, replace)?;
        transaction.commit()?;
        Ok(document.items.len())
    }

    pub(crate) fn import_into(
        transaction: &Connection,
        document: &CatalogDocument,
        replace: bool,
    ) -> Result<()> {
        if replace {
            transaction.execute("DELETE FROM rust_catalog_summary", [])?;
            transaction.execute("DELETE FROM rust_catalog", [])?;
            transaction.execute("DELETE FROM rust_catalog_tombstones", [])?;
        }
        // Most items already carry their identity, so they are written as they
        // are. Only an item whose identity has to be filled in is copied.
        let with_identity = |item: &CatalogItem, id: Uuid| -> Result<String> {
            Ok(if item.sync_id == Some(id) {
                serde_json::to_string(item)?
            } else {
                let mut owned = item.clone();
                owned.sync_id = Some(id);
                serde_json::to_string(&owned)?
            })
        };
        for item in &document.items {
            let id = item.id()?.to_string();
            if item.deleted {
                transaction.execute("DELETE FROM rust_catalog_arrivals WHERE id=?1", [&id])?;
                let payload = with_identity(
                    item,
                    Uuid::parse_str(&id)
                        .map_err(|_| Error::Invalid("Invalid stored identity".into()))?,
                )?;
                transaction
                    .prepare_cached("DELETE FROM rust_catalog_summary WHERE id=?1")?
                    .execute([&id])?;
                transaction
                    .prepare_cached("DELETE FROM rust_catalog WHERE id=?1")?
                    .execute([&id])?;
                transaction
                    .prepare_cached(
                        "INSERT INTO rust_catalog_tombstones(id,payload) VALUES(?1,?2)
                     ON CONFLICT(id) DO UPDATE SET payload=excluded.payload",
                    )?
                    .execute(params![id, payload])?;
                continue;
            }
            // An old ID-free export can refresh an entry already imported with
            // a sync ID without changing its identity or losing its favorite.
            let stored_id: Option<String> = if item.sync_id.is_none() {
                let mut lookup = transaction
                    .prepare_cached("SELECT id FROM rust_catalog WHERE name_key=?1 LIMIT 2")?;
                let mut matches = lookup.query([item.key()])?;
                let first = matches.next()?.map(|row| row.get(0)).transpose()?;
                if matches.next()?.is_some() {
                    return Err(Error::Invalid(format!(
                        "Hay varios títulos llamados {}. El catálogo debe indicar syncId.",
                        item.name
                    )));
                }
                first
            } else {
                None
            };
            let id = stored_id.unwrap_or(id);
            let payload = with_identity(
                item,
                Uuid::parse_str(&id)
                    .map_err(|_| Error::Invalid("Invalid stored identity".into()))?,
            )?;
            transaction.execute("INSERT INTO rust_catalog_changes(id,changed_at) SELECT id,?3 FROM rust_catalog WHERE id=?1 AND payload!=?2 ON CONFLICT(id) DO UPDATE SET changed_at=excluded.changed_at", params![id,payload,now()])?;
            transaction
                .prepare_cached(
                    "INSERT INTO rust_catalog(id,name_key,media_type,search_text,payload)
                 VALUES(?1,?2,?3,?4,?5) ON CONFLICT(id) DO UPDATE SET
                 name_key=excluded.name_key,media_type=excluded.media_type,
                 search_text=excluded.search_text,payload=excluded.payload",
                )?
                .execute(params![
                    id,
                    item.key(),
                    item.media_type.to_lowercase(),
                    item.search_text(),
                    payload
                ])?;
            transaction
                .prepare_cached(SUMMARY_UPSERT_SQL)?
                .execute(params![id, payload])?;
            transaction
                .prepare_cached(
                    "INSERT INTO rust_catalog_arrivals(id) VALUES(?1) ON CONFLICT(id) DO NOTHING",
                )?
                .execute([&id])?;
            transaction
                .prepare_cached("DELETE FROM rust_catalog_tombstones WHERE id=?1")?
                .execute([&id])?;
        }
        if replace {
            transaction.execute(
                "DELETE FROM rust_catalog_arrivals WHERE id NOT IN (SELECT id FROM rust_catalog)",
                [],
            )?;
        }
        transaction.execute("UPDATE rust_state SET revision=revision+1,updated_at=?1,source_label=?2 WHERE singleton=1",
            params![now(), document.source_label])?;
        Ok(())
    }

    pub(crate) fn tombstones_from(connection: &Connection) -> Result<Vec<CatalogItem>> {
        let mut statement =
            connection.prepare("SELECT payload FROM rust_catalog_tombstones ORDER BY id")?;
        let mut items = Vec::new();
        for row in statement.query_map([], |row| row.get::<_, String>(0))? {
            items.push(serde_json::from_str(&row?)?);
        }
        Ok(items)
    }

    /// Only read the fields needed to refresh saved editions. A large library can contain
    /// thousands of chapters per title, so the scheduler must not deserialize every payload.
    pub fn saved_editions(&self) -> Result<Vec<(Uuid, String, String, String)>> {
        let db = self.0.lock().map_err(|_| Error::Lock)?;
        let mut stmt = db.prepare(
            "SELECT c.id,json_extract(c.summary,'$.name'),p.key,p.value FROM rust_catalog_summary c
            LEFT JOIN json_each(c.summary,'$.onlineProviders') p ON p.type='text'
            WHERE p.key IS NOT NULL OR c.id LIKE '729ef15d-b486-84f4-983c-%' ORDER BY c.id",
        )?;
        let rows = stmt.query_map([], |row| {
            Ok((
                row.get::<_, String>(0)?,
                row.get::<_, String>(1)?,
                row.get::<_, Option<String>>(2)?,
                row.get::<_, Option<String>>(3)?,
            ))
        })?;
        let mut out = Vec::new();
        let mut has_capibara = std::collections::HashSet::new();
        let mut seen_candidates = std::collections::HashSet::new();
        let mut capibara_candidates = Vec::new();
        for row in rows {
            let (id, name, site, external) = row?;
            if let Ok(id) = Uuid::parse_str(&id) {
                if let (Some(site), Some(external)) = (site, external) {
                    if site.eq_ignore_ascii_case("capibaratraductor") {
                        has_capibara.insert(id);
                    }
                    out.push((id, name.clone(), site, external));
                }
                if let Some(number) = crate::capibara::edition_id(id) {
                    if seen_candidates.insert(id) {
                        capibara_candidates.push((id, name, number));
                    }
                }
            }
        }
        for (id, name, number) in capibara_candidates {
            if !has_capibara.contains(&id) {
                out.push((id, name, "capibaratraductor".into(), number.to_string()));
            }
        }
        Ok(out)
    }

    pub fn page(&self, query: &CatalogQuery) -> Result<LibraryPage> {
        self.filtered_page(query, 100, false, &[])
    }

    pub fn search(&self, query: &CatalogQuery) -> Result<LibraryPage> {
        self.filtered_page(query, 500, false, &[])
    }

    pub fn display_page(
        &self,
        query: &CatalogQuery,
        hide_adult: bool,
        preferred: &[String],
    ) -> Result<LibraryPage> {
        self.filtered_page(query, 100, hide_adult, preferred)
    }
    fn filtered_page(
        &self,
        query: &CatalogQuery,
        max: u32,
        hide_adult: bool,
        preferred: &[String],
    ) -> Result<LibraryPage> {
        query.validate_limit(max)?;
        let connection = self.0.lock().map_err(|_| Error::Lock)?;
        let _snapshot = connection.unchecked_transaction()?;
        let (revision, updated_at, source_label) = state(&connection)?;
        let media = query.media_type.as_ref().map(|v| v.to_lowercase());
        let q = normalize(&query.q);
        let cursor = query
            .cursor
            .as_ref()
            .map(|value| {
                Uuid::parse_str(value)
                    .map(|id| id.to_string())
                    .map_err(|_| Error::Invalid("Invalid catalog cursor".into()))
            })
            .transpose()?
            .unwrap_or_default();
        let limit = query.limit.unwrap_or(50) as usize;
        let mut filter = String::from(
            " FROM rust_catalog c JOIN rust_catalog_summary s ON s.id=c.id WHERE instr(c.search_text,?1)>0
            AND (?2 IS NULL OR c.media_type=?2)
            AND (?3=0 OR EXISTS(SELECT 1 FROM rust_favorites f WHERE f.id=c.id AND f.profile_id=?4))",
        );
        let mut args: Vec<rusqlite::types::Value> = vec![
            q.into(),
            media.into(),
            i64::from(query.favorites_only).into(),
            self.1.to_string().into(),
        ];
        if let Some(list_id) = query.filters.list_id {
            args.push(list_id.to_string().into());
            filter.push_str(
                " AND c.id IN (
                    SELECT member.value->>'itemId'
                    FROM rust_profiles p
                    CROSS JOIN json_each(p.document,'$.lists') list
                    CROSS JOIN json_each(list.value,'$.items') member
                    WHERE p.id=?4 AND list.value->>'id'=?5
                    AND COALESCE(list.value->>'isDeleted',0)=0
                )",
            );
        }
        crate::filters::apply(&query.filters, &mut filter, &mut args);
        const HIDE_ADULT_SQL: &str = " AND NOT EXISTS (SELECT 1 FROM json_each(s.summary,'$.genres') tag WHERE tag.value IS NOT NULL AND umd_adult_tag(tag.value))
            AND NOT EXISTS (SELECT 1 FROM json_each(s.summary,'$.categories') tag WHERE tag.value IS NOT NULL AND umd_adult_tag(tag.value))
            AND NOT umd_adult_rating(s.summary->>'contentRating')";
        if hide_adult {
            filter.push_str(HIDE_ADULT_SQL);
        }
        let total: i64 = connection
            .prepare_cached(&format!("SELECT count(*){filter}"))?
            .query_row(rusqlite::params_from_iter(&args), |row| row.get(0))?;
        let projection = if query.filters.summary {
            "summary"
        } else {
            "payload"
        };
        let sql = if preferred.is_empty() {
            let table = if query.filters.summary { "s" } else { "c" };
            format!("SELECT {table}.{projection}{filter} AND c.id>? ORDER BY c.id LIMIT ?")
        } else {
            args.push(serde_json::to_string(preferred)?.into());
            let pref = args.len();
            let roles = crate::filters::DISTRIBUTOR_ROLES_SQL;
            let rank = format!(
                "NOT (EXISTS(SELECT 1 FROM json_each(c.summary,'$.onlineProviders') src JOIN json_each(?{pref}) pref ON umd_label(src.key)=umd_label(pref.value))
                OR EXISTS(SELECT 1 FROM json_each(c.summary,'$.contributors') person JOIN json_each(?{pref}) pref ON umd_label(person.value->>'name')=umd_label(pref.value)
                    WHERE (person.value->>'kind' IS NULL OR lower(trim(person.value->>'kind'))='organization')
                    AND EXISTS(SELECT 1 FROM json_each(person.value,'$.credits') credit WHERE lower(trim(credit.value->>'role')) IN {roles})))"
            );
            // The cursor includes the relative position in the same ordered result, across pages.
            let place = format!("?{}", args.len() + 1);
            format!(
                "WITH filtered AS (SELECT c.id,c.payload,s.summary {filter}), ranked AS (SELECT c.id AS id,{projection} AS doc,{rank} AS rank FROM filtered c)
                SELECT doc FROM ranked WHERE (rank,id)>=(COALESCE((SELECT rank FROM ranked WHERE id={place}),(SELECT {rank} FROM rust_catalog_summary c WHERE c.id={place}),CASE WHEN {place}='' THEN 0 ELSE 1 END),{place})
                AND id!={place} ORDER BY rank,id LIMIT ?"
            )
        };
        let mut statement = connection.prepare_cached(&sql)?;
        args.extend([cursor.into(), (limit as i64 + 1).into()]);
        let payloads = statement.query_map(rusqlite::params_from_iter(&args), |row| {
            row.get::<_, String>(0)
        })?;
        let mut items = Vec::new();
        for payload in payloads {
            items.push(serde_json::from_str::<CatalogItem>(&payload?)?);
        }
        let more = items.len() > limit;
        items.truncate(limit);
        let continuation_token = if more {
            items
                .last()
                .and_then(|item| item.sync_id)
                .map(|id| id.simple().to_string())
        } else {
            None
        };
        let favorites_sql = if hide_adult {
            format!(
                "SELECT f.id FROM rust_favorites f JOIN rust_catalog_summary s ON s.id=f.id WHERE f.profile_id=?1{HIDE_ADULT_SQL} ORDER BY f.id"
            )
        } else {
            "SELECT id FROM rust_favorites WHERE profile_id=?1 ORDER BY id".into()
        };
        let mut favorites = connection.prepare_cached(&favorites_sql)?;
        let favorite_ids = favorites
            .query_map([self.1.to_string()], |row| row.get::<_, String>(0))?
            .map(|row| {
                Uuid::parse_str(&row?)
                    .map_err(|_| Error::Invalid("Invalid favorite identity".into()))
            })
            .collect::<Result<Vec<_>>>()?;
        let mut document = CatalogDocument::new(items);
        document.exported_at = updated_at;
        document.source_label = source_label;
        Ok(LibraryPage {
            page: CatalogPage {
                revision,
                continuation_token,
                document,
            },
            favorite_ids,
            total: total as u64,
        })
    }

    pub fn export(&self) -> Result<CatalogDocument> {
        let connection = self.0.lock().map_err(|_| Error::Lock)?;
        let _snapshot = connection.unchecked_transaction()?;
        Self::export_from(&connection)
    }

    pub fn export_with_limit(&self, max: usize) -> Result<Option<CatalogDocument>> {
        let db = self.0.lock().map_err(|_| Error::Lock)?;
        let tx = db.unchecked_transaction()?;
        let bytes: i64 = tx.query_row(
            "SELECT coalesce(sum(octet_length(payload)),0) FROM rust_catalog",
            [],
            |r| r.get(0),
        )?;
        if bytes > max as i64 {
            return Ok(None);
        }
        Self::export_from(&tx).map(Some)
    }

    pub(crate) fn export_from(connection: &Connection) -> Result<CatalogDocument> {
        let (_, updated_at, source_label) = state(connection)?;
        let mut statement = connection.prepare("SELECT payload FROM rust_catalog ORDER BY id")?;
        let mut items = Vec::new();
        for row in statement.query_map([], |row| row.get::<_, String>(0))? {
            items.push(serde_json::from_str(&row?)?);
        }
        let mut document = CatalogDocument::new(items);
        document.exported_at = updated_at;
        document.source_label = source_label;
        Ok(document)
    }

    pub fn filter_choices(&self) -> Result<serde_json::Value> {
        let db = self.0.lock().map_err(|_| Error::Lock)?;
        let values = |sql: &str| -> Result<Vec<String>> {
            let mut stmt = db.prepare(sql)?;
            let rows = stmt.query_map([], |r| r.get(0))?;
            Ok(rows.collect::<std::result::Result<_, _>>()?)
        };
        let distributors=format!("SELECT DISTINCT name FROM (SELECT org.value->>'name' name FROM rust_catalog_summary s,json_each(s.summary,'$.contributors') org WHERE coalesce(lower(org.value->>'kind'),'organization')='organization' AND EXISTS(SELECT 1 FROM json_each(org.value,'$.credits') c WHERE lower(trim(c.value->>'role')) IN {}) UNION SELECT p.key FROM rust_catalog_summary s,json_each(s.summary,'$.onlineProviders') p) WHERE name IS NOT NULL ORDER BY name COLLATE NOCASE",crate::filters::DISTRIBUTOR_ROLES_SQL);
        Ok(serde_json::json!({
            "category":values("SELECT DISTINCT CAST(c.value AS TEXT) FROM rust_catalog_summary s,json_each(s.summary,'$.categories') c WHERE c.type='text' ORDER BY 1 COLLATE NOCASE")?,
            "year":values("SELECT DISTINCT CAST(summary->>'releaseYear' AS TEXT) FROM rust_catalog_summary WHERE summary->>'releaseYear' IS NOT NULL ORDER BY 1 DESC")?,
            "distributor":values(&distributors)?
        }))
    }
    pub fn home_recent(&self) -> Result<serde_json::Value> {
        let added = self.recently_added()?;
        let db = self.0.lock().map_err(|_| Error::Lock)?;
        let mut stmt=db.prepare("SELECT s.summary FROM rust_catalog_changes c JOIN rust_catalog_summary s ON s.id=c.id ORDER BY c.changed_at DESC,c.rowid DESC LIMIT 20")?;
        let mut updated = Vec::new();
        for row in stmt.query_map([], |r| r.get::<_, String>(0))? {
            updated.push(serde_json::from_str::<CatalogItem>(&row?)?);
        }
        Ok(serde_json::json!({"added":added.items,"updated":updated}))
    }

    pub fn recently_added(&self) -> Result<CatalogDocument> {
        let db = self.0.lock().map_err(|_| Error::Lock)?;
        let mut stmt = db.prepare_cached("SELECT s.summary FROM rust_catalog_arrivals a JOIN rust_catalog_summary s ON s.id=a.id ORDER BY a.rowid DESC LIMIT 12")?;
        let rows = stmt.query_map([], |r| r.get::<_, String>(0))?;
        let mut items = Vec::new();
        for row in rows {
            items.push(serde_json::from_str(&row?)?);
        }
        Ok(CatalogDocument::new(items))
    }

    pub fn metadata(&self) -> Result<CatalogMetadata> {
        // SQLite's read transaction also covers writers in another app instance.
        let connection = self.0.lock().map_err(|_| Error::Lock)?;
        let _snapshot = connection.unchecked_transaction()?;
        let (revision, updated_at, _) = state(&connection)?;
        if let Some(cached) = self
            .2
            .lock()
            .map_err(|_| Error::Lock)?
            .as_ref()
            .filter(|(rev, meta)| *rev == revision && meta.updated_at == updated_at)
            .map(|(_, meta)| meta.clone())
        {
            return Ok(cached);
        }
        let computed = Self::metadata_from(&connection)?;
        *self.2.lock().map_err(|_| Error::Lock)? = Some((computed.revision, computed.clone()));
        Ok(computed)
    }

    pub fn publish(&self, document: &CatalogDocument) -> Result<CatalogMetadata> {
        document.validate()?;
        let mut connection = self.0.lock().map_err(|_| Error::Lock)?;
        let transaction =
            connection.transaction_with_behavior(rusqlite::TransactionBehavior::Immediate)?;
        Self::import_into(&transaction, document, true)?;
        let metadata = Self::metadata_from(&transaction)?;
        transaction.commit()?;
        *self.2.lock().map_err(|_| Error::Lock)? = Some((metadata.revision, metadata.clone()));
        Ok(metadata)
    }

    fn metadata_from(connection: &Connection) -> Result<CatalogMetadata> {
        let revision = state(connection)?.0;
        let document = Self::export_from(connection)?;
        let updated_at = document.exported_at.clone();
        let item_count = document.items.len();
        let sha256 = format!("{:X}", Sha256::digest(serde_json::to_vec(&document)?));
        Ok(CatalogMetadata {
            revision,
            updated_at,
            sha256,
            item_count,
        })
    }

    pub fn party_titles(&self, media_type: &str, name: &str) -> Result<Vec<Uuid>> {
        let key = friend_item_key(media_type, name);
        let db = self.0.lock().map_err(|_| Error::Lock)?;
        let mut stmt=db.prepare_cached("SELECT c.id,json_extract(s.summary,'$.name') FROM rust_catalog c JOIN rust_catalog_summary s ON s.id=c.id WHERE c.media_type=?1 ORDER BY c.id")?;
        let mut rows = stmt.query([media_type.trim().to_ascii_lowercase()])?;
        let mut matches = Vec::new();
        while let Some(row) = rows.next()? {
            if friend_item_key(media_type, &row.get::<_, String>(1)?) == key {
                matches.push(
                    row.get::<_, String>(0)?
                        .parse()
                        .map_err(|_| Error::Invalid("Identidad de catálogo inválida.".into()))?,
                );
            }
        }
        Ok(matches)
    }

    pub fn item(&self, id: Uuid) -> Result<Option<CatalogDocument>> {
        let connection = self.0.lock().map_err(|_| Error::Lock)?;
        Ok(Self::item_from(&connection, id)?.map(|item| CatalogDocument::new(vec![item])))
    }

    pub(crate) fn item_from(connection: &Connection, id: Uuid) -> Result<Option<CatalogItem>> {
        let payload: Option<String> = connection
            .prepare_cached("SELECT payload FROM rust_catalog WHERE id=?1")?
            .query_row([id.to_string()], |row| row.get(0))
            .optional()?;
        payload
            .map(|text| serde_json::from_str(&text))
            .transpose()
            .map_err(Into::into)
    }

    /// The title's metadata only. Use it where chapters and sources are not
    /// needed: a manga payload can be megabytes of chapter rows.
    pub(crate) fn summary_from(connection: &Connection, id: Uuid) -> Result<Option<CatalogItem>> {
        let summary: Option<String> = connection
            .prepare_cached("SELECT summary FROM rust_catalog_summary WHERE id=?1")?
            .query_row([id.to_string()], |row| row.get(0))
            .optional()?;
        summary
            .map(|text| serde_json::from_str(&text))
            .transpose()
            .map_err(Into::into)
    }

    pub fn set_favorite(&self, id: Uuid, enabled: bool) -> Result<()> {
        self.change_favorite(id, enabled)
    }

    pub fn delete_unit(&self, target: &ProgressTarget) -> Result<CatalogItem> {
        let bad = || Error::Invalid("No se encontró la unidad seleccionada.".into());
        let (kind, number) = (
            target.unit_kind.as_deref().ok_or_else(bad)?,
            target.display_number.as_deref().ok_or_else(bad)?,
        );
        let mut db = self.0.lock().map_err(|_| Error::Lock)?;
        let tx = db.transaction_with_behavior(rusqlite::TransactionBehavior::Immediate)?;
        let mut item = Self::item_from(&tx, target.item_id)?.ok_or_else(bad)?;
        let units = if let Some(season) = target.season_number {
            item.metadata
                .get_mut("seasons")
                .and_then(|v| v.as_array_mut())
                .and_then(|s| s.iter_mut().find(|v| v["number"].as_i64() == Some(season)))
                .and_then(|s| s.get_mut("units"))
        } else {
            item.metadata.get_mut("units")
        }
        .and_then(|v| v.as_array_mut())
        .ok_or_else(bad)?;
        let index = units
            .iter()
            .position(|u| {
                u["kind"].as_str() == Some(kind) && u["displayNumber"].as_str() == Some(number)
            })
            .ok_or_else(bad)?;
        units.remove(index);
        Self::import_into(&tx, &CatalogDocument::new(vec![item.clone()]), false)?;
        tx.commit()?;
        if let Ok(mut cache) = self.2.lock() {
            *cache = None;
        }
        Ok(item)
    }

    pub fn delete_item(&self, id: Uuid) -> Result<bool> {
        let mut connection = self.0.lock().map_err(|_| Error::Lock)?;
        let transaction =
            connection.transaction_with_behavior(rusqlite::TransactionBehavior::Immediate)?;
        let Some(mut item) = Self::item_from(&transaction, id)? else {
            return Ok(false);
        };
        item.sync_id = Some(id);
        item.deleted = true;
        item.metadata
            .insert("deletedAt".into(), serde_json::Value::String(now()));
        Self::import_into(&transaction, &CatalogDocument::new(vec![item]), false)?;
        transaction.commit()?;
        if let Ok(mut cache) = self.2.lock() {
            *cache = None;
        }
        Ok(true)
    }

    pub fn backup_revision(&self, server: &str, account: Uuid) -> Result<Option<i64>> {
        let db = self.0.lock().map_err(|_| Error::Lock)?;
        Ok(db
            .query_row(
                "SELECT revision FROM rust_backup_revisions WHERE server=?1 AND account=?2 AND profile_id=?3",
                params![server, account.to_string(),self.1.to_string()],
                |row| row.get(0),
            )
            .optional()?)
    }

    pub fn remember_upload(&self, server: &str, account: Uuid, revision: i64) -> Result<()> {
        let db = self.0.lock().map_err(|_| Error::Lock)?;
        self.write_backup_revision(&db, server, account, revision)
    }

    pub fn has_synced_account(&self, server: &str, account: Uuid) -> Result<bool> {
        let db = self.0.lock().map_err(|_| Error::Lock)?;
        Ok(db.query_row("SELECT EXISTS(SELECT 1 FROM rust_sync_origins WHERE profile_id=?1 AND server=?2 AND account=?3
            UNION ALL SELECT 1 FROM rust_backup_revisions WHERE profile_id=?1 AND server=?2 AND account=?3 AND revision>0)",
            params![self.1.to_string(),server,account.to_string()],|r|r.get(0))?)
    }

    pub fn remember_sync_origin(&self, server: &str, account: Uuid) -> Result<()> {
        let db = self.0.lock().map_err(|_| Error::Lock)?;
        db.execute(
            "INSERT OR IGNORE INTO rust_sync_origins VALUES(?1,?2,?3)",
            params![self.1.to_string(), server, account.to_string()],
        )?;
        Ok(())
    }

    pub(crate) fn write_backup_revision(
        &self,
        db: &Connection,
        server: &str,
        account: Uuid,
        revision: i64,
    ) -> Result<()> {
        db.execute(
            "INSERT INTO rust_backup_revisions(profile_id,server,account,revision) VALUES(?4,?1,?2,?3)
            ON CONFLICT(profile_id,server,account) DO UPDATE SET revision=excluded.revision",
            params![server, account.to_string(), revision,self.1.to_string()],
        )?;
        Ok(())
    }

    pub fn setting(&self, key: &str) -> Result<Option<String>> {
        let connection = self.0.lock().map_err(|_| Error::Lock)?;
        Ok(connection
            .query_row(
                "SELECT value FROM rust_settings WHERE key=?1",
                [key],
                |row| row.get(0),
            )
            .optional()?)
    }

    pub fn set_setting(&self, key: &str, value: &str) -> Result<()> {
        let connection = self.0.lock().map_err(|_| Error::Lock)?;
        connection.execute("INSERT INTO rust_settings VALUES(?1,?2) ON CONFLICT(key) DO UPDATE SET value=excluded.value",
            params![key, value])?;
        Ok(())
    }
}

use rusqlite::OptionalExtension;

/// A title without its chapters, seasons, sources and rights: what a list,
/// a filter or a search result needs.
pub(crate) const SUMMARY_SQL: &str =
    "json_remove(payload,'$.units','$.seasons','$.sources','$.distributionRights')";
const SUMMARY_UPSERT_SQL: &str = "INSERT INTO rust_catalog_summary(id,summary)
    VALUES(?1,json_remove(?2,'$.units','$.seasons','$.sources','$.distributionRights'))
    ON CONFLICT(id) DO UPDATE SET summary=excluded.summary";

fn state(connection: &Connection) -> Result<(i64, String, Option<String>)> {
    Ok(connection
        .prepare_cached(
            "SELECT revision,updated_at,source_label FROM rust_state WHERE singleton=1",
        )?
        .query_row([], |row| Ok((row.get(0)?, row.get(1)?, row.get(2)?)))?)
}
