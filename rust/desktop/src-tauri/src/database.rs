use crate::{with_document, work, CommandResult};
use std::{
    fs, io,
    io::Write,
    path::{Path, PathBuf},
};
use tauri::State;
use tauri_plugin_dialog::DialogExt;
use tauri_plugin_fs::OpenOptions;
use umd_core::{
    legacy_private_settings, prepare_library_restore, CatalogStore, LegacyPrivateSettings,
};

const DEEP_CHECK_INTERVAL: std::time::Duration = std::time::Duration::from_secs(7 * 24 * 60 * 60);

pub struct LocalDatabase {
    directory: PathBuf,
    notice: Option<String>,
    filename: String,
}
impl LocalDatabase {
    pub fn open(directory: PathBuf) -> CommandResult<(CatalogStore, Self)> {
        let marker = directory.join("active-library");
        let mut filename = match fs::read_to_string(&marker) {
            Ok(name)
                if name
                    .strip_prefix("catalog-")
                    .and_then(|s| s.strip_suffix(".db"))
                    .and_then(|s| uuid::Uuid::parse_str(s).ok())
                    .is_some() =>
            {
                name
            }
            Ok(_) => return Err("El selector de la biblioteca es inválido.".into()),
            Err(e) if e.kind() == io::ErrorKind::NotFound => "catalog.db".into(),
            Err(e) => return Err(e.to_string()),
        };
        let mut live = directory.join(&filename);
        let mut notice = None;
        // The page-by-page check grows with the library and delayed every
        // launch. It now runs when this library has not passed it within a
        // week; other launches only confirm the file opens as a database.
        let stamp = directory.join("integrity-checked");
        let checked_recently = fs::read_to_string(&stamp).is_ok_and(|name| name == filename)
            && fs::metadata(&stamp)
                .and_then(|m| m.modified())
                .ok()
                .and_then(|time| time.elapsed().ok())
                .is_some_and(|age| age < DEEP_CHECK_INTERVAL);
        let healthy = if checked_recently {
            umd_core::library_readable(&live)
        } else {
            umd_core::library_integrity(&live)
        }
        .map_err(|e| e.to_string())?;
        if healthy && !checked_recently {
            let _ = fs::write(&stamp, &filename);
        }
        if !healthy {
            let _ = fs::remove_file(&stamp);
            notice=Some(format!("SQLite detectó daños. UMD conservó la base original en {} y abrió una biblioteca nueva. Podés restaurar tu copia desde Perfiles.",live.display()));
            filename = format!("catalog-{}.db", uuid::Uuid::new_v4());
            live = directory.join(&filename);
            drop(CatalogStore::open(&live).map_err(|e| e.to_string())?);
            let mut choice =
                tempfile::NamedTempFile::new_in(&directory).map_err(|e| e.to_string())?;
            choice
                .write_all(filename.as_bytes())
                .and_then(|_| choice.as_file().sync_all())
                .map_err(|e| e.to_string())?;
            choice.persist(&marker).map_err(|e| e.to_string())?;
        }
        let package = directory.join("restore.pending");
        let bundled = package.join("library.umddb").is_file();
        if !bundled && package.is_dir() {
            let _ = fs::remove_file(package.join("profiles.json"));
            let _ = fs::remove_dir(&package);
        }
        let pending = if bundled {
            package.join("library.umddb")
        } else {
            directory.join("restore.pending.umddb")
        };
        if pending.is_file() {
            let previous =
                directory.join(format!("antes-de-restaurar-{}.umddb", uuid::Uuid::new_v4()));
            match CatalogStore::restore_database_at_start(&live, &pending, &previous) {
                Ok(()) => {
                    if bundled {
                        let private = package.join("profiles.json");
                        match fs::read(&private) {
                            Ok(contents) => {
                                let profiles: Vec<LegacyPrivateSettings> =
                                    serde_json::from_slice(&contents)
                                        .map_err(|_| "Ajustes privados del respaldo inválidos.")?;
                                let files = crate::private_files::PrivateFiles(
                                    directory.join("private-profiles"),
                                );
                                for profile in profiles {
                                    for (name, value) in [
                                        ("session", profile.session),
                                        ("metadata", profile.metadata),
                                    ] {
                                        if let Some(value) = value {
                                            files.save(profile.profile, name, &value)?;
                                        }
                                    }
                                }
                                fs::remove_file(private).map_err(|e| e.to_string())?;
                            }
                            Err(e) if e.kind() == io::ErrorKind::NotFound => {}
                            Err(e) => return Err(e.to_string()),
                        }
                    }
                    notice = Some(format!(
                        "Biblioteca restaurada. Copia anterior en {}.",
                        previous.display()
                    ));
                    fs::remove_file(&pending).map_err(|e| e.to_string())?;
                }
                Err(e) => {
                    if bundled {
                        let _ = fs::remove_file(package.join("profiles.json"));
                    }
                    let rejected = directory
                        .join(format!("respaldo-rechazado-{}.umddb", uuid::Uuid::new_v4()));
                    fs::rename(&pending, &rejected).map_err(|e| e.to_string())?;
                    notice = Some(format!("No se pudo restaurar la biblioteca. {e}"));
                }
            }
            if bundled {
                for file in ["library.umddb-wal", "library.umddb-shm"] {
                    let _ = fs::remove_file(package.join(file));
                }
                fs::remove_dir(&package).map_err(|e| e.to_string())?;
            }
        }
        // A library that fails to open gets the full check on the next launch.
        let store = CatalogStore::open(&live).map_err(|e| {
            let _ = fs::remove_file(&stamp);
            e.to_string()
        })?;
        Ok((
            store,
            Self {
                directory,
                notice,
                filename,
            },
        ))
    }
}
#[tauri::command]
pub fn library_notice(db: State<'_, LocalDatabase>) -> Option<String> {
    #[cfg(desktop)]
    if let Some(notice) = crate::updates::desktop::notice() {
        return Some(notice);
    }
    db.notice.clone()
}
#[tauri::command]
pub async fn database_copy(
    app: tauri::AppHandle,
    db: State<'_, LocalDatabase>,
    store: State<'_, CatalogStore>,
    restore: bool,
) -> CommandResult<bool> {
    let directory = db.directory.clone();
    let filename = db.filename.clone();
    let store = store.inner().clone();
    work(move || {
        let dialog = app.dialog().file();
        let file = if restore {
            dialog
                .add_filter(
                    "Biblioteca UMD o SQLite de .NET",
                    &["umddb", "umdbackup", "db", "sqlite", "sqlite3"],
                )
                .blocking_pick_file()
        } else {
            dialog
                .add_filter("Biblioteca UMD", &["umddb"])
                .set_file_name("biblioteca.umddb")
                .blocking_save_file()
        };
        let Some(file) = file else { return Ok(false) };
        // SQLite can create WAL sidecars even when validating a read-only
        // snapshot. Keep the entire staging directory scoped to this operation.
        let staging = tempfile::tempdir_in(&directory).map_err(|e| e.to_string())?;
        let mut temp =
            tempfile::NamedTempFile::new_in(staging.path()).map_err(|e| e.to_string())?;
        if restore {
            let mut opts = OpenOptions::new();
            opts.read(true);
            with_document(&app, file, opts, |mut input| {
                io::copy(&mut input, &mut temp).map_err(|e| e.to_string())?;
                temp.flush().map_err(|e| e.to_string())
            })?;
            temp.as_file().sync_all().map_err(|e| e.to_string())?;
            let converted =
                tempfile::NamedTempFile::new_in(staging.path()).map_err(|e| e.to_string())?;
            static RESTORE: std::sync::Mutex<()> = std::sync::Mutex::new(());
            let _guard = RESTORE.lock().map_err(|_| "Restauración ocupada.")?;
            let pending = directory.join("restore.pending.umddb");
            let package = directory.join("restore.pending");
            if pending.exists() || package.exists() {
                return Err(
                    "Ya hay una restauración pendiente. Reiniciá UMD para aplicarla.".into(),
                );
            }
            if prepare_library_restore(temp.path(), converted.path()).map_err(|e| e.to_string())? {
                let restored = CatalogStore::open(converted.path()).map_err(|e| e.to_string())?;
                let profiles = restored.profiles().map_err(|e| e.to_string())?;
                let ids = profiles
                    .profiles
                    .into_iter()
                    .map(|p| uuid::Uuid::parse_str(&p.id))
                    .collect::<Result<Vec<_>, _>>()
                    .map_err(|_| "Identidad de perfil inválida.")?;
                drop(restored);
                let private = legacy_private_settings(temp.path(), &ids)
                    .map_err(|_| "No se pudieron leer los ajustes privados del respaldo.")?;
                drop(temp);
                let bundle = tempfile::tempdir_in(&directory).map_err(|e| e.to_string())?;
                converted.as_file().sync_all().map_err(|e| e.to_string())?;
                converted
                    .persist(bundle.path().join("library.umddb"))
                    .map_err(|e| e.to_string())?;
                let mut file =
                    tempfile::NamedTempFile::new_in(bundle.path()).map_err(|e| e.to_string())?;
                serde_json::to_writer(&mut file, &private)
                    .map_err(|_| "No se pudieron guardar los ajustes privados.")?;
                file.flush()
                    .and_then(|_| file.as_file().sync_all())
                    .map_err(|e| e.to_string())?;
                file.persist(bundle.path().join("profiles.json"))
                    .map_err(|e| e.to_string())?;
                fs::rename(bundle.path(), package)
                    .map_err(|e| format!("No se pudo preparar la restauración. {e}"))?;
            } else {
                temp.persist_noclobber(pending)
                    .map_err(|e| format!("No se pudo preparar la restauración. {e}"))?;
            }
        } else {
            if let Some(path) = file.as_path().and_then(|p| p.canonicalize().ok()) {
                if directory
                    .join("restore.pending")
                    .canonicalize()
                    .ok()
                    .is_some_and(|bundle| path.starts_with(bundle))
                    || [
                        "catalog.db",
                        "catalog.db-wal",
                        "catalog.db-shm",
                        &filename,
                        &format!("{filename}-wal"),
                        &format!("{filename}-shm"),
                        "active-library",
                        "restore.pending.umddb",
                    ]
                    .iter()
                    .any(|name| {
                        directory.join(name).canonicalize().ok().as_deref()
                            == Some(Path::new(&path))
                    })
                {
                    return Err("Elegí un archivo distinto de la biblioteca activa.".into());
                }
            }
            store
                .copy_database(temp.path())
                .map_err(|e| e.to_string())?;
            let mut input = fs::File::open(temp.path()).map_err(|e| e.to_string())?;
            #[cfg(not(any(target_os = "android", target_os = "ios")))]
            {
                let dest = file.into_path().map_err(|e| e.to_string())?;
                let mut output = tempfile::NamedTempFile::new_in(
                    dest.parent().ok_or("Carpeta de destino inválida.")?,
                )
                .map_err(|e| e.to_string())?;
                io::copy(&mut input, &mut output).map_err(|e| e.to_string())?;
                output.as_file().sync_all().map_err(|e| e.to_string())?;
                output.persist(dest).map_err(|e| e.to_string())?;
            }
            #[cfg(any(target_os = "android", target_os = "ios"))]
            {
                let mut opts = OpenOptions::new();
                opts.write(true).create(true).truncate(true);
                with_document(&app, file, opts, |mut output| {
                    io::copy(&mut input, &mut output).map_err(|e| e.to_string())?;
                    output.sync_all().map_err(|e| e.to_string())
                })?;
            }
        }
        Ok(true)
    })
    .await
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn restores_legacy_profile_files_only_when_the_staged_library_is_applied() {
        let dir = tempfile::tempdir().unwrap();
        let pending = dir.path().join("restore.pending");
        fs::create_dir(&pending).unwrap();
        let backup = CatalogStore::open(pending.join("library.umddb")).unwrap();
        backup
            .set_setting("server_url", "https://example.com/")
            .unwrap();
        let profile = uuid::Uuid::parse_str(&backup.profiles().unwrap().active_id).unwrap();
        drop(backup);
        let credentials = serde_json::json!({"tmdb":"fixture","mal":"","language":"es-AR"});
        fs::write(
            pending.join("profiles.json"),
            serde_json::to_vec(&vec![LegacyPrivateSettings {
                profile,
                metadata: Some(credentials.clone()),
                session: None,
            }])
            .unwrap(),
        )
        .unwrap();
        let files = crate::private_files::PrivateFiles(dir.path().join("private-profiles"));
        assert!(files
            .read::<serde_json::Value>(profile, "metadata")
            .unwrap()
            .is_none());
        let (store, _) = LocalDatabase::open(dir.path().into()).unwrap();
        assert_eq!(
            store.setting("server_url").unwrap().as_deref(),
            Some("https://example.com/")
        );
        assert_eq!(
            files
                .read::<serde_json::Value>(profile, "metadata")
                .unwrap(),
            Some(credentials)
        );
        assert!(!pending.exists());
    }
    #[test]
    fn corrupt_file_remains_available_after_recovery_and_restart() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("catalog.db");
        fs::write(&path, [17; 4096]).unwrap();
        let (library, state) = LocalDatabase::open(dir.path().into()).unwrap();
        assert!(state.notice.is_some());
        assert_ne!(state.filename, "catalog.db");
        drop(library);
        assert_eq!(fs::read(&path).unwrap(), vec![17; 4096]);
        let (_, again) = LocalDatabase::open(dir.path().into()).unwrap();
        assert_eq!(again.filename, state.filename);
        assert!(again.notice.is_none());
    }
    #[test]
    fn a_recently_checked_library_still_recovers_from_an_unreadable_file() {
        let dir = tempfile::tempdir().unwrap();
        let (library, state) = LocalDatabase::open(dir.path().into()).unwrap();
        assert_eq!(state.filename, "catalog.db");
        drop(library);
        // The first launch ran the deep check and recorded it.
        assert_eq!(
            fs::read_to_string(dir.path().join("integrity-checked")).unwrap(),
            "catalog.db"
        );
        for sidecar in ["catalog.db-wal", "catalog.db-shm"] {
            let _ = fs::remove_file(dir.path().join(sidecar));
        }
        fs::write(dir.path().join("catalog.db"), [17; 4096]).unwrap();
        let (_, recovered) = LocalDatabase::open(dir.path().into()).unwrap();
        assert!(recovered.notice.is_some());
        assert_ne!(recovered.filename, "catalog.db");
    }
}
