use rusqlite::{Connection, OpenFlags};
use sha2::{Digest, Sha256};
use std::{
    collections::BTreeMap,
    ffi::OsString,
    fs::{self, File},
    io::{self, Read},
    path::{Path, PathBuf},
};
mod dotnet;

type Error = Box<dyn std::error::Error + Send + Sync>;
type Result<T> = std::result::Result<T, Error>;
const DBS: [&str; 3] = ["catalog.db", "accounts.db", "crawler.db"];
const INVENTORY: &str = "umd-server-backup-v1.json";

fn lock(dir: &Path) -> Result<File> {
    Ok(File::options()
        .create(true)
        .truncate(false)
        .read(true)
        .write(true)
        .open(dir.join("maintenance.lock"))?)
}
pub fn service_lock(dir: &Path) -> Result<File> {
    let file = lock(dir)?;
    file.try_lock_shared()
        .map_err(|_| "Hay una operación de mantenimiento en curso.")?;
    Ok(file)
}

fn plain(path: &Path) -> Result<()> {
    let meta = fs::symlink_metadata(path)?;
    if meta.file_type().is_symlink() {
        return Err("El directorio contiene un enlace simbólico.".into());
    }
    #[cfg(windows)]
    {
        use std::os::windows::fs::MetadataExt;
        if meta.file_attributes() & 0x400 != 0 {
            return Err("El directorio contiene un punto de reanálisis.".into());
        }
    }
    Ok(())
}
fn readonly(path: &Path) -> Result<Connection> {
    plain(path)?;
    let db = Connection::open_with_flags(path, OpenFlags::SQLITE_OPEN_READ_ONLY)?;
    db.execute_batch("PRAGMA trusted_schema=OFF")?;
    Ok(db)
}
fn verified(db: &Connection) -> Result<()> {
    if db.query_row("PRAGMA integrity_check(1)", [], |r| r.get::<_, String>(0))? != "ok"
        || db.prepare("PRAGMA foreign_key_check")?.exists([])?
    {
        return Err("Falló la comprobación de integridad SQLite.".into());
    }
    Ok(())
}
fn snapshot(src: &Path, dst: &Path) -> Result<()> {
    let db = readonly(src)?;
    db.execute("VACUUM INTO ?1", [dst.to_str().ok_or("Ruta inválida")?])?;
    verified(&readonly(dst)?)
}
fn tree(source: &Path, dest: &Path) -> Result<()> {
    plain(source)?;
    if source.is_dir() {
        fs::create_dir(dest)?;
        for entry in fs::read_dir(source)? {
            let entry = entry?;
            tree(&entry.path(), &dest.join(entry.file_name()))?;
        }
    } else if source.is_file() {
        fs::copy(source, dest)?;
    } else {
        return Err("Tipo de archivo inesperado.".into());
    }
    Ok(())
}
fn inventory(root: &Path) -> Result<BTreeMap<String, String>> {
    fn scan(root: &Path, path: &Path, files: &mut BTreeMap<String, String>) -> Result<()> {
        plain(path)?;
        if path.is_dir() {
            for entry in fs::read_dir(path)? {
                scan(root, &entry?.path(), files)?;
            }
        } else if path.is_file() {
            let mut input = File::open(path)?;
            let mut hash = Sha256::new();
            io::copy(&mut input, &mut hash)?;
            files.insert(
                path.strip_prefix(root)?
                    .to_str()
                    .ok_or("Ruta inválida")?
                    .replace('\\', "/"),
                format!("{:x}", hash.finalize()),
            );
        } else {
            return Err("Tipo de archivo inesperado.".into());
        }
        Ok(())
    }
    let mut files = BTreeMap::new();
    for name in DBS.into_iter().chain(["releases", "packages"]) {
        let p = root.join(name);
        if p.exists() {
            scan(root, &p, &mut files)?;
        }
    }
    Ok(files)
}
fn staged(
    source: &Path,
    dest: &Path,
    operation: impl FnOnce(&Path, &Path) -> Result<()>,
) -> Result<()> {
    let source = source.canonicalize()?;
    if dest.try_exists()? {
        return Err("El destino ya existe. Elegí un directorio nuevo.".into());
    }
    let parent = dest
        .parent()
        .filter(|p| !p.as_os_str().is_empty())
        .unwrap_or(Path::new("."))
        .canonicalize()?;
    if parent.starts_with(&source) {
        return Err("El destino debe quedar fuera del origen.".into());
    }
    let temp = tempfile::Builder::new()
        .prefix(".umd-staging-")
        .tempdir_in(&parent)?;
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        fs::set_permissions(temp.path(), fs::Permissions::from_mode(0o700))?;
    }
    operation(&source, temp.path())?;
    let target = parent.join(dest.file_name().ok_or("Falta el directorio de destino.")?);
    if target.try_exists()? {
        return Err("El destino ya existe.".into());
    }
    fs::rename(temp.path(), target)?;
    Ok(())
}
fn backup(source: &Path, dest: &Path) -> Result<()> {
    let guard = lock(source)?;
    guard
        .try_lock()
        .map_err(|_| "Detené el servidor Rust y el rastreador antes de respaldar.")?;
    for name in DBS {
        snapshot(&source.join(name), &dest.join(name))?;
    }
    for name in ["packages", "releases"] {
        let p = source.join(name);
        if p.try_exists()? {
            tree(&p, &dest.join(name))?;
        }
    }
    fs::write(
        dest.join(INVENTORY),
        serde_json::to_vec_pretty(&inventory(dest)?)?,
    )?;
    Ok(())
}
fn restore(source: &Path, dest: &Path) -> Result<()> {
    let mut bytes = Vec::new();
    File::open(source.join(INVENTORY))?
        .take(64 * 1024 * 1024)
        .read_to_end(&mut bytes)?;
    let known: BTreeMap<String, String> = serde_json::from_slice(&bytes)?;
    for name in DBS {
        tree(&source.join(name), &dest.join(name))?;
    }
    for name in ["packages", "releases"] {
        let p = source.join(name);
        if p.try_exists()? {
            tree(&p, &dest.join(name))?;
        }
    }
    if inventory(dest)? != known {
        return Err("El contenido de la copia no coincide con sus hashes.".into());
    }
    for name in DBS {
        verified(&readonly(&dest.join(name))?)?;
    }
    Ok(())
}
pub fn run(directory: &Path, args: &[OsString]) -> Result<bool> {
    match args {
        [flag,src,dst] if flag=="--migrate-dotnet"=>staged(&PathBuf::from(src),&PathBuf::from(dst),dotnet::convert)?,
        [flag,dst] if flag=="--backup"=>staged(directory,&PathBuf::from(dst),backup)?,
        [flag,src,dst] if flag=="--restore"=>staged(&PathBuf::from(src),&PathBuf::from(dst),restore)?,
        []=>return Ok(false),
        [flag] if flag=="--scrape-once"=>return Ok(false),
        _=>return Err("Uso: umd-server [--scrape-once | --migrate-dotnet ORIGEN DESTINO | --backup DESTINO | --restore COPIA DESTINO]".into())
    }
    println!("Operación completada y verificada.");
    Ok(true)
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn backup_and_restore_check_files_and_never_replace_live_data() -> Result<()> {
        let test = tempfile::tempdir()?;
        let data = test.path().join("data");
        fs::create_dir(&data)?;
        for name in DBS {
            Connection::open(data.join(name))?.execute_batch(
                "CREATE TABLE marker(value INTEGER);INSERT INTO marker VALUES(42)",
            )?;
        }
        let packages = data.join("packages");
        fs::create_dir(&packages)?;
        fs::write(packages.join("release"), b"verified release")?;
        let saved = test.path().join("backup");
        let copy = test.path().join("restored");
        let guard = service_lock(&data)?;
        assert!(staged(&data, &saved, backup).is_err());
        drop(guard);
        staged(&data, &saved, backup)?;
        staged(&saved, &copy, restore)?;
        assert_eq!(
            readonly(&copy.join(DBS[0]))?
                .query_row("SELECT value FROM marker", [], |r| r.get::<_, i64>(0))?,
            42
        );
        assert!(staged(&saved, &copy, restore).is_err());
        fs::write(saved.join("packages/release"), b"damaged")?;
        let refused = test.path().join("refused");
        assert!(staged(&saved, &refused, restore).is_err());
        assert!(!refused.exists());
        Ok(())
    }
}
