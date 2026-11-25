use crate::{remote::private_url, CommandResult};
use base64::{engine::general_purpose::STANDARD, Engine};
use p256::{
    ecdsa::{signature::hazmat::PrehashVerifier, Signature, VerifyingKey},
    pkcs8::DecodePublicKey,
};
use reqwest::{redirect, Client, StatusCode, Url};
use semver::Version;
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
#[cfg(windows)]
use std::os::windows::fs::OpenOptionsExt;
use std::{fs::File, io, path::PathBuf, time::Duration};
use tauri::{ipc::Channel, AppHandle, State};
use tempfile::TempDir;
use tokio::{io::AsyncWriteExt, sync::Mutex};
use umd_core::CatalogStore;
use uuid::Uuid;

#[cfg(desktop)]
pub(crate) mod desktop;

const CHANNEL: &str = if cfg!(target_os = "android") {
    "tauri-stable"
} else {
    "tauri-portable"
};
const KEY_ID: &str = "umd-release-1";
const PUBLIC_KEY: &str = include_str!("../umd-release-1.public.spki.base64");
const MAX_PACKAGE: u64 = 2 * 1024 * 1024 * 1024;
const MAX_MANIFEST: usize = 128 * 1024;

fn target() -> CommandResult<&'static str> {
    match (std::env::consts::OS, std::env::consts::ARCH) {
        ("windows", "x86_64") => Ok("win-x64"),
        ("windows", "aarch64") => Ok("win-arm64"),
        ("windows", "x86") => Ok("win-x86"),
        ("linux", "x86_64") => Ok("linux-x64"),
        ("linux", "aarch64") => Ok("linux-arm64"),
        ("android", "aarch64") => Ok("android-arm64"),
        ("android", "arm") => Ok("android-arm"),
        ("android", "x86_64") => Ok("android-x64"),
        ("android", "x86") => Ok("android-x86"),
        _ => Err("Esta arquitectura no tiene un paquete de UMD.".into()),
    }
}
const EXTENSION: &str = if cfg!(target_os = "android") {
    ".apk"
} else {
    ".zip"
};
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Manifest {
    version: String,
    channel: String,
    runtime_identifier: String,
    download_uri: String,
    sha256: String,
    size_bytes: u64,
    signing_key_id: String,
    signature_algorithm: String,
    signature: String,
    release_notes: Option<String>,
}
#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Offer {
    id: Uuid,
    version: Version,
    size_bytes: u64,
    notes: Option<String>,
}
struct Candidate {
    offer: Offer,
    source: Url,
    url: Url,
    digest: [u8; 32],
}
#[derive(Default)]
struct UpdateState {
    candidate: Option<Candidate>,
    #[cfg(desktop)]
    prepared: Option<desktop::Prepared>,
}

#[derive(Default)]
pub struct Updates(Mutex<UpdateState>);

fn trusted_transport(url: &Url) -> bool {
    url.username().is_empty()
        && url.password().is_none()
        && url.host().is_some()
        && (url.scheme() == "https"
            || url.scheme() == "http"
                && matches!(url.host_str(), Some("localhost" | "127.0.0.1" | "[::1]")))
}
fn client() -> CommandResult<Client> {
    Client::builder()
        .connect_timeout(Duration::from_secs(10))
        .read_timeout(Duration::from_secs(30))
        .redirect(redirect::Policy::custom(|r| {
            if r.previous().len() >= 5
                || !trusted_transport(r.url())
                || (r.url().scheme() == "http"
                    && r.previous().iter().any(|p| p.scheme() == "https"))
            {
                r.error("Redirección de actualización inválida.")
            } else {
                r.follow()
            }
        }))
        .build()
        .map_err(|e| e.to_string())
}
struct Trust {
    id: &'static str,
    key: VerifyingKey,
}
fn trusted_signer() -> CommandResult<Trust> {
    let (id, encoded) =
        match (
            option_env!("UMD_UPDATE_SIGNING_KEY_ID"),
            option_env!("UMD_UPDATE_SIGNING_PUBLIC_KEY"),
        ) {
            (None, None) => (KEY_ID, PUBLIC_KEY),
            (Some(id), Some(key)) if !id.trim().is_empty() => (id, key),
            _ => return Err(
                "La compilación debe incluir el identificador y la clave pública de actualización."
                    .into(),
            ),
        };
    let bytes = STANDARD
        .decode(encoded.trim())
        .map_err(|_| "Clave pública de UMD inválida.")?;
    let key =
        VerifyingKey::from_public_key_der(&bytes).map_err(|_| "Clave pública de UMD inválida.")?;
    Ok(Trust { id, key })
}
fn checked_manifest(
    m: Manifest,
    source: &Url,
    current: &Version,
    trust: &Trust,
) -> CommandResult<Option<Candidate>> {
    let version = Version::parse(m.version.strip_prefix('v').unwrap_or(&m.version))
        .map_err(|_| "Versión inválida.")?;
    let url = Url::parse(&m.download_uri).map_err(|_| "Dirección del paquete inválida.")?;
    if !version.pre.is_empty()
        || !version.build.is_empty()
        || version.major > 255
        || version.minor > 255
        || version.patch > 65535
        || m.channel != CHANNEL
        || m.runtime_identifier != target()?
        || m.signing_key_id != trust.id
        || m.signature_algorithm != "ECDSA-P256-SHA256"
        || !trusted_transport(&url)
        || (source.scheme() == "https" && url.scheme() != "https")
        || !url.path().to_ascii_lowercase().ends_with(EXTENSION)
        || !(1..=MAX_PACKAGE).contains(&m.size_bytes)
    {
        return Err(
            "El manifiesto no corresponde a una actualización de UMD para este equipo.".into(),
        );
    }
    if m.sha256.len() != 64 || !m.sha256.bytes().all(|b| b.is_ascii_hexdigit()) {
        return Err("SHA-256 inválido.".into());
    }
    let mut digest = [0; 32];
    for (i, b) in digest.iter_mut().enumerate() {
        *b =
            u8::from_str_radix(&m.sha256[i * 2..i * 2 + 2], 16).map_err(|_| "SHA-256 inválido.")?;
    }
    let sig = STANDARD
        .decode(m.signature.trim())
        .ok()
        .and_then(|bytes| Signature::from_slice(&bytes).ok())
        .ok_or("Firma de actualización inválida.")?;
    trust
        .key
        .verify_prehash(&digest, &sig)
        .map_err(|_| "La firma de esta actualización no es válida.")?;
    if version <= *current {
        return Ok(None);
    }
    Ok(Some(Candidate {
        offer: Offer {
            id: Uuid::new_v4(),
            version,
            size_bytes: m.size_bytes,
            notes: m.release_notes,
        },
        source: source.clone(),
        url,
        digest,
    }))
}
async fn discover(
    http: &Client,
    base: &Url,
    current: &Version,
    trust: &Trust,
) -> CommandResult<Option<Candidate>> {
    let url = base
        .join(&format!("api/v1/releases/{CHANNEL}/{}/latest", target()?))
        .map_err(|e| e.to_string())?;
    let mut res = http
        .get(url)
        .timeout(Duration::from_secs(25))
        .send()
        .await
        .map_err(|e| e.to_string())?;
    if res.status() == StatusCode::NOT_FOUND {
        return Ok(None);
    }
    res.error_for_status_ref().map_err(|e| e.to_string())?;
    let mut bytes = Vec::new();
    while let Some(chunk) = res.chunk().await.map_err(|e| e.to_string())? {
        if chunk.len() > MAX_MANIFEST - bytes.len() {
            return Err("El manifiesto supera el tamaño permitido.".into());
        }
        bytes.extend_from_slice(&chunk);
    }
    let manifest = serde_json::from_slice(&bytes)
        .map_err(|_| "El servidor devolvió un manifiesto inválido.")?;
    checked_manifest(manifest, base, current, trust)
}
fn source(store: &CatalogStore) -> CommandResult<Url> {
    let address = store
        .setting("server_url")
        .map_err(|e| e.to_string())?
        .filter(|s| !s.is_empty())
        .ok_or(
            "Conectá tu servidor desde «Conectar servidor» para consultar sus actualizaciones.",
        )?;
    private_url(&address)
}
#[tauri::command]
pub async fn check_update(
    app: AppHandle,
    store: State<'_, CatalogStore>,
    updates: State<'_, Updates>,
) -> CommandResult<Option<Offer>> {
    let mut slot = updates
        .0
        .try_lock()
        .map_err(|_| "Ya hay una actualización en curso.")?;
    *slot = UpdateState::default();
    let found = discover(
        &client()?,
        &source(&store)?,
        &app.package_info().version,
        &trusted_signer()?,
    )
    .await?;
    let offer = found.as_ref().map(|c| c.offer.clone());
    slot.candidate = found;
    Ok(offer)
}

#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase", tag = "phase")]
pub enum Progress {
    Download {
        bytes: u64,
    },
    Verify,
    #[cfg(desktop)]
    Extract,
    #[cfg(target_os = "android")]
    Install,
}
struct Staged {
    #[cfg_attr(target_os = "android", allow(dead_code))]
    // Holds the verified file through the Android handoff.
    file: File,
    #[cfg(any(test, target_os = "android"))]
    path: PathBuf,
    // Close the verified file before cleaning up its directory.
    _directory: TempDir,
}

#[cfg(desktop)]
fn validate_identity(reader: impl io::Read, version: &Version) -> CommandResult<()> {
    use std::io::Read;
    #[derive(Deserialize)]
    #[serde(rename_all = "camelCase")]
    struct Identity {
        product: String,
        version: Version,
        runtime_identifier: String,
    }
    let mut bytes = Vec::new();
    reader
        .take(4097)
        .read_to_end(&mut bytes)
        .map_err(|e| e.to_string())?;
    if bytes.len() > 4096 {
        return Err("Identidad del paquete inválida.".into());
    }
    let identity: Identity =
        serde_json::from_slice(&bytes).map_err(|_| "Identidad del paquete inválida.")?;
    if identity.product != "org.universalmediadistribution.rust"
        || identity.version != *version
        || identity.runtime_identifier != target()?
    {
        return Err("El producto, la plataforma o la versión del ZIP no coinciden.".into());
    }
    Ok(())
}

#[cfg(desktop)]
fn inspect(file: &File, version: &Version) -> CommandResult<()> {
    let mut archive = zip::ZipArchive::new(file).map_err(|_| "El ZIP de UMD no es válido.")?;
    let info = archive
        .by_name("umd-package.json")
        .map_err(|_| "Falta la identidad del paquete.")?;
    validate_identity(info, version)?;
    let exe = if cfg!(windows) {
        "umd-desktop.exe"
    } else {
        "umd-desktop"
    };
    if !archive
        .by_name(exe)
        .is_ok_and(|file| file.is_file() && file.size() > 0)
    {
        return Err("Falta el ejecutable de UMD en el ZIP.".into());
    }
    Ok(())
}
async fn stage(
    http: &Client,
    c: &Candidate,
    progress: impl Fn(Progress),
    root: Option<&std::path::Path>,
) -> CommandResult<Staged> {
    let mut builder = tempfile::Builder::new();
    builder.prefix("UMD-update-");
    let dir = root
        .map_or_else(|| builder.tempdir(), |root| builder.tempdir_in(root))
        .map_err(|e| e.to_string())?;
    let path = dir.path().join(format!("UMD{EXTENSION}"));
    let mut res = http
        .get(c.url.clone())
        .timeout(Duration::from_secs(15 * 60))
        .send()
        .await
        .map_err(|e| e.to_string())?
        .error_for_status()
        .map_err(|e| e.to_string())?;
    let size = c.offer.size_bytes;
    if res.content_length().is_some_and(|n| n != size) {
        return Err("El tamaño del paquete no coincide.".into());
    }
    let mut file = tokio::fs::File::create_new(&path)
        .await
        .map_err(|e| e.to_string())?;
    let mut written = 0;
    let mut last = 0;
    while let Some(chunk) = res.chunk().await.map_err(|e| e.to_string())? {
        if chunk.len() as u64 > size - written {
            return Err("El paquete excede el tamaño anunciado.".into());
        }
        file.write_all(&chunk).await.map_err(|e| e.to_string())?;
        written += chunk.len() as u64;
        if written - last >= 512 * 1024 || written == size {
            progress(Progress::Download { bytes: written });
            last = written;
        }
    }
    file.flush().await.map_err(|e| e.to_string())?;
    drop(file);
    if written != size {
        return Err("La descarga está incompleta.".into());
    }
    progress(Progress::Verify);
    let expected = c.digest;
    #[cfg(desktop)]
    let version = c.offer.version.clone();
    crate::work(move || {
        // Use this same verified file handle for extraction.
        let mut options = File::options();
        options.read(true);
        #[cfg(windows)]
        options.share_mode(1);
        let mut lock = options.open(&path).map_err(|e| e.to_string())?;
        let mut hash = Sha256::new();
        io::copy(&mut lock, &mut hash).map_err(|e| e.to_string())?;
        if <[u8; 32]>::from(hash.finalize()) != expected {
            return Err("El SHA-256 del paquete descargado no coincide con la firma.".into());
        }
        #[cfg(desktop)]
        inspect(&lock, &version)?;
        Ok(Staged {
            _directory: dir,
            #[cfg(any(test, target_os = "android"))]
            path,
            file: lock,
        })
    })
    .await
}

#[tauri::command]
pub async fn install_update(
    _app: AppHandle,
    id: Uuid,
    store: State<'_, CatalogStore>,
    updates: State<'_, Updates>,
    progress: Channel<Progress>,
) -> CommandResult<String> {
    let mut slot = updates
        .0
        .try_lock()
        .map_err(|_| "Ya hay una actualización en curso.")?;
    let c = slot
        .candidate
        .as_ref()
        .filter(|c| c.offer.id == id)
        .ok_or("Volvé a buscar la actualización.")?;
    if source(&store)? != c.source {
        return Err("Cambió el servidor. Volvé a buscar la actualización.".into());
    }
    #[cfg(target_os = "android")]
    let root = {
        use tauri::Manager;
        let d = _app.path().app_cache_dir().map_err(|e| e.to_string())?;
        std::fs::create_dir_all(&d).map_err(|e| e.to_string())?;
        Some(d)
    };
    #[cfg(desktop)]
    let root: Option<PathBuf> = None;
    #[cfg(desktop)]
    if slot.prepared.is_some() {
        return Ok("La actualización está lista para reiniciar.".into());
    }
    let staged = stage(
        &client()?,
        c,
        |p| {
            let _ = progress.send(p);
        },
        root.as_deref(),
    )
    .await?;
    #[cfg(target_os = "android")]
    {
        let _ = progress.send(Progress::Install);
        let directory = root
            .as_ref()
            .ok_or("Falta la carpeta de actualización.")?
            .join("verified-updates");
        tokio::fs::create_dir_all(&directory)
            .await
            .map_err(|e| e.to_string())?;
        let package = directory.join(format!("{}.apk", Uuid::new_v4()));
        tokio::fs::rename(&staged.path, &package)
            .await
            .map_err(|e| e.to_string())?;
        if let Err(e) = handoff_android(&_app, &package, &c.offer.version.to_string()).await {
            let _ = tokio::fs::remove_file(package).await;
            return Err(e);
        }
        slot.candidate = None;
        Ok("Seguí los pasos del instalador de Android.".into())
    }
    #[cfg(desktop)]
    {
        let _ = progress.send(Progress::Extract);
        slot.prepared = Some(crate::work(move || desktop::prepare(staged.file)).await?);
        Ok("La actualización está lista para reiniciar.".into())
    }
}

#[cfg(desktop)]
#[tauri::command]
pub async fn restart_update(
    app: AppHandle,
    id: Uuid,
    store: State<'_, CatalogStore>,
    updates: State<'_, Updates>,
) -> CommandResult<()> {
    let mut slot = updates
        .0
        .try_lock()
        .map_err(|_| "Ya hay una actualización en curso.")?;
    let candidate = slot
        .candidate
        .as_ref()
        .filter(|c| c.offer.id == id)
        .ok_or("Volvé a buscar la actualización.")?;
    if source(&store)? != candidate.source {
        return Err("Cambió el servidor. Volvé a buscar la actualización.".into());
    }
    let prepared = slot
        .prepared
        .take()
        .ok_or("La actualización todavía no está lista.")?;
    crate::work(move || prepared.handoff()).await?;
    slot.candidate = None;
    app.exit(0);
    Ok(())
}
#[cfg(target_os = "android")]
async fn handoff_android(
    app: &AppHandle,
    path: &std::path::Path,
    version: &str,
) -> CommandResult<()> {
    use jni::objects::JValue;
    use tauri::Manager;
    let main = app
        .get_webview_window("main")
        .ok_or("Falta la ventana principal.")?;
    let path = path.to_string_lossy().into_owned();
    let version = version.to_string();
    let (tx, rx) = tokio::sync::oneshot::channel();
    main.with_webview(move |web| {
        web.jni_handle().exec(move |env, activity, _| {
            let result = (|| -> jni::errors::Result<bool> {
                let path = env.new_string(path)?;
                let version = env.new_string(version)?;
                env.call_method(
                    activity,
                    "installVerifiedApk",
                    "(Ljava/lang/String;Ljava/lang/String;)Z",
                    &[JValue::Object(&path), JValue::Object(&version)],
                )?
                .z()
            })();
            if result.is_err() {
                let _ = env.exception_clear();
            }
            let _ = tx.send(result.unwrap_or(false));
        })
    })
    .map_err(|e| e.to_string())?;
    if rx.await.unwrap_or(false) {
        Ok(())
    } else {
        Err("El instalador no corresponde a esta aplicación o no se pudo abrir.".into())
    }
}

#[cfg(all(test, desktop))]
mod tests {
    use super::*;
    use axum::{routing::get, Json, Router};
    use p256::ecdsa::{signature::hazmat::PrehashSigner, SigningKey};
    use serde_json::json;
    use std::io::Cursor;

    fn zip_fixture(product: &str, version: &str) -> Vec<u8> {
        use std::io::Write;
        let mut archive = zip::ZipWriter::new(Cursor::new(Vec::new()));
        let options = zip::write::SimpleFileOptions::default();
        archive.start_file("umd-package.json", options).unwrap();
        let identity =
            json!({"product": product, "version": version, "runtimeIdentifier": target().unwrap()});
        archive.write_all(identity.to_string().as_bytes()).unwrap();
        archive
            .start_file(
                if cfg!(windows) {
                    "umd-desktop.exe"
                } else {
                    "umd-desktop"
                },
                options,
            )
            .unwrap();
        archive.write_all(b"test binary").unwrap();
        archive.finish().unwrap().into_inner()
    }
    fn signer() -> (SigningKey, Trust) {
        let key = SigningKey::random(&mut p256::elliptic_curve::rand_core::OsRng);
        let trust = Trust {
            id: "qa",
            key: *key.verifying_key(),
        };
        (key, trust)
    }
    fn manifest(bytes: &[u8], url: &str, key: &SigningKey, version: &str) -> serde_json::Value {
        let hash = Sha256::digest(bytes);
        let sig: Signature = key.sign_prehash(&hash).unwrap();
        json!({"version":version,"channel":CHANNEL,"runtimeIdentifier":target().unwrap(),
            "downloadUri":url,"sizeBytes":bytes.len(),"sha256":format!("{hash:x}"),"releaseNotes":"Notas",
            "signingKeyId":"qa","signatureAlgorithm":"ECDSA-P256-SHA256","signature":STANDARD.encode(sig.to_bytes())})
    }
    #[test]
    fn trust_target_and_newer_version_are_checked_before_offering() {
        trusted_signer().unwrap();
        let (sign, trust) = signer();
        let base = Url::parse("https://updates.example/").unwrap();
        let v = Version::new(1, 0, 0);
        let doc = manifest(
            b"package",
            "https://updates.example/umd.zip",
            &sign,
            "1.1.0",
        );
        let check = |doc: serde_json::Value| {
            checked_manifest(serde_json::from_value(doc).unwrap(), &base, &v, &trust)
        };
        assert!(check(doc.clone()).unwrap().is_some());
        for (field, bad) in [
            ("channel", json!("stable")),
            ("runtimeIdentifier", json!("wrong-platform")),
            ("signingKeyId", json!("other")),
            ("sha256", json!("00".repeat(32))),
            ("signature", json!(STANDARD.encode([0; 64]))),
            ("downloadUri", json!("http://127.0.0.1/file.zip")),
            ("sizeBytes", json!(MAX_PACKAGE + 1)),
        ] {
            let mut changed = doc.clone();
            changed[field] = bad;
            assert!(check(changed).is_err(), "{field}");
        }
        for published in ["1.0.0", "0.9.0"] {
            let mut changed = doc.clone();
            changed["version"] = json!(published);
            assert!(check(changed).unwrap().is_none());
        }
    }
    async fn receive_fixture(
        signed: &[u8],
        served: Vec<u8>,
        version: &str,
    ) -> CommandResult<Staged> {
        let (key, trust) = signer();
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let base = Url::parse(&format!(
            "http://{}/library/",
            listener.local_addr().unwrap()
        ))
        .unwrap();
        let info = manifest(
            signed,
            base.join("package.zip").unwrap().as_str(),
            &key,
            version,
        );
        let route = format!("/library/api/v1/releases/{CHANNEL}/{}/latest", target()?);
        let app = Router::new()
            .route(&route, get(move || async move { Json(info) }))
            .route("/library/package.zip", get(move || async move { served }));
        let server = tokio::spawn(async move { axum::serve(listener, app).await.unwrap() });
        let http = client()?;
        let candidate = discover(&http, &base, &Version::new(0, 0, 0), &trust)
            .await?
            .unwrap();
        let result = stage(&http, &candidate, |_| {}, None).await;
        server.abort();
        result
    }
    #[tokio::test]
    async fn only_matching_verified_archives_can_be_staged() {
        let good = zip_fixture("org.universalmediadistribution.rust", "1.1.0");
        let staged = receive_fixture(&good, good.clone(), "1.1.0").await.unwrap();
        let path = staged.path.clone();
        #[cfg(windows)]
        assert!(File::options().write(true).open(&path).is_err());
        drop(staged);
        assert!(!path.exists());
        let mut corrupt = good.clone();
        corrupt[0] ^= 1;
        assert!(receive_fixture(&good, corrupt, "1.1.0")
            .await
            .err()
            .unwrap()
            .contains("SHA-256"));
        assert!(receive_fixture(&good, good[..9].to_vec(), "1.1.0")
            .await
            .is_err());
        assert!(receive_fixture(&good, good.clone(), "2.0.0")
            .await
            .err()
            .unwrap()
            .contains("versión"));
        let other = zip_fixture(&Uuid::new_v4().to_string(), "1.1.0");
        assert!(receive_fixture(&other, other.clone(), "1.1.0")
            .await
            .err()
            .unwrap()
            .contains("producto"));
    }
    #[tokio::test]
    #[ignore = "requiere el ZIP portable generado para la plataforma"]
    async fn packaged_zip_passes_update_verification() {
        let path = std::env::var_os("UMD_TEST_PACKAGED_ZIP").expect("UMD_TEST_PACKAGED_ZIP");
        let bytes = std::fs::read(path).unwrap();
        let staged = receive_fixture(&bytes, bytes.clone(), env!("CARGO_PKG_VERSION"))
            .await
            .unwrap();
        assert_eq!(
            std::fs::metadata(&staged.path).unwrap().len(),
            bytes.len() as u64
        );
    }
}
