use crate::*;
use rusqlite::{params, Connection};
use serde_json::Value;
use sha2::{Digest, Sha256};
use std::{
    collections::BTreeMap,
    path::Path,
    sync::{
        atomic::{AtomicUsize, Ordering},
        Arc, Mutex,
    },
    time::Duration,
};
use uuid::Uuid;

#[derive(Clone)]
pub struct CatalogStore(
    pub(crate) Arc<Mutex<Connection>>,
    pub(crate) Uuid,
    pub(crate) Arc<Mutex<CatalogCaches>>,
);

#[derive(Default)]
pub(crate) struct CatalogCaches {
    metadata: Option<(i64, CatalogMetadata)>,
    filters: Option<(i64, String, serde_json::Value)>,
}

pub struct CatalogReaders {
    readers: Vec<CatalogStore>,
    next: AtomicUsize,
}

pub struct EncodedCatalogPage {
    pub continuation_token: Option<String>,
    pub body: Vec<u8>,
}

impl CatalogReaders {
    pub fn get(&self) -> CatalogStore {
        let index = self.next.fetch_add(1, Ordering::Relaxed) % self.readers.len();
        self.readers[index].clone()
    }
}

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
            Arc::new(Mutex::new(CatalogCaches::default())),
        ))
    }

    pub fn readers(&self, count: usize) -> Result<CatalogReaders> {
        let path = self
            .0
            .lock()
            .map_err(|_| Error::Lock)?
            .path()
            .filter(|path| !path.is_empty())
            .map(str::to_owned);
        let mut readers = Vec::new();
        if let Some(path) = path {
            for _ in 0..count.max(1) {
                let mut reader = Self::open_read_only(&path)?;
                reader.2 = self.2.clone();
                readers.push(reader);
            }
        } else {
            readers.push(self.clone());
        }
        Ok(CatalogReaders {
            readers,
            next: AtomicUsize::new(0),
        })
    }

    fn configure_connection(connection: &Connection) -> Result<()> {
        crate::filters::register(connection)?;
        connection.busy_timeout(Duration::from_secs(10))?;
        connection.set_prepared_statement_cache_capacity(64);
        let cache_kib = if cfg!(target_os = "android") {
            2000
        } else {
            8000
        };
        connection.execute_batch(&format!(
            "PRAGMA temp_store=MEMORY; PRAGMA cache_size=-{cache_kib};"
        ))?;
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
                summary TEXT NOT NULL, is_adult INTEGER);
             DELETE FROM rust_catalog_summary WHERE id NOT IN (SELECT id FROM rust_catalog);
             INSERT INTO rust_catalog_summary(id,summary)
                SELECT id,{SUMMARY_SQL} FROM rust_catalog
                WHERE id NOT IN (SELECT id FROM rust_catalog_summary);"
        ))?;
        let adult_column = tx
            .prepare(
                "SELECT 1 FROM pragma_table_info('rust_catalog_summary') WHERE name='is_adult'",
            )?
            .exists([])?;
        if !adult_column {
            tx.execute_batch("ALTER TABLE rust_catalog_summary ADD COLUMN is_adult INTEGER")?;
        }
        tx.execute_batch("UPDATE rust_catalog_summary AS s SET is_adult=(
           EXISTS(SELECT 1 FROM json_each(s.summary,'$.genres') tag WHERE tag.type='text' AND umd_adult_tag(tag.value))
           OR EXISTS(SELECT 1 FROM json_each(s.summary,'$.categories') tag WHERE tag.type='text' AND umd_adult_tag(tag.value))
           OR umd_adult_rating(CASE WHEN json_type(s.summary,'$.contentRating')='text' THEN s.summary->>'contentRating' END))
           WHERE is_adult IS NULL")?;
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
            Arc::new(Mutex::new(CatalogCaches::default())),
        ))
    }

    pub fn import(&self, document: &CatalogDocument, replace: bool) -> Result<usize> {
        document.validate()?;
        let prepared = prepare_import(document)?;
        let mut connection = self.0.lock().map_err(|_| Error::Lock)?;
        let transaction =
            connection.transaction_with_behavior(rusqlite::TransactionBehavior::Immediate)?;
        Self::write_import(
            &transaction,
            document,
            replace,
            prepared.into_iter().map(Ok),
        )?;
        transaction.commit()?;
        Ok(document.items.len())
    }

    pub(crate) fn import_into(
        transaction: &Connection,
        document: &CatalogDocument,
        replace: bool,
    ) -> Result<()> {
        Self::import_rows(transaction, document, replace, true)
    }

    pub(crate) fn import_rows(
        transaction: &Connection,
        document: &CatalogDocument,
        replace: bool,
        include_tombstones: bool,
    ) -> Result<()> {
        Self::write_import(
            transaction,
            document,
            replace,
            document
                .items
                .iter()
                .filter(|item| include_tombstones || !item.deleted)
                .map(PreparedItem::new),
        )
    }

    fn write_import<'a>(
        transaction: &Connection,
        document: &CatalogDocument,
        replace: bool,
        rows: impl IntoIterator<Item = Result<PreparedItem<'a>>>,
    ) -> Result<()> {
        if replace {
            transaction.execute("DELETE FROM rust_catalog_summary", [])?;
            transaction.execute("DELETE FROM rust_catalog", [])?;
            transaction.execute("DELETE FROM rust_catalog_tombstones", [])?;
        }
        for row in rows {
            let row = row?;
            let item = row.item;
            let id = row.id.to_string();
            if item.deleted {
                transaction.execute("DELETE FROM rust_catalog_arrivals WHERE id=?1", [&id])?;
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
                    .execute(params![id, row.payload])?;
                continue;
            }
            // An old ID-free export can refresh an entry already imported with
            // a sync ID without changing its identity or losing its favorite.
            let stored_id: Option<String> = if item.sync_id.is_none() {
                let mut lookup = transaction
                    .prepare_cached("SELECT id FROM rust_catalog WHERE name_key=?1 LIMIT 2")?;
                let mut matches = lookup.query([&row.key])?;
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
            let (id, payload, summary) = if let Some(old_id) = stored_id.filter(|old| old != &id) {
                let identity = Uuid::parse_str(&old_id)
                    .map_err(|_| Error::Invalid("Invalid stored identity".into()))?;
                let (payload, summary) = encode_item(item, identity)?;
                (old_id, payload, summary)
            } else {
                (id, row.payload, row.summary)
            };
            transaction.execute("INSERT INTO rust_catalog_changes(id,changed_at) SELECT id,?3 FROM rust_catalog WHERE id=?1 AND payload!=?2 ON CONFLICT(id) DO UPDATE SET changed_at=excluded.changed_at", params![id,payload,now()])?;
            transaction
                .prepare_cached(
                    "INSERT INTO rust_catalog(id,name_key,media_type,search_text,payload)
                 VALUES(?1,?2,?3,?4,?5) ON CONFLICT(id) DO UPDATE SET
                 name_key=excluded.name_key,media_type=excluded.media_type,
                 search_text=excluded.search_text,payload=excluded.payload",
                )?
                .execute(params![id, row.key, row.media, row.search, payload])?;
            transaction
                .prepare_cached("INSERT INTO rust_catalog_summary(id,summary,is_adult) VALUES(?1,?2,?3)
                    ON CONFLICT(id) DO UPDATE SET summary=excluded.summary,is_adult=excluded.is_adult")?
                .execute(params![id, summary, row.adult])?;
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

    /// Public pages can reuse the validated, compact JSON stored in SQLite.
    pub fn encode_public_page(
        &self,
        query: &CatalogQuery,
        search: bool,
        max: usize,
        hide_adult: bool,
    ) -> Result<EncodedCatalogPage> {
        query.validate_limit(if search { 500 } else { 100 })?;
        let cursor = canonical_cursor(query)?;
        let count = query.limit.unwrap_or(50) as usize;
        let (filter, mut args) = page_filter(self.1, query, hide_adult);
        args.extend([cursor.into(), (count as i64 + 1).into()]);
        let column = if query.filters.summary {
            "s.summary"
        } else {
            "c.payload"
        };
        let db = self.0.lock().map_err(|_| Error::Lock)?;
        let _tx = db.unchecked_transaction()?;
        let (revision, stamp, source) = state(&db)?;
        let mut body = Vec::with_capacity(max.min(64 << 10));
        if !search {
            body.extend_from_slice(format!(r#"{{"revision":{revision},"document":"#).as_bytes());
        }
        document_prefix(&mut body, &stamp, source.as_deref())?;
        const CLOSED: &[u8] = b"]}";
        const CURSOR_FIELD: &[u8] = br#","continuationToken":"#;
        let min_footer = CLOSED.len()
            + if search {
                0
            } else {
                CURSOR_FIELD.len() + b"null}".len()
            };
        if body.len() + min_footer > max {
            return Err(Error::DocumentTooLarge(max));
        }
        let mut statement = db.prepare_cached(&format!(
            "SELECT c.id,{column}{filter} AND c.id>? ORDER BY c.id LIMIT ?"
        ))?;
        let mut rows = statement.query(rusqlite::params_from_iter(&args))?;
        let mut added = Vec::new();
        let mut more = false;
        while let Some(row) = rows.next()? {
            let payload = raw_text(row, 1)?;
            if added.len() == count
                || body.len() + payload.len() + usize::from(!added.is_empty()) + min_footer > max
            {
                more = true;
                break;
            }
            let key: String = row.get(0)?;
            added.push((body.len(), key));
            if added.len() > 1 {
                body.push(b',');
            }
            body.extend_from_slice(payload);
        }
        if more && !search {
            let extra = 34 - b"null".len();
            while body.len() + min_footer + extra > max {
                let (offset, _) = added.pop().ok_or(Error::DocumentTooLarge(max))?;
                body.truncate(offset);
            }
        }
        let continuation_token = if more {
            let (_, id) = added.last().ok_or(Error::DocumentTooLarge(max))?;
            Some(
                Uuid::parse_str(id)
                    .map_err(|_| Error::Invalid("Identidad de catálogo inválida.".into()))?
                    .simple()
                    .to_string(),
            )
        } else {
            None
        };
        body.extend_from_slice(CLOSED);
        if !search {
            body.extend_from_slice(CURSOR_FIELD);
            serde_json::to_writer(&mut body, &continuation_token)?;
            body.push(b'}');
        }
        Ok(EncodedCatalogPage {
            continuation_token,
            body,
        })
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
        let cursor = canonical_cursor(query)?;
        let limit = query.limit.unwrap_or(50) as usize;
        let (filter, mut args) = page_filter(self.1, query, hide_adult);
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
                "WITH filtered AS (SELECT c.id,s.summary {filter}), ranked AS (SELECT c.id AS id,{rank} AS rank FROM filtered c),
                selected AS (SELECT id,rank FROM ranked WHERE (rank,id)>=(COALESCE((SELECT rank FROM ranked WHERE id={place}),(SELECT {rank} FROM rust_catalog_summary c WHERE c.id={place}),CASE WHEN {place}='' THEN 0 ELSE 1 END),{place})
                AND id!={place} ORDER BY rank,id LIMIT ?)
                SELECT docs.{projection} FROM selected JOIN {documents} docs ON docs.id=selected.id ORDER BY selected.rank,selected.id",
                documents = if query.filters.summary { "rust_catalog_summary" } else { "rust_catalog" },
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
        let favorite_ids = self.favorite_ids_from(&connection, hide_adult)?;
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
        Self::bounded_export(&tx, max)
    }

    /// Uses one SQLite snapshot for both the cache validator and its response.
    pub fn encode_current(
        &self,
        known: Option<&str>,
        max: usize,
    ) -> Result<(String, Option<Vec<u8>>)> {
        let db = self.0.lock().map_err(|_| Error::Lock)?;
        let tx = db.unchecked_transaction()?;
        let (revision, updated_at, source) = state(&tx)?;
        let validator = format!("\"{revision}-{updated_at}\"");
        let unchanged = known.is_some_and(|header| {
            header.split(',').any(|value| {
                let value = value.trim();
                value == "*" || value.strip_prefix("W/").unwrap_or(value) == validator
            })
        });
        let document = if unchanged {
            None
        } else {
            let mut statement = tx.prepare("SELECT payload FROM rust_catalog ORDER BY id")?;
            let rows = statement.query([])?;
            Some(encode_document(rows, &updated_at, source.as_deref(), max)?)
        };
        Ok((format!("W/{validator}"), document))
    }

    pub fn encode_recent(&self, max: usize, hide_adult: bool) -> Result<Vec<u8>> {
        let db = self.0.lock().map_err(|_| Error::Lock)?;
        let mut statement = db.prepare_cached(RECENT_ADDITIONS_SQL)?;
        let rows = statement.query([hide_adult])?;
        encode_document(rows, &now(), Some("Universal Media Distribution"), max)
    }

    pub fn favorite_ids(&self, hide_adult: bool) -> Result<Vec<Uuid>> {
        let db = self.0.lock().map_err(|_| Error::Lock)?;
        self.favorite_ids_from(&db, hide_adult)
    }

    fn favorite_ids_from(&self, connection: &Connection, hide_adult: bool) -> Result<Vec<Uuid>> {
        let sql = if hide_adult {
            "SELECT f.id FROM rust_favorites f JOIN rust_catalog_summary s ON s.id=f.id WHERE f.profile_id=?1 AND NOT s.is_adult ORDER BY f.id"
        } else {
            "SELECT id FROM rust_favorites WHERE profile_id=?1 ORDER BY id"
        };
        let mut query = connection.prepare_cached(sql)?;
        let rows = query.query_map([self.1.to_string()], |row| row.get::<_, String>(0))?;
        rows.map(|row| {
            Uuid::parse_str(&row?).map_err(|_| Error::Invalid("Invalid favorite identity".into()))
        }).collect()
    }

    pub fn encode_public_item(&self, id: Uuid, max: usize) -> Result<Option<Vec<u8>>> {
        let db = self.0.lock().map_err(|_| Error::Lock)?;
        let mut statement = db.prepare_cached("SELECT payload FROM rust_catalog WHERE id=?1")?;
        let mut rows = statement.query([id.to_string()])?;
        let Some(row) = rows.next()? else {
            return Ok(None);
        };
        let payload = raw_text(row, 0)?;
        let mut body = Vec::with_capacity(payload.len().saturating_add(256).min(max));
        document_prefix(&mut body, &now(), Some("Universal Media Distribution"))?;
        if body.len() + payload.len() + 2 > max {
            return Err(Error::DocumentTooLarge(max));
        }
        body.extend_from_slice(payload);
        body.extend_from_slice(b"]}");
        Ok(Some(body))
    }

    fn bounded_export(db: &Connection, max: usize) -> Result<Option<CatalogDocument>> {
        let bytes: i64 = db.query_row(
            "SELECT coalesce(sum(octet_length(payload)),0) FROM rust_catalog",
            [],
            |r| r.get(0),
        )?;
        if bytes > max as i64 {
            return Ok(None);
        }
        Self::export_from(db).map(Some)
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

    pub(crate) fn summaries_from(connection: &Connection) -> Result<CatalogDocument> {
        let mut statement =
            connection.prepare("SELECT summary FROM rust_catalog_summary ORDER BY id")?;
        let mut rows = statement.query([])?;
        let mut items = Vec::new();
        while let Some(row) = rows.next()? {
            let summary: String = row.get(0)?;
            items.push(serde_json::from_str(&summary)?);
        }
        Ok(CatalogDocument::new(items))
    }

    pub fn filter_choices(&self) -> Result<serde_json::Value> {
        let db = self.0.lock().map_err(|_| Error::Lock)?;
        let _snapshot = db.unchecked_transaction()?;
        let (revision, updated, _) = state(&db)?;
        if let Some((_, _, value)) = self
            .2
            .lock()
            .map_err(|_| Error::Lock)?
            .filters
            .as_ref()
            .filter(|(rev, stamp, _)| *rev == revision && *stamp == updated)
        {
            return Ok(value.clone());
        }
        let values = |sql: &str| -> Result<Vec<String>> {
            let mut stmt = db.prepare(sql)?;
            let rows = stmt.query_map([], |r| r.get(0))?;
            Ok(rows.collect::<std::result::Result<_, _>>()?)
        };
        let distributors=format!("SELECT DISTINCT name FROM (SELECT org.value->>'name' name FROM rust_catalog_summary s,json_each(s.summary,'$.contributors') org WHERE coalesce(lower(org.value->>'kind'),'organization')='organization' AND EXISTS(SELECT 1 FROM json_each(org.value,'$.credits') c WHERE lower(trim(c.value->>'role')) IN {}) UNION SELECT p.key FROM rust_catalog_summary s,json_each(s.summary,'$.onlineProviders') p) WHERE name IS NOT NULL ORDER BY name COLLATE NOCASE",crate::filters::DISTRIBUTOR_ROLES_SQL);
        let choices = serde_json::json!({
            "category":values("SELECT DISTINCT CAST(c.value AS TEXT) FROM rust_catalog_summary s,json_each(s.summary,'$.categories') c WHERE c.type='text' ORDER BY 1 COLLATE NOCASE")?,
            "year":values("SELECT DISTINCT CAST(summary->>'releaseYear' AS TEXT) FROM rust_catalog_summary WHERE summary->>'releaseYear' IS NOT NULL ORDER BY 1 DESC")?,
            "distributor":values(&distributors)?
        });
        self.2.lock().map_err(|_| Error::Lock)?.filters =
            Some((revision, updated, choices.clone()));
        Ok(choices)
    }
    pub fn home_recent(&self, hide_adult: bool) -> Result<serde_json::Value> {
        let added = self.recently_added(hide_adult)?;
        let db = self.0.lock().map_err(|_| Error::Lock)?;
        let mut stmt = db.prepare_cached(
            "SELECT s.summary FROM rust_catalog_changes c JOIN rust_catalog_summary s ON s.id=c.id
             WHERE (NOT ?1 OR NOT s.is_adult) ORDER BY c.changed_at DESC, c.rowid DESC LIMIT 20",
        )?;
        let mut updated = Vec::new();
        for row in stmt.query_map([hide_adult], |r| r.get::<_, String>(0))? {
            updated.push(serde_json::from_str::<CatalogItem>(&row?)?);
        }
        Ok(serde_json::json!({"added":added.items,"updated":updated}))
    }

    pub fn recently_added(&self, hide_adult: bool) -> Result<CatalogDocument> {
        let db = self.0.lock().map_err(|_| Error::Lock)?;
        let mut stmt = db.prepare_cached(RECENT_ADDITIONS_SQL)?;
        let rows = stmt.query_map([hide_adult], |r| r.get::<_, String>(0))?;
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
            .metadata
            .as_ref()
            .filter(|(rev, meta)| *rev == revision && meta.updated_at == updated_at)
            .map(|(_, meta)| meta.clone())
        {
            return Ok(cached);
        }
        let computed = Self::metadata_from(&connection)?;
        self.2.lock().map_err(|_| Error::Lock)?.metadata =
            Some((computed.revision, computed.clone()));
        Ok(computed)
    }

    pub fn publish(&self, document: &CatalogDocument) -> Result<CatalogMetadata> {
        document.validate()?;
        let prepared = prepare_import(document)?;
        let mut connection = self.0.lock().map_err(|_| Error::Lock)?;
        let transaction =
            connection.transaction_with_behavior(rusqlite::TransactionBehavior::Immediate)?;
        Self::write_import(&transaction, document, true, prepared.into_iter().map(Ok))?;
        let metadata = Self::metadata_from(&transaction)?;
        transaction.commit()?;
        self.2.lock().map_err(|_| Error::Lock)?.metadata =
            Some((metadata.revision, metadata.clone()));
        Ok(metadata)
    }

    fn metadata_from(connection: &Connection) -> Result<CatalogMetadata> {
        struct HashWriter(Sha256);
        impl std::io::Write for HashWriter {
            fn write(&mut self, bytes: &[u8]) -> std::io::Result<usize> {
                self.0.update(bytes);
                Ok(bytes.len())
            }
            fn flush(&mut self) -> std::io::Result<()> {
                Ok(())
            }
        }
        let (revision, updated_at, source_label) = state(connection)?;
        let mut hash = HashWriter(Sha256::new());
        // Hash the same compact JSON as export(), one typed row at a time.
        hash.0.update(
            br#"{"format":"universal-media-distribution.catalog","version":1,"exportedAt":"#,
        );
        serde_json::to_writer(&mut hash, &updated_at)?;
        hash.0.update(br#","sourceLabel":"#);
        serde_json::to_writer(&mut hash, &source_label)?;
        hash.0.update(br#","items":["#);
        let mut statement = connection.prepare("SELECT payload FROM rust_catalog ORDER BY id")?;
        let mut rows = statement.query([])?;
        let mut item_count = 0;
        while let Some(row) = rows.next()? {
            if item_count != 0 {
                hash.0.update(b",");
            }
            let item: CatalogItem =
                serde_json::from_str(row.get_ref(0)?.as_str().map_err(|error| {
                    rusqlite::Error::FromSqlConversionFailure(
                        0,
                        rusqlite::types::Type::Text,
                        Box::new(error),
                    )
                })?)?;
            serde_json::to_writer(&mut hash, &item)?;
            item_count += 1;
        }
        hash.0.update(b"]}");
        let sha256 = format!("{:X}", hash.0.finalize());
        Ok(CatalogMetadata {
            revision,
            updated_at,
            sha256,
            item_count,
        })
    }

    pub fn party_titles(&self, media_type: &str, name: &str) -> Result<Vec<Uuid>> {
        let key = friend_item_key(media_type, name);
        let (media, name) = key.split_once(':').unwrap();
        let types: &[&str] = match media {
            "movie" => &[
                "movie",
                "series",
                "anime_movie",
                "anime_series",
                "documentary",
                "short",
            ],
            "book" => &[
                "book",
                "novel",
                "manga",
                "comic",
                "light_novel",
                "web_novel",
            ],
            "audio" => &["album", "single", "audiobook", "podcast", "radio_drama"],
            _ => return Ok(Vec::new()),
        };
        let keys: Vec<_> = types
            .iter()
            .map(|kind| format!("{media}:{kind}:{name}"))
            .collect();
        let db = self.0.lock().map_err(|_| Error::Lock)?;
        let mut stmt=db.prepare_cached("SELECT id FROM rust_catalog WHERE name_key IN (SELECT value FROM json_each(?1)) ORDER BY id")?;
        let mut rows = stmt.query([serde_json::to_string(&keys)?])?;
        let mut matches = Vec::new();
        while let Some(row) = rows.next()? {
            matches.push(
                row.get::<_, String>(0)?
                    .parse()
                    .map_err(|_| Error::Invalid("Identidad de catálogo inválida.".into()))?,
            );
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

    /// Find counterparts even when the local title is on a different page.
    pub fn matching_catalog_summaries(
        &self,
        titles: &[CatalogItem],
    ) -> Result<BTreeMap<Uuid, CatalogItem>> {
        let connection = self.0.lock().map_err(|_| Error::Lock)?;
        let mut matches = BTreeMap::new();
        for title in titles {
            if let Some(id) = title.sync_id {
                if let Some(local) = Self::matching_summary_from(&connection, id, title)? {
                    matches.insert(id, local);
                }
            }
        }
        Ok(matches)
    }

    pub(crate) fn matching_summary_from(
        connection: &Connection,
        id: Uuid,
        title: &CatalogItem,
    ) -> Result<Option<CatalogItem>> {
        if let Some(exact) = Self::summary_from(connection, id)? {
            return Ok(Some(exact));
        }
        fn year(item: &CatalogItem) -> Option<i64> {
            item.metadata
                .get("releaseYear")
                .and_then(Value::as_i64)
                .or_else(|| item.metadata.get("releaseDate")?.as_str()?.get(..4)?.parse().ok())
                .filter(|year| (1800..=2200).contains(year))
        }
        let mut query = connection.prepare_cached(
            "SELECT s.summary FROM rust_catalog c JOIN rust_catalog_summary s ON s.id=c.id WHERE c.name_key=?1",
        )?;
        let mut rows = query.query([title.key()])?;
        let incoming_year = year(title);
        let incoming_providers = title
            .metadata
            .get("onlineProviders")
            .and_then(Value::as_object);
        let mut matched = None;
        while let Some(row) = rows.next()? {
            let local: CatalogItem = serde_json::from_slice(raw_text(row, 0)?)?;
            if year(&local)
                .zip(incoming_year)
                .is_some_and(|(a, b)| a != b)
            {
                continue;
            }
            if let (Some(local_providers), Some(incoming_providers)) = (
                local.metadata.get("onlineProviders").and_then(Value::as_object),
                incoming_providers,
            ) {
                if local_providers.iter().any(|(site, value)| {
                    value.as_str()
                        .zip(incoming_providers.get(site).and_then(Value::as_str))
                        .is_some_and(|(a, b)| !a.eq_ignore_ascii_case(b))
                }) {
                    continue;
                }
            }
            if matched.is_some() {
                return Ok(None);
            }
            matched = Some(local);
        }
        Ok(matched)
    }

    pub fn summaries(&self, ids: &[Uuid]) -> Result<Vec<CatalogItem>> {
        let connection = self.0.lock().map_err(|_| Error::Lock)?;
        let mut query = connection.prepare_cached(
            "SELECT s.summary FROM json_each(?1) ids JOIN rust_catalog_summary s ON s.id=ids.value ORDER BY ids.key",
        )?;
        let mut rows = query.query([serde_json::to_string(ids)?])?;
        let mut items = Vec::new();
        while let Some(row) = rows.next()? {
            let json: String = row.get(0)?;
            items.push(serde_json::from_str(&json)?);
        }
        Ok(items)
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
            *cache = CatalogCaches::default();
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
            *cache = CatalogCaches::default();
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

fn raw_text<'a>(row: &'a rusqlite::Row<'_>, column: usize) -> Result<&'a [u8]> {
    row.get_ref(column)?
        .as_str()
        .map(str::as_bytes)
        .map_err(|error| {
            rusqlite::Error::FromSqlConversionFailure(
                column,
                rusqlite::types::Type::Text,
                Box::new(error),
            )
            .into()
        })
}

fn document_prefix(body: &mut Vec<u8>, stamp: &str, source: Option<&str>) -> Result<()> {
    body.extend_from_slice(
        br#"{"format":"universal-media-distribution.catalog","version":1,"exportedAt":"#,
    );
    serde_json::to_writer(&mut *body, stamp)?;
    body.extend_from_slice(br#","sourceLabel":"#);
    serde_json::to_writer(&mut *body, &source)?;
    body.extend_from_slice(br#","items":["#);
    Ok(())
}

fn encode_document(
    mut rows: rusqlite::Rows<'_>,
    stamp: &str,
    source: Option<&str>,
    max: usize,
) -> Result<Vec<u8>> {
    let mut body = Vec::with_capacity(max.min(64 << 10));
    document_prefix(&mut body, stamp, source)?;
    if body.len() + 2 > max {
        return Err(Error::DocumentTooLarge(max));
    }
    let mut separator = 0;
    while let Some(row) = rows.next()? {
        let data = raw_text(row, 0)?;
        if body.len() + data.len() + separator + 2 > max {
            return Err(Error::DocumentTooLarge(max));
        }
        if separator != 0 {
            body.push(b',');
        }
        body.extend_from_slice(data);
        separator = 1;
    }
    body.extend_from_slice(b"]}");
    Ok(body)
}

struct PreparedItem<'a> {
    item: &'a CatalogItem,
    id: Uuid,
    key: String,
    media: String,
    search: String,
    payload: String,
    summary: String,
    adult: bool,
}

impl<'a> PreparedItem<'a> {
    fn new(item: &'a CatalogItem) -> Result<Self> {
        let id = item.id()?;
        let (payload, summary) = encode_item(item, id)?;
        let (key, media, search) = if item.deleted {
            Default::default()
        } else {
            (
                item.key(),
                item.media_type.to_lowercase(),
                item.search_text(),
            )
        };
        Ok(Self {
            item,
            id,
            key,
            media,
            search,
            payload,
            summary,
            adult: item.adult(),
        })
    }
}

fn encode_item(item: &CatalogItem, id: Uuid) -> Result<(String, String)> {
    #[derive(serde::Serialize)]
    #[serde(rename_all = "camelCase")]
    struct Row<'a> {
        sync_id: Uuid,
        media_type: &'a str,
        subtype: &'a str,
        name: &'a str,
        deleted: bool,
        #[serde(flatten)]
        metadata: std::collections::BTreeMap<&'a str, &'a serde_json::Value>,
    }
    let mut row = Row {
        sync_id: id,
        media_type: &item.media_type,
        subtype: &item.subtype,
        name: &item.name,
        deleted: item.deleted,
        metadata: item
            .metadata
            .iter()
            .map(|(key, value)| (key.as_str(), value))
            .collect(),
    };
    let payload = serde_json::to_string(&row)?;
    if item.deleted {
        return Ok((payload, String::new()));
    }
    for key in ["units", "seasons", "sources", "distributionRights"] {
        row.metadata.remove(key);
    }
    Ok((payload, serde_json::to_string(&row)?))
}

fn prepare_import(document: &CatalogDocument) -> Result<Vec<PreparedItem<'_>>> {
    let thread_limit = if cfg!(target_os = "android") { 2 } else { 4 };
    let available = std::thread::available_parallelism().map_or(1, usize::from);
    let workers = available
        .min(thread_limit)
        .min(document.items.len().div_ceil(128));
    if workers < 2 {
        return document.items.iter().map(PreparedItem::new).collect();
    }
    std::thread::scope(|scope| {
        let part = document.items.len().div_ceil(workers);
        let jobs: Vec<_> = document
            .items
            .chunks(part)
            .map(|items| {
                scope.spawn(move || {
                    items
                        .iter()
                        .map(PreparedItem::new)
                        .collect::<Result<Vec<_>>>()
                })
            })
            .collect();
        let mut rows = Vec::with_capacity(document.items.len());
        for job in jobs {
            rows.extend(
                job.join()
                    .map_err(|_| Error::Invalid("No se pudo preparar el catálogo.".into()))??,
            );
        }
        Ok(rows)
    })
}

fn canonical_cursor(query: &CatalogQuery) -> Result<String> {
    Ok(query
        .cursor
        .as_ref()
        .map(|value| {
            Uuid::parse_str(value)
                .map(|id| id.to_string())
                .map_err(|_| Error::Invalid("Invalid catalog cursor".into()))
        })
        .transpose()?
        .unwrap_or_default())
}

const HIDE_ADULT_SQL: &str = " AND NOT s.is_adult";
const RECENT_ADDITIONS_SQL: &str =
    "SELECT s.summary FROM rust_catalog_arrivals a JOIN rust_catalog_summary s ON s.id=a.id
     WHERE (NOT ?1 OR NOT s.is_adult) ORDER BY a.rowid DESC LIMIT 12";

fn page_filter(
    profile: Uuid,
    query: &CatalogQuery,
    hide_adult: bool,
) -> (String, Vec<rusqlite::types::Value>) {
    let media = query.media_type.as_ref().map(|v| v.to_lowercase());
    let q = normalize(&query.q);
    let mut filter = String::from(
            " FROM rust_catalog c JOIN rust_catalog_summary s ON s.id=c.id WHERE instr(c.search_text,?1)>0
            AND (?2 IS NULL OR c.media_type=?2)
            AND (?3=0 OR EXISTS(SELECT 1 FROM rust_favorites f WHERE f.id=c.id AND f.profile_id=?4))",
        );
    let mut args: Vec<rusqlite::types::Value> = vec![
        q.into(),
        media.into(),
        i64::from(query.favorites_only).into(),
        profile.to_string().into(),
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
    if hide_adult {
        filter.push_str(HIDE_ADULT_SQL);
    }
    (filter, args)
}

fn state(connection: &Connection) -> Result<(i64, String, Option<String>)> {
    Ok(connection
        .prepare_cached(
            "SELECT revision,updated_at,source_label FROM rust_state WHERE singleton=1",
        )?
        .query_row([], |row| Ok((row.get(0)?, row.get(1)?, row.get(2)?)))?)
}
