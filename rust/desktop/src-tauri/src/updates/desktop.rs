use super::{target, validate_identity};
use crate::CommandResult;
use std::{
    env,
    fs::{self, File},
    io::{self, Read, Seek},
    path::{Path, PathBuf},
    process::{Child, ChildStdin, Command, Stdio},
    sync::OnceLock,
    thread,
    time::{Duration, Instant},
};
use tempfile::TempDir;

const PREFIX: &str = ".umd-update-";
const APPLY: &str = "--apply-portable-update";
const STAGE: &str = "UMD_PORTABLE_UPDATE_STAGE";
const EXE: &str = if cfg!(windows) {
    "umd-desktop.exe"
} else {
    "umd-desktop"
};
const HELPER: &str = if cfg!(windows) { "apply.exe" } else { "apply" };
const STARTUP_LIMIT: Duration = Duration::from_secs(90);
static HANDOFF: OnceLock<ChildStdin> = OnceLock::new();
static RESTORED: OnceLock<bool> = OnceLock::new();

pub(super) struct Prepared {
    directory: TempDir,
}

fn components() -> CommandResult<Vec<(PathBuf, bool)>> {
    let mut paths = vec![
        ("umd-package.json".into(), false),
        ("LEEME.txt".into(), false),
    ];
    if cfg!(windows) {
        let arch = target()?.trim_start_matches("win-");
        paths.push((PathBuf::from("vlc").join(arch), true));
    } else {
        paths.extend([
            ("UMD".into(), false),
            ("libvlc.so".into(), false),
            ("libvlc".into(), true),
            ("licenses/libvlc".into(), true),
        ]);
    }
    paths.push((EXE.into(), false));
    Ok(paths)
}

fn invalid(message: &str) -> io::Error {
    io::Error::new(io::ErrorKind::InvalidData, message)
}

fn plain(metadata: &fs::Metadata) -> bool {
    #[cfg(windows)]
    {
        use std::os::windows::fs::MetadataExt;
        if metadata.file_attributes() & 0x400 != 0 {
            return false;
        }
    }
    !metadata.is_symlink() && (metadata.is_dir() || metadata.is_file())
}

fn matches_type(path: &Path, directory: bool) -> io::Result<bool> {
    match fs::symlink_metadata(path) {
        Ok(meta) if plain(&meta) && meta.is_dir() == directory => Ok(true),
        Ok(_) => Err(invalid(
            "La carpeta de UMD contiene archivos de instalación incompatibles.",
        )),
        Err(error) if error.kind() == io::ErrorKind::NotFound => Ok(false),
        Err(error) => Err(error),
    }
}

fn path_for_entry(name: &str) -> io::Result<PathBuf> {
    let name = name.strip_suffix('/').unwrap_or(name);
    for part in name.split('/') {
        if part.is_empty()
            || part.ends_with(['.', ' '])
            || part
                .chars()
                .any(|c| c.is_control() || "\\:\"<>|?*".contains(c))
        {
            return Err(invalid("El ZIP contiene una ruta inválida."));
        }
        if cfg!(windows) {
            let stem = part
                .split('.')
                .next()
                .unwrap_or("")
                .trim_end()
                .to_ascii_uppercase();
            if matches!(
                stem.as_str(),
                "CON" | "PRN" | "AUX" | "NUL" | "CONIN$" | "CONOUT$"
            ) || ["COM", "LPT"].iter().any(|prefix| {
                stem.strip_prefix(prefix)
                    .is_some_and(|tail| tail.chars().count() == 1 && "0123456789¹²³".contains(tail))
            }) {
                return Err(invalid("El ZIP contiene una ruta reservada."));
            }
        }
    }
    Ok(PathBuf::from(name))
}

fn extract(file: &mut File, destination: &Path, paths: &[(PathBuf, bool)]) -> io::Result<()> {
    file.rewind()?;
    let mut archive = zip::ZipArchive::new(file)?;
    if archive.len() > 4096 {
        return Err(invalid("El ZIP contiene demasiados archivos."));
    }
    let mut remaining = 4_u64 * 1024 * 1024 * 1024;
    for index in 0..archive.len() {
        let mut entry = archive.by_index(index)?;
        let relative = path_for_entry(entry.name())?;
        let is_dir = entry.is_dir();
        if entry.is_symlink()
            || (!entry.is_file() && !is_dir)
            || !paths.iter().any(|(path, directory)| {
                (relative == *path && is_dir == *directory)
                    || (*directory && relative != *path && relative.starts_with(path))
                    || (is_dir && relative != *path && path.starts_with(&relative))
            })
        {
            return Err(invalid(
                "El ZIP contiene archivos ajenos a la instalación portable.",
            ));
        }
        let out = destination.join(relative);
        if is_dir {
            fs::create_dir_all(out)?;
        } else {
            fs::create_dir_all(out.parent().expect("payload path has a parent"))?;
            let size = entry.size();
            remaining = remaining
                .checked_sub(size)
                .ok_or_else(|| invalid("El ZIP descomprimido supera el tamaño permitido."))?;
            let mut file = File::create_new(&out)?;
            if io::copy(&mut (&mut entry).take(size + 1), &mut file)? != size {
                return Err(invalid("Un archivo del ZIP tiene un tamaño incorrecto."));
            }
            #[cfg(unix)]
            {
                use std::os::unix::fs::PermissionsExt;
                let executable = entry.unix_mode().is_some_and(|mode| mode & 0o111 != 0);
                file.set_permissions(fs::Permissions::from_mode(if executable {
                    0o755
                } else {
                    0o644
                }))?;
            }
        }
    }
    for (relative, directory) in paths {
        if !matches_type(&destination.join(relative), *directory)? {
            return Err(invalid(
                "El ZIP no contiene una instalación portable completa.",
            ));
        }
    }
    #[cfg(unix)]
    for name in [EXE, "UMD"] {
        use std::os::unix::fs::PermissionsExt;
        if fs::metadata(destination.join(name))?.permissions().mode() & 0o100 == 0 {
            return Err(invalid(
                "El ZIP no conserva los permisos de ejecución de UMD.",
            ));
        }
    }
    Ok(())
}

fn installation() -> CommandResult<PathBuf> {
    let exe = env::current_exe()
        .and_then(fs::canonicalize)
        .map_err(|error| error.to_string())?;
    let root = exe
        .parent()
        .filter(|_| exe.file_name().is_some_and(|name| name == EXE))
        .ok_or("La aplicación debe ejecutarse desde una carpeta portable de UMD.")?;
    let identity = File::open(root.join("umd-package.json"))
        .map_err(|_| "Falta el archivo umd-package.json de la instalación portable.")?;
    validate_identity(
        identity,
        &semver::Version::parse(env!("CARGO_PKG_VERSION")).expect("package version"),
    )?;
    Ok(root.to_path_buf())
}

pub(super) fn prepare(mut file: File) -> CommandResult<Prepared> {
    let root = installation()?;
    let directory = tempfile::Builder::new()
        .prefix(PREFIX)
        .tempdir_in(&root)
        .map_err(|error| format!("No se puede escribir en la carpeta de UMD: {error}"))?;
    let paths = components()?;
    extract(&mut file, &directory.path().join("payload"), &paths)
        .map_err(|error| error.to_string())?;
    // Check existing ancestors before the app exits. The helper checks them
    // again before touching any installed files.
    check_destinations(&root, &paths).map_err(|error| error.to_string())?;
    fs::copy(root.join(EXE), directory.path().join(HELPER)).map_err(|error| error.to_string())?;
    Ok(Prepared { directory })
}

impl Prepared {
    pub(super) fn handoff(self) -> CommandResult<()> {
        if HANDOFF.get().is_some() {
            return Err("Ya hay una actualización en curso.".into());
        }
        let stage = self.directory.path();
        let mut command = Command::new(stage.join(HELPER));
        command
            .arg(APPLY)
            .current_dir(stage)
            .stdin(Stdio::piped())
            .stdout(Stdio::null())
            .stderr(Stdio::null());
        #[cfg(windows)]
        {
            use std::os::windows::process::CommandExt;
            command.creation_flags(0x0800_0000); // CREATE_NO_WINDOW
        }
        let mut child = command.spawn().map_err(|error| error.to_string())?;
        let pipe = child.stdin.take().expect("helper stdin is piped");
        HANDOFF
            .set(pipe)
            .map_err(|_| "Ya hay una actualización en curso.")?;
        let _ = self.directory.keep();
        Ok(())
    }
}

fn check_destinations(root: &Path, paths: &[(PathBuf, bool)]) -> io::Result<()> {
    for (relative, directory) in paths {
        let mut current = root.to_path_buf();
        for part in relative.parent().expect("relative path").components() {
            current.push(part);
            matches_type(&current, true)?;
        }
        matches_type(&root.join(relative), *directory)?;
    }
    Ok(())
}

fn transition(
    root: &Path,
    stage: &Path,
    paths: &[(PathBuf, bool)],
    changed: &mut Vec<(PathBuf, bool, bool)>,
) -> io::Result<()> {
    check_destinations(root, paths)?;
    let backup = stage.join("backup");
    fs::create_dir(&backup)?;
    for (relative, directory) in paths {
        let live = root.join(relative);
        let old = backup.join(relative);
        fs::create_dir_all(live.parent().expect("installation parent"))?;
        fs::create_dir_all(old.parent().expect("backup parent"))?;
        let existed = matches_type(&live, *directory)?;
        if existed {
            rename(&live, &old)?;
        }
        changed.push((relative.clone(), existed, false));
        rename(&stage.join("payload").join(relative), &live)?;
        changed.last_mut().expect("entry just added").2 = true;
    }
    Ok(())
}

fn rename(source: &Path, destination: &Path) -> io::Result<()> {
    // Windows may take a moment to release handles held by the old process.
    #[cfg(windows)]
    for _ in 0..20 {
        match fs::rename(source, destination) {
            Err(error) if matches!(error.raw_os_error(), Some(32 | 33)) => {
                thread::sleep(Duration::from_millis(100));
            }
            result => return result,
        }
    }
    fs::rename(source, destination)
}

fn rollback(root: &Path, stage: &Path, changed: &[(PathBuf, bool, bool)]) -> io::Result<()> {
    for (relative, had_old, installed) in changed.iter().rev() {
        let live = root.join(relative);
        if *installed {
            rename(&live, &stage.join("payload").join(relative))?;
        }
        if *had_old {
            rename(&stage.join("backup").join(relative), &live)?;
        }
    }
    Ok(())
}

fn launch(root: &Path, stage: &Path) -> io::Result<Child> {
    Command::new(root.join(if cfg!(windows) { EXE } else { "UMD" }))
        .current_dir(root)
        .env(STAGE, stage)
        .stdin(Stdio::null())
        .spawn()
}

fn wait_for_start(child: &mut Child, stage: &Path) -> io::Result<()> {
    let start = Instant::now();
    while start.elapsed() < STARTUP_LIMIT {
        if child.try_wait()?.is_some() {
            return Err(invalid("La nueva versión se cerró antes de iniciar."));
        }
        if stage.join("ready").is_file() {
            return Ok(());
        }
        thread::sleep(Duration::from_millis(100));
    }
    child.kill()?;
    child.wait()?;
    Err(invalid("La nueva versión no inició a tiempo."))
}

fn apply(stage: &Path, root: &Path) -> io::Result<()> {
    // EOF is tied to the original process, without polling or reusing a PID.
    if io::stdin().read(&mut [0])? != 0 {
        return Err(invalid(
            "La solicitud para instalar la actualización es inválida.",
        ));
    }
    let paths = components().map_err(|e| invalid(&e))?;
    let lock = File::options()
        .create(true)
        .truncate(false)
        .read(true)
        .write(true)
        .open(root.join(".umd-update.lock"))?;
    lock.try_lock().map_err(io::Error::from)?;
    let mut changed = Vec::new();
    let result = transition(root, stage, &paths, &mut changed)
        .and_then(|()| wait_for_start(&mut launch(root, stage)?, stage));
    if let Err(error) = result {
        let _ = fs::write(stage.join("error.log"), error.to_string());
        rollback(root, stage, &changed)?;
        fs::write(stage.join("restored"), [])?;
        launch(root, stage)?;
    }
    File::create_new(stage.join("complete"))?;
    Ok(())
}

fn is_stage(stage: &Path) -> bool {
    stage
        .file_name()
        .and_then(|value| value.to_str())
        .is_some_and(|name| name.starts_with(PREFIX))
        && fs::symlink_metadata(stage).is_ok_and(|meta| plain(&meta) && meta.is_dir())
}

pub(crate) fn run_helper() -> Option<i32> {
    if !env::args_os()
        .nth(1)
        .is_some_and(|argument| argument == APPLY)
    {
        return None;
    }
    let done = || -> io::Result<()> {
        let executable = env::current_exe()?;
        let stage = executable
            .parent()
            .filter(|path| is_stage(path))
            .ok_or_else(|| invalid("No se encontró la actualización preparada."))?;
        let root = stage
            .parent()
            .ok_or_else(|| invalid("Carpeta de UMD inválida."))?;
        if executable.file_name().is_none_or(|name| name != HELPER) {
            return Err(invalid(
                "Solo el proceso de actualización preparado puede instalar el ZIP.",
            ));
        }
        if let Err(error) = apply(stage, root) {
            let _ = fs::write(stage.join("handoff.log"), error.to_string());
            return Err(error);
        }
        Ok(())
    };
    Some(if done().is_ok() { 0 } else { 1 })
}

pub(crate) fn started() {
    let Some(directory) = env::var_os(STAGE).map(PathBuf::from) else {
        return;
    };
    let Ok(executable) = env::current_exe().and_then(fs::canonicalize) else {
        return;
    };
    if !is_stage(&directory)
        || fs::canonicalize(&directory)
            .ok()
            .as_deref()
            .and_then(Path::parent)
            != executable.parent()
    {
        return;
    }
    let restored = directory.join("restored").is_file();
    let _ = RESTORED.set(restored);
    if !restored {
        let _ = File::create_new(directory.join("ready"));
    }
    thread::spawn(move || {
        let start = Instant::now();
        let mut completed = false;
        while start.elapsed() < STARTUP_LIMIT {
            completed |= directory.join("complete").is_file();
            if completed && fs::remove_dir_all(&directory).is_ok() {
                break;
            }
            thread::sleep(Duration::from_millis(500));
        }
    });
}

pub(crate) fn notice() -> Option<String> {
    RESTORED
        .get()
        .copied()
        .unwrap_or(false)
        .then(|| "La actualización no se pudo iniciar. UMD restauró la versión anterior.".into())
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Write;

    #[test]
    fn archive_cannot_escape_staging_or_replace_unrelated_files() {
        let paths = components().unwrap();
        for name in [
            "../keep.txt",
            "vlc/../../keep.txt",
            "keep.txt",
            "C:/keep.txt",
            "libvlc/run:stream",
        ] {
            let dir = tempfile::tempdir().unwrap();
            let payload = dir.path().join("payload");
            let path = dir.path().join("package.zip");
            let mut zip = zip::ZipWriter::new(File::create(&path).unwrap());
            zip.start_file(name, zip::write::SimpleFileOptions::default())
                .unwrap();
            zip.write_all(b"unexpected").unwrap();
            zip.finish().unwrap();
            assert!(extract(&mut File::open(path).unwrap(), &payload, &paths).is_err());
            assert!(!dir.path().join("keep.txt").exists());
            assert!(!payload.exists());
        }
    }

    #[test]
    fn a_partial_swap_restores_the_executable_and_preserves_the_library() {
        let install = tempfile::tempdir().unwrap();
        let stage = tempfile::Builder::new()
            .prefix(PREFIX)
            .tempdir_in(install.path())
            .unwrap();
        let payload = stage.path().join("payload");
        let paths = [
            (PathBuf::from(EXE), false),
            (PathBuf::from("umd-package.json"), false),
        ];
        fs::create_dir(&payload).unwrap();
        fs::write(install.path().join(EXE), b"old").unwrap();
        fs::write(install.path().join("umd-package.json"), b"old identity").unwrap();
        fs::write(install.path().join("library.db"), b"user data").unwrap();
        fs::write(payload.join(EXE), b"new").unwrap();
        let mut changed = Vec::new();
        assert!(transition(install.path(), stage.path(), &paths, &mut changed).is_err());
        rollback(install.path(), stage.path(), &changed).unwrap();
        assert_eq!(fs::read(install.path().join(EXE)).unwrap(), b"old");
        assert_eq!(
            fs::read(install.path().join("umd-package.json")).unwrap(),
            b"old identity"
        );
        assert_eq!(
            fs::read(install.path().join("library.db")).unwrap(),
            b"user data"
        );
    }

    #[test]
    #[ignore = "requiere el ZIP portable generado para la plataforma"]
    fn packaged_zip_extracts_all_components() {
        let path = env::var_os("UMD_TEST_PACKAGED_ZIP").expect("UMD_TEST_PACKAGED_ZIP");
        let dir = tempfile::tempdir().unwrap();
        extract(
            &mut File::open(path).unwrap(),
            dir.path(),
            &components().unwrap(),
        )
        .unwrap();
    }
}
