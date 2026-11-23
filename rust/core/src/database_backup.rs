use crate::{CatalogStore, Error, Result};
use rusqlite::{Connection, OpenFlags, OptionalExtension};
use std::path::Path;
fn invalid() -> Error {
    Error::Invalid("El archivo no es un respaldo de biblioteca UMD compatible.".into())
}
/// Walks every page of the library. Thorough, and proportional to its size.
pub fn library_integrity(path: &Path) -> Result<bool> {
    probe(path, "PRAGMA quick_check(1)")
}
/// Confirms the file opens as a database and its schema can be read. This is
/// the check a launch can afford every time; `library_integrity` is the deep one.
pub fn library_readable(path: &Path) -> Result<bool> {
    probe(
        path,
        "SELECT CASE WHEN count(*)>=0 THEN 'ok' END FROM sqlite_schema",
    )
}
fn probe(path: &Path, sql: &str) -> Result<bool> {
    if !path.exists() {
        return Ok(true);
    }
    let check = |flags: OpenFlags| {
        Connection::open_with_flags(path, flags).and_then(|db| {
            db.execute_batch("PRAGMA trusted_schema=OFF")?;
            db.query_row(sql, [], |r| r.get::<_, String>(0))
        })
    };
    let result = match check(OpenFlags::SQLITE_OPEN_READ_ONLY) {
        Err(rusqlite::Error::SqliteFailure(ref e, _))
            if e.code == rusqlite::ErrorCode::CannotOpen && path.exists() =>
        {
            check(OpenFlags::SQLITE_OPEN_READ_WRITE)
        }
        other => other,
    };
    match result {
        Ok(answer) => Ok(answer == "ok"),
        Err(rusqlite::Error::SqliteFailure(ref e, _))
            if matches!(
                e.code,
                rusqlite::ErrorCode::DatabaseCorrupt | rusqlite::ErrorCode::NotADatabase
            ) =>
        {
            Ok(false)
        }
        Err(e) => Err(e.into()),
    }
}
pub fn check_library_backup(path: &Path) -> Result<()> {
    let db = Connection::open_with_flags(path, OpenFlags::SQLITE_OPEN_READ_ONLY)?;
    db.execute_batch("PRAGMA trusted_schema=OFF;")?;
    if db.query_row("PRAGMA integrity_check(1)", [], |r| r.get::<_, String>(0))? != "ok" {
        return Err(invalid());
    }
    let has_disallowed_objects: bool = db.query_row(
        "SELECT EXISTS(SELECT 1 FROM pragma_table_list WHERE schema='main' AND type IN ('view','virtual','shadow'))",
        [],
        |r| r.get(0),
    )?;
    let tables:i64=db.query_row("SELECT count(*) FROM sqlite_schema WHERE type='table' AND name IN ('rust_catalog','rust_state','rust_profiles','rust_profile_selection','rust_favorites','rust_settings','rust_backup_revisions','rust_sync_origins')",[],|r|r.get(0))?;
    if has_disallowed_objects
        || tables != 8
        || db.query_row(
            "SELECT count(*) FROM sqlite_schema WHERE type='trigger'",
            [],
            |r| r.get::<_, i64>(0),
        )? != 0
    {
        return Err(invalid());
    }
    if db.prepare("PRAGMA foreign_key_check")?.exists([])? {
        return Err(invalid());
    }
    let has_state: bool = db.query_row(
        "SELECT EXISTS(SELECT 1 FROM rust_state WHERE singleton=1)",
        [],
        |r| r.get(0),
    )?;
    if !has_state {
        return Err(invalid());
    }
    let active: Option<String> = db
        .query_row(
            "SELECT p.id FROM rust_profile_selection s JOIN rust_profiles p ON p.id=s.profile_id WHERE s.singleton=1",
            [],
            |r| r.get(0),
        )
        .optional()?;
    if active
        .as_deref()
        .is_none_or(|s| uuid::Uuid::parse_str(s).is_err())
    {
        return Err(invalid());
    }
    // Read the columns consumed by the current schema before touching a live library.
    db.prepare("SELECT id,name_key,media_type,search_text,payload FROM rust_catalog")?;
    Ok(())
}
impl CatalogStore {
    pub fn copy_database(&self, path: &Path) -> Result<()> {
        let dir = path
            .parent()
            .filter(|p| !p.as_os_str().is_empty())
            .unwrap_or_else(|| Path::new("."));
        let staging = tempfile::NamedTempFile::new_in(dir)
            .map_err(|e| Error::Invalid(format!("No se pudo preparar la copia: {e}")))?;
        {
            let db = self.0.lock().map_err(|_| Error::Lock)?;
            db.backup(
                rusqlite::MAIN_DB,
                staging.path(),
                None::<fn(rusqlite::backup::Progress)>,
            )?;
        }
        check_library_backup(staging.path())?;
        staging
            .as_file()
            .sync_all()
            .map_err(|e| Error::Invalid(format!("No se pudo guardar la copia: {e}")))?;
        if let Err(err) = staging.persist(path) {
            #[cfg(windows)]
            if err.error.kind() == std::io::ErrorKind::PermissionDenied
                && std::fs::metadata(path).is_ok_and(|m| m.is_file() && m.len() == 0)
            {
                use std::io::{Seek, SeekFrom};
                let mut src = err.file;
                src.as_file_mut()
                    .seek(SeekFrom::Start(0))
                    .map_err(|e| Error::Invalid(format!("No se pudo guardar la copia: {e}")))?;
                let mut dst = std::fs::OpenOptions::new()
                    .write(true)
                    .truncate(true)
                    .open(path)
                    .map_err(|e| Error::Invalid(format!("No se pudo guardar la copia: {e}")))?;
                std::io::copy(src.as_file_mut(), &mut dst)
                    .map_err(|e| Error::Invalid(format!("No se pudo guardar la copia: {e}")))?;
                dst.sync_all()
                    .map_err(|e| Error::Invalid(format!("No se pudo guardar la copia: {e}")))?;
                return Ok(());
            }
            return Err(Error::Invalid(format!(
                "No se pudo guardar la copia: {}",
                err.error
            )));
        }
        Ok(())
    }
    /// Restore at startup, before opening sessions bound to a profile in the old library.
    pub fn restore_database_at_start(live: &Path, pending: &Path, safety: &Path) -> Result<()> {
        check_library_backup(pending)?;
        let mut target = Connection::open(live)?;
        let mut has_safety = false;
        if target.query_row(
            "SELECT EXISTS(SELECT 1 FROM sqlite_schema WHERE name='rust_catalog')",
            [],
            |r| r.get::<_, bool>(0),
        )? {
            target.backup(
                rusqlite::MAIN_DB,
                safety,
                None::<fn(rusqlite::backup::Progress)>,
            )?;
            check_library_backup(safety)?;
            has_safety = true;
        }
        if let Err(err) = target.restore(
            rusqlite::MAIN_DB,
            pending,
            None::<fn(rusqlite::backup::Progress)>,
        ) {
            if has_safety {
                let _ = target.restore(
                    rusqlite::MAIN_DB,
                    safety,
                    None::<fn(rusqlite::backup::Progress)>,
                );
            }
            return Err(err.into());
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn complete_backup_restores_profiles_and_saves_the_previous_database() {
        let dir = tempfile::tempdir().unwrap();
        let live = dir.path().join("live");
        let pending = dir.path().join("copy");
        let safety = dir.path().join("safety");
        let store = CatalogStore::open(&live).unwrap();
        store
            .edit_profile(crate::ProfileEdit::Create {
                name: "Primero".into(),
            })
            .unwrap();
        store.copy_database(&pending).unwrap();
        store
            .edit_profile(crate::ProfileEdit::Create {
                name: "Segundo".into(),
            })
            .unwrap();
        drop(store);
        CatalogStore::restore_database_at_start(&live, &pending, &safety).unwrap();
        assert_eq!(
            CatalogStore::open(&live)
                .unwrap()
                .profiles()
                .unwrap()
                .profiles
                .len(),
            2
        );
        assert_eq!(
            CatalogStore::open(&safety)
                .unwrap()
                .profiles()
                .unwrap()
                .profiles
                .len(),
            3
        );
        let bad = dir.path().join("other");
        Connection::open(&bad).unwrap();
        assert!(CatalogStore::restore_database_at_start(&live, &bad, &safety).is_err());
        assert_eq!(
            CatalogStore::open(live)
                .unwrap()
                .profiles()
                .unwrap()
                .profiles
                .len(),
            2
        );
    }

    #[test]
    fn copy_database_persists_into_existing_named_temp_file_and_checks_schema_guards() {
        let dir = tempfile::tempdir().unwrap();
        let live = dir.path().join("live.db");
        let store = CatalogStore::open(&live).unwrap();
        let temp = tempfile::NamedTempFile::new_in(dir.path()).unwrap();
        store.copy_database(temp.path()).unwrap();
        check_library_backup(temp.path()).unwrap();
        assert!(library_integrity(&live).unwrap());

        // Reject views in backup.
        let with_view = dir.path().join("view.db");
        store.copy_database(&with_view).unwrap();
        {
            let db = Connection::open(&with_view).unwrap();
            db.execute_batch("CREATE VIEW v_bad AS SELECT 1;").unwrap();
        }
        assert!(matches!(
            check_library_backup(&with_view),
            Err(Error::Invalid(_))
        ));

        // Reject missing rust_state singleton row.
        let no_state = dir.path().join("nostate.db");
        store.copy_database(&no_state).unwrap();
        {
            let db = Connection::open(&no_state).unwrap();
            db.execute("DELETE FROM rust_state", []).unwrap();
        }
        assert!(matches!(
            check_library_backup(&no_state),
            Err(Error::Invalid(_))
        ));

        // Reject empty rust_profile_selection as Error::Invalid (not QueryReturnedNoRows).
        let no_sel = dir.path().join("nosel.db");
        store.copy_database(&no_sel).unwrap();
        {
            let db = Connection::open(&no_sel).unwrap();
            db.execute("DELETE FROM rust_profile_selection", [])
                .unwrap();
        }
        assert!(matches!(
            check_library_backup(&no_sel),
            Err(Error::Invalid(_))
        ));
    }
}
