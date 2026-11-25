use crate::{with_document, work, CommandResult};
use librqbit::{
    api::TorrentIdOrHash, AddTorrent, AddTorrentOptions, Api, DhtSessionConfig, Magnet, Session,
    SessionOptions, SessionPersistenceConfig,
};
use serde::Serialize;
use std::{io::Read, path::PathBuf, sync::Arc, time::Duration};
use tauri::{Manager, State};
use tauri_plugin_dialog::DialogExt;
use tokio::sync::{Mutex, OnceCell};

pub struct Downloads {
    pub(crate) fast_resume: std::sync::atomic::AtomicBool,
    root: PathBuf,
    private_session: OnceCell<Arc<Session>>,
    session: Mutex<Option<Arc<Session>>>,
}

impl Downloads {
    pub fn new(root: PathBuf) -> Self {
        Self {
            root,
            fast_resume: true.into(),
            private_session: OnceCell::new(),
            session: Mutex::new(None),
        }
    }
    async fn public(&self, create: bool, resume: bool) -> CommandResult<Option<Arc<Session>>> {
        let mut session = self.session.lock().await;
        if session.is_none() {
            if !create {
                if !resume {
                    return Ok(None);
                }
                let path = self.root.join("session/session.json");
                let saved = work(move || {
                    let file = match std::fs::File::open(path) {
                        Ok(file) => file,
                        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
                            return Ok(false)
                        }
                        Err(error) => return Err(error.to_string()),
                    };
                    #[derive(serde::Deserialize)]
                    struct Persisted {
                        torrents: std::collections::HashMap<String, serde::de::IgnoredAny>,
                    }
                    let state: Persisted = serde_json::from_reader(std::io::BufReader::new(file))
                        .map_err(|e| e.to_string())?;
                    Ok(!state.torrents.is_empty())
                })
                .await?;
                if !saved {
                    return Ok(None);
                }
            }
            let files = self.root.join("files");
            tokio::fs::create_dir_all(&files)
                .await
                .map_err(|e| e.to_string())?;
            *session = Some(
                Session::new_with_opts(
                    files,
                    SessionOptions {
                        persistence: Some(SessionPersistenceConfig::Json {
                            folder: Some(self.root.join("session")),
                        }),
                        fastresume: self.fast_resume.load(std::sync::atomic::Ordering::Relaxed),
                        dht: Some(DhtSessionConfig {
                            persistence: None,
                            ..Default::default()
                        }),
                        ..Default::default()
                    },
                )
                .await
                .map_err(|e| format!("No se pudo iniciar BitTorrent. {e}"))?,
            );
        }
        Ok(session.clone())
    }

    async fn stop_empty_public(&self, current: &Arc<Session>) {
        let mut slot = self.session.lock().await;
        if slot
            .as_ref()
            .is_some_and(|session| Arc::ptr_eq(session, current))
            && current.with_torrents(|torrents| torrents.next().is_none())
        {
            current.stop().await;
            *slot = None;
        }
    }
    async fn private(&self) -> CommandResult<&Arc<Session>> {
        self.private_session
            .get_or_try_init(|| async {
                let folder = self.root.join("private-files");
                std::fs::create_dir_all(&folder).map_err(|e| e.to_string())?;
                Session::new_with_opts(
                    folder,
                    SessionOptions {
                        dht: None,
                        disable_local_service_discovery: true,
                        persistence: None,
                        listen: None,
                        ..Default::default()
                    },
                )
                .await
                .map_err(|_| "No se pudo iniciar la sesión privada.".to_owned())
            })
            .await
    }
    pub(super) async fn for_id(&self, id: usize) -> CommandResult<(Arc<Session>, usize)> {
        if id >= PRIVATE_ID {
            Ok((
                self.private_session
                    .get()
                    .cloned()
                    .ok_or("No se encontró la descarga.")?,
                id - PRIVATE_ID,
            ))
        } else {
            Ok((
                self.public(false, true)
                    .await?
                    .ok_or("No se encontró la descarga.")?,
                id,
            ))
        }
    }
}

const PRIVATE_ID: usize = 1 << 30;

#[derive(serde::Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PrivateTracker {
    endpoint: String,
    #[serde(default)]
    requires_authentication: bool,
    credential_key: Option<String>,
}
fn tracker_url(raw: &str) -> CommandResult<reqwest::Url> {
    reqwest::Url::parse(raw)
        .ok()
        .filter(|u| {
            u.scheme() == "https"
                && u.host_str().is_some()
                && u.username().is_empty()
                && u.password().is_none()
                && u.fragment().is_none()
        })
        .ok_or("El tracker privado requiere una dirección HTTPS válida.".into())
}
fn resolve_tracker(route: &PrivateTracker) -> CommandResult<reqwest::Url> {
    let base = tracker_url(&route.endpoint)?;
    if !route.requires_authentication {
        return Ok(base);
    }
    let default_key = format!(
        "HTTPS_{}{}",
        base.host_str().unwrap_or(""),
        base.port().map(|p| format!("_{p}")).unwrap_or_default()
    );
    let key = route
        .credential_key
        .as_deref()
        .filter(|k| !k.trim().is_empty())
        .unwrap_or(&default_key);
    let suffix = key
        .split(|c: char| !c.is_ascii_alphanumeric())
        .filter(|w| !w.is_empty())
        .collect::<Vec<_>>()
        .join("_")
        .to_ascii_uppercase();
    if suffix.is_empty() || suffix.len() > 200 {
        return Err("La clave de credencial del tracker es inválida.".into());
    }
    let name = format!("UMD_TRACKER_ENDPOINT_{suffix}");
    let secret = std::env::var(&name)
        .map_err(|_| format!("Configurá {name} con la dirección autorizada del tracker."))?;
    let authorized = tracker_url(secret.trim())?;
    if authorized.origin() != base.origin() {
        return Err("La credencial pertenece a otro tracker.".into());
    }
    Ok(authorized)
}
// Preserve the exact info dictionary bytes, including extensions covered by the info hash.
// Only the selected tracker receives the private announcement.
fn private_torrent(info: &[u8], tracker: &reqwest::Url) -> Vec<u8> {
    let address = tracker.as_str();
    let mut data = format!("d8:announce{}:{address}4:info", address.len()).into_bytes();
    data.extend_from_slice(info);
    data.push(b'e');
    data
}

type Owners = std::collections::BTreeMap<String, std::collections::BTreeSet<String>>;
static OWNERS: tokio::sync::Mutex<()> = tokio::sync::Mutex::const_new(());
fn owners(store: &umd_core::CatalogStore) -> CommandResult<Owners> {
    store
        .setting("torrent-owners")
        .map_err(|e| e.to_string())?
        .map(|s| {
            serde_json::from_str(&s).map_err(|_| "Registro de descargas inválido.".to_string())
        })
        .transpose()
        .map(Option::unwrap_or_default)
}
fn save_owners(store: &umd_core::CatalogStore, owners: &Owners) -> CommandResult<()> {
    store
        .set_setting(
            "torrent-owners",
            &serde_json::to_string(owners).map_err(|e| e.to_string())?,
        )
        .map_err(|e| e.to_string())
}
pub async fn clear_profile(
    downloads: &Downloads,
    store: &umd_core::CatalogStore,
    profile: uuid::Uuid,
) -> CommandResult<()> {
    let _guard = OWNERS.lock().await;
    let mut owners = owners(store)?;
    let user = profile.to_string();
    let keys: Vec<_> = owners
        .iter()
        .filter(|(_, users)| users.contains(&user))
        .map(|(key, _)| key.clone())
        .collect();
    for key in keys {
        let members = owners.get_mut(&key).unwrap();
        if members.len() == 1 {
            let (private, raw) = key.split_once(':').ok_or("Identidad torrent inválida.")?;
            let id = raw.parse().map_err(|_| "Identidad torrent inválida.")?;
            let session = if private == "private" {
                downloads.private_session.get().cloned()
            } else {
                downloads.public(false, true).await?
            };
            if let Some(session) =
                session.filter(|session| session.get(TorrentIdOrHash::Hash(id)).is_some())
            {
                session
                    .delete(TorrentIdOrHash::Hash(id), true)
                    .await
                    .map_err(|_| "No se pudo eliminar una descarga del perfil.")?;
                if private != "private" {
                    downloads.stop_empty_public(&session).await;
                }
            }
        }
        members.remove(&user);
        if members.is_empty() {
            owners.remove(&key);
        }
        save_owners(store, &owners)?;
    }
    Ok(())
}
#[tauri::command]
#[allow(clippy::too_many_arguments)]
pub async fn add_download(
    app: tauri::AppHandle,
    downloads: State<'_, Downloads>,
    magnet: Option<String>,
    metadata: Option<String>,
    private_trackers: Option<Vec<PrivateTracker>>,
    force_private: Option<bool>,
    profile_id: Option<uuid::Uuid>,
    store: State<'_, umd_core::CatalogStore>,
) -> CommandResult<Option<usize>> {
    let marked = magnet
        .as_deref()
        .and_then(|m| reqwest::Url::parse(m.trim()).ok())
        .is_some_and(|u| {
            u.query_pairs().any(|(k, v)| {
                k.eq_ignore_ascii_case("x.umd.private")
                    && (v == "1" || v.eq_ignore_ascii_case("true"))
            })
        });
    let private = marked || private_trackers.is_some() || force_private == Some(true);
    let magnet = magnet.as_deref().map(str::trim).filter(|s| !s.is_empty());
    let parsed_magnet = magnet
        .map(|s| {
            if s.len() > 16384 || !s.starts_with("magnet:?") {
                return Err("Enlace magnet inválido.");
            }
            Magnet::parse(s).map_err(|_| "Enlace magnet inválido.")
        })
        .transpose()?;
    let expected = parsed_magnet
        .as_ref()
        .map(|m| m.as_id20().ok_or("Se requiere un torrent v1 o híbrido."))
        .transpose()?;
    let (source, hash, use_private) = if let (Some(text), Some(hash), false, None) =
        (magnet, expected, private, metadata.as_ref())
    {
        (AddTorrent::from_url(text.to_owned()), hash, false)
    } else {
        let bytes = if let Some(encoded) = metadata {
            use base64::{engine::general_purpose::STANDARD, Engine};
            if encoded.len() > 12 * 1024 * 1024 {
                return Err("Metadatos demasiado grandes.".into());
            }
            STANDARD
                .decode(encoded)
                .map_err(|_| "Metadatos torrent inválidos.")?
        } else if let (Some(m), Some(hash), true) = (&parsed_magnet, expected, private) {
            let tracker = match private_trackers.as_deref() {
                Some(routes) if !routes.is_empty() => resolve_tracker(&routes[0])?,
                _ => tracker_url(
                    m.trackers
                        .first()
                        .ok_or("El magnet privado requiere un tracker HTTPS.")?,
                )?,
            };
            let mut url = reqwest::Url::parse("magnet:?").unwrap();
            url.query_pairs_mut()
                .append_pair("xt", &format!("urn:btih:{}", hash.as_string()))
                .append_pair("tr", tracker.as_str());
            let session = downloads.private().await?;
            let response = tokio::time::timeout(
                Duration::from_secs(120),
                session.add_torrent(
                    AddTorrent::from_url(url.to_string()),
                    Some(AddTorrentOptions {
                        list_only: true,
                        ..Default::default()
                    }),
                ),
            )
            .await
            .map_err(|_| "El tracker privado no respondió a tiempo.")?
            .map_err(|_| "No se pudieron obtener los metadatos privados.")?;
            let librqbit::AddTorrentResponse::ListOnly(result) = response else {
                return Err("La descarga privada no devolvió metadatos.".into());
            };
            result.torrent_bytes.to_vec()
        } else {
            let selected = work(move || {
                let Some(path) = app
                    .dialog()
                    .file()
                    .add_filter("BitTorrent", &["torrent"])
                    .blocking_pick_file()
                else {
                    return Ok(None);
                };
                let mut opts = tauri_plugin_fs::OpenOptions::new();
                opts.read(true);
                with_document(&app, path, opts, |file| {
                    let mut data = Vec::new();
                    file.take(8 * 1024 * 1024 + 1)
                        .read_to_end(&mut data)
                        .map_err(|e| e.to_string())?;
                    Ok(Some(data))
                })
            })
            .await?;
            let Some(data) = selected else {
                return Ok(None);
            };
            data
        };
        if bytes.len() > 8 * 1024 * 1024 {
            return Err("El archivo .torrent supera 8 MiB.".into());
        }
        let parsed = librqbit::torrent_from_bytes(&bytes)
            .map_err(|_| "El archivo .torrent no es válido.")?;
        let hash = parsed.info_hash;
        if expected.is_some_and(|id| id != hash) {
            return Err("Los metadatos no corresponden al magnet.".into());
        }
        if private || parsed.info.data.private {
            if !parsed.info.data.private {
                return Err("Los metadatos no declaran un torrent privado.".into());
            }
            let embedded = parsed
                .iter_announce()
                .map(|t| {
                    std::str::from_utf8(t.0)
                        .map_err(|_| "El tracker privado es inválido.".to_owned())
                        .and_then(tracker_url)
                })
                .collect::<CommandResult<Vec<_>>>()?;
            let chosen = if let Some(routes) = private_trackers.as_ref().filter(|r| !r.is_empty()) {
                let mut selected = None;
                for route in routes {
                    let public = tracker_url(&route.endpoint)?;
                    let authorized = resolve_tracker(route)?;
                    if embedded.contains(&public) || embedded.contains(&authorized) {
                        selected = Some(authorized);
                        break;
                    }
                }
                selected.ok_or("Los trackers del archivo no coinciden con los del catálogo.")?
            } else {
                embedded
                    .into_iter()
                    .next()
                    .ok_or("Falta el tracker privado.")?
            };
            (
                AddTorrent::from_bytes(private_torrent(parsed.info.raw_bytes.0, &chosen)),
                hash,
                true,
            )
        } else {
            (AddTorrent::from_bytes(bytes), hash, false)
        }
    };
    let _owner_guard = OWNERS.lock().await;
    let session = if use_private {
        downloads.private().await?.clone()
    } else {
        downloads
            .public(true, true)
            .await?
            .ok_or("No se pudo iniciar BitTorrent.")?
    };
    let task = session.add_torrent(
        source,
        Some(AddTorrentOptions {
            sub_folder: Some(hash.as_string()),
            overwrite: use_private,
            ..Default::default()
        }),
    );
    let response = tokio::time::timeout(Duration::from_secs(120), task)
        .await
        .map_err(|_| "Ningún par respondió a tiempo con los metadatos del torrent.")?
        .map_err(|e| {
            if use_private {
                "No se pudo agregar la descarga privada.".into()
            } else {
                e.to_string()
            }
        })?;
    if let Some(id) = profile_id {
        store.for_profile(id).map_err(|e| e.to_string())?;
        let key = format!(
            "{}:{}",
            if use_private { "private" } else { "public" },
            hash.as_string()
        );
        let mut data = owners(&store)?;
        let members = data.entry(key).or_default();
        if matches!(response, librqbit::AddTorrentResponse::Added(..)) {
            members.clear();
        } else if members.is_empty() {
            members.insert("preexisting".into());
        }
        members.insert(id.to_string());
        save_owners(&store, &data)?;
    }
    Ok(response
        .into_handle()
        .map(|h| h.id() + if use_private { PRIVATE_ID } else { 0 }))
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Download {
    id: usize,
    name: String,
    state: String,
    ready: bool,
    received: u64,
    total: u64,
    error: Option<String>,
}

#[tauri::command]
pub async fn list_downloads(
    active_only: Option<bool>,
    downloads: State<'_, Downloads>,
) -> CommandResult<Vec<Download>> {
    let mut results = Vec::new();
    for (session, offset) in [
        (downloads.public(false, active_only != Some(true)).await?, 0),
        (downloads.private_session.get().cloned(), PRIVATE_ID),
    ] {
        let Some(session) = session else { continue };
        results.extend(session.with_torrents(|items| {
            items
                .map(|(id, h)| {
                    let stats = h.stats();
                    Download {
                        id: id + offset,
                        name: h.name().unwrap_or_else(|| h.info_hash().as_string()),
                        state: stats.state.to_string(),
                        ready: stats.finished,
                        received: stats.progress_bytes,
                        total: stats.total_bytes,
                        error: if offset == 0 {
                            stats.error
                        } else {
                            stats.error.map(|_| "Error de descarga privada.".into())
                        },
                    }
                })
                .collect::<Vec<_>>()
        }));
    }
    Ok(results)
}

#[tauri::command]
pub async fn pause_download(
    id: usize,
    paused: bool,
    downloads: State<'_, Downloads>,
) -> CommandResult<()> {
    let (session, id) = downloads.for_id(id).await?;
    let h = session
        .get(TorrentIdOrHash::Id(id))
        .ok_or("Descarga inexistente.")?;
    if paused {
        session.pause(&h).await
    } else {
        session.unpause(&h).await
    }
    .map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn delete_download(
    id: usize,
    delete_files: bool,
    downloads: State<'_, Downloads>,
) -> CommandResult<()> {
    let _owner_guard = OWNERS.lock().await;
    let public = id < PRIVATE_ID;
    let (session, id) = downloads.for_id(id).await?;
    session
        .delete(TorrentIdOrHash::Id(id), delete_files)
        .await
        .map_err(|e| e.to_string())?;
    if public {
        downloads.stop_empty_public(&session).await;
    }
    Ok(())
}

#[tauri::command]
pub async fn download_files(
    id: usize,
    downloads: State<'_, Downloads>,
) -> CommandResult<Vec<(usize, String, bool)>> {
    let (session, id) = downloads.for_id(id).await?;
    let api = Api::new(session.clone(), None);
    let details = api
        .api_torrent_details(TorrentIdOrHash::Id(id))
        .map_err(|e| e.to_string())?;
    Ok(details
        .files
        .unwrap_or_default()
        .into_iter()
        .enumerate()
        .map(|(i, f)| (i, f.name, f.included))
        .collect())
}

#[tauri::command]
pub async fn choose_download_files(
    id: usize,
    files: std::collections::HashSet<usize>,
    downloads: State<'_, Downloads>,
) -> CommandResult<()> {
    let (session, id) = downloads.for_id(id).await?;
    let api = Api::new(session.clone(), None);
    let details = api
        .api_torrent_details(TorrentIdOrHash::Id(id))
        .map_err(|_| "La descarga todavía no tiene metadatos.")?;
    let count = details.files.as_deref().map_or(0, |items| items.len());
    if files.is_empty() || files.iter().any(|i| *i >= count) {
        return Err("Seleccioná al menos un archivo de la descarga.".into());
    }
    api.api_torrent_action_update_only_files(TorrentIdOrHash::Id(id), &files)
        .await
        .map_err(|_| "No se pudo cambiar la selección.")?;
    Ok(())
}

#[tauri::command]
pub async fn open_download(
    id: usize,
    index: usize,
    downloads: State<'_, Downloads>,
    app: tauri::AppHandle,
    media: State<'_, crate::media::MediaState>,
) -> CommandResult<crate::media::OpenedMedia> {
    open_files(id, &[index], false, &downloads, &app, &media).await
}
#[tauri::command]
pub async fn catalog_download_files(
    hash: String,
    private: bool,
    indices: Vec<usize>,
    sequence: bool,
    downloads: State<'_, Downloads>,
    app: tauri::AppHandle,
    media: State<'_, crate::media::MediaState>,
) -> CommandResult<crate::media::OpenedMedia> {
    if hash.len() != 40
        || !hash.bytes().all(|c| c.is_ascii_hexdigit())
        || indices.is_empty()
        || indices.len() > 2000
        || (!sequence && indices.len() != 1)
    {
        return Err("Referencia al torrent inválida.".into());
    }
    let session = if private {
        downloads.private_session.get().cloned()
    } else {
        downloads.public(false, true).await?
    }
    .ok_or("Agregá el torrent de esta edición a Descargas.")?;
    let id = session
        .get(TorrentIdOrHash::Hash(
            hash.parse().map_err(|_| "Hash de torrent inválido.")?,
        ))
        .map(|h| h.id())
        .ok_or("Agregá el torrent de esta edición a Descargas.")?;
    open_files(
        id + if private { PRIVATE_ID } else { 0 },
        &indices,
        sequence,
        &downloads,
        &app,
        &media,
    )
    .await
}
async fn open_files(
    id: usize,
    indices: &[usize],
    sequence: bool,
    downloads: &Downloads,
    app: &tauri::AppHandle,
    media: &crate::media::MediaState,
) -> CommandResult<crate::media::OpenedMedia> {
    let request = media.begin()?;
    let directory = if id >= PRIVATE_ID {
        "private-files"
    } else {
        "files"
    };
    let (session, id) = downloads.for_id(id).await?;
    let h = session
        .get(TorrentIdOrHash::Id(id))
        .ok_or("Descarga inexistente.")?;
    if !h.stats().finished {
        return Err("La descarga todavía no terminó.".into());
    }
    let api = Api::new(session.clone(), None);
    let details = api
        .api_torrent_details(TorrentIdOrHash::Id(id))
        .map_err(|e| e.to_string())?;
    let files = details.files.unwrap_or_default();
    let root = downloads
        .root
        .join(directory)
        .join(h.info_hash().as_string())
        .canonicalize()
        .map_err(|e| e.to_string())?;
    let mut paths = Vec::new();
    for &index in indices {
        let file = files
            .get(index)
            .filter(|f| f.included)
            .ok_or("Archivo no disponible.")?;
        let path = file
            .components
            .iter()
            .fold(h.output_folder().to_path_buf(), |p, c| p.join(c))
            .canonicalize()
            .map_err(|e| e.to_string())?;
        if !path.starts_with(&root) || !path.is_file() {
            return Err("Archivo fuera de la descarga.".into());
        }
        paths.push(path);
    }
    if sequence {
        let opened = crate::media::open_image_sequence(app, paths)?;
        media.keep(request, None)?;
        return Ok(opened);
    }
    let app = app.clone();
    let path = paths.pop().ok_or("Falta el archivo.")?;
    let cache = app.path().app_cache_dir().map_err(|e| e.to_string())?;
    let (opened, temp) =
        work(move || crate::media::open_downloaded_file(&app, &path, &cache)).await?;
    media.keep(request, temp)?;
    Ok(opened)
}
#[cfg(test)]
pub(crate) mod tests {
    use super::*;
    use librqbit::ListenerOptions;
    use std::net::{Ipv4Addr, SocketAddr};

    pub(crate) async fn local_session(path: PathBuf) -> Arc<Session> {
        Session::new_with_opts(
            path,
            SessionOptions {
                listen: Some(ListenerOptions {
                    listen_addr: SocketAddr::from((Ipv4Addr::LOCALHOST, 0)),
                    ipv4_only: true,
                    ..Default::default()
                }),
                ipv4_only: true,
                dht: None,
                disable_trackers: true,
                disable_local_service_discovery: true,
                ..Default::default()
            },
        )
        .await
        .unwrap()
    }

    #[test]
    fn selecting_private_tracker_keeps_the_original_info_hash() {
        let info = [
            b"d6:lengthi3e4:name8:clip.mp412:piece lengthi16384e6:pieces20:".as_slice(),
            &[23; 20],
            b"7:privatei1ee",
        ]
        .concat();
        let orig = tracker_url("https://tracker.example/announce").unwrap();
        let first = private_torrent(&info, &orig);
        let parsed = librqbit::torrent_from_bytes(&first).unwrap();
        let auth = tracker_url("https://tracker.example/session/fixture").unwrap();
        let changed = private_torrent(parsed.info.raw_bytes.0, &auth);
        let result = librqbit::torrent_from_bytes(&changed).unwrap();
        assert!(result.info.data.private);
        assert_eq!(parsed.info_hash, result.info_hash);
        assert_eq!(
            result.iter_announce().map(|v| v.0).collect::<Vec<_>>(),
            vec![auth.as_str().as_bytes()]
        );
        assert!(tracker_url("http://tracker.example/announce").is_err());
    }

    #[tokio::test]
    async fn downloads_a_complete_file_from_a_local_peer() {
        let sha1 = [
            0xa9, 0x99, 0x3e, 0x36, 0x47, 0x06, 0x81, 0x6a, 0xba, 0x3e, 0x25, 0x71, 0x78, 0x50,
            0xc2, 0x6c, 0x9c, 0xd0, 0xd8, 0x9d,
        ];
        let metadata = [
            b"d4:infod6:lengthi3e4:name8:clip.mp412:piece lengthi16384e6:pieces20:".as_slice(),
            &sha1,
            b"ee",
        ]
        .concat();
        let seed_dir = tempfile::tempdir().unwrap();
        let download_dir = tempfile::tempdir().unwrap();
        std::fs::write(seed_dir.path().join("clip.mp4"), b"abc").unwrap();
        let seed = local_session(seed_dir.path().into()).await;
        let client = local_session(download_dir.path().into()).await;
        let torrent = seed
            .add_torrent(
                AddTorrent::from_bytes(metadata.clone()),
                Some(AddTorrentOptions {
                    overwrite: true,
                    ..Default::default()
                }),
            )
            .await
            .unwrap()
            .into_handle()
            .unwrap();
        tokio::time::timeout(Duration::from_secs(10), torrent.wait_until_completed())
            .await
            .unwrap()
            .unwrap();
        let folder = torrent.info_hash().as_string();
        let transfer = client
            .add_torrent(
                AddTorrent::from_bytes(metadata),
                Some(AddTorrentOptions {
                    initial_peers: Some(vec![seed.listen_addr().unwrap()]),
                    sub_folder: Some(folder.clone()),
                    ..Default::default()
                }),
            )
            .await
            .unwrap()
            .into_handle()
            .unwrap();
        tokio::time::timeout(Duration::from_secs(25), transfer.wait_until_completed())
            .await
            .unwrap()
            .unwrap();
        assert_eq!(
            std::fs::read(download_dir.path().join(folder).join("clip.mp4")).unwrap(),
            b"abc"
        );
        client.stop().await;
        seed.stop().await;
    }
}

static MAGNETS: std::sync::Mutex<std::collections::VecDeque<String>> =
    std::sync::Mutex::new(std::collections::VecDeque::new());
fn queue_magnet(url: &reqwest::Url) {
    if url.scheme() == "magnet"
        && url.as_str().len() <= 16384
        && Magnet::parse(url.as_str()).is_ok()
    {
        if let Ok(mut pending) = MAGNETS.lock() {
            if pending.len() < 16 && !pending.iter().any(|v| v == url.as_str()) {
                pending.push_back(url.to_string());
            }
        }
    }
}
pub fn setup_magnets(app: &tauri::App) -> Result<(), Box<dyn std::error::Error>> {
    use tauri::Emitter;
    use tauri_plugin_deep_link::DeepLinkExt;
    let handle = app.handle().clone();
    app.deep_link().on_open_url(move |event| {
        for url in event.urls() {
            queue_magnet(&url);
        }
        let _ = handle.emit_to("main", "umd-magnet", ());
    });
    if let Some(urls) = app.deep_link().get_current()? {
        for url in urls {
            queue_magnet(&url);
        }
    }
    Ok(())
}
#[tauri::command]
pub fn next_magnet() -> Option<String> {
    MAGNETS.lock().ok()?.pop_front()
}
#[tauri::command]
pub fn register_magnets(app: tauri::AppHandle) -> CommandResult<()> {
    #[cfg(any(windows, target_os = "linux"))]
    {
        use tauri_plugin_deep_link::DeepLinkExt;
        app.deep_link()
            .register("magnet")
            .map_err(|e| e.to_string())?;
    }
    let _ = app;
    Ok(())
}
#[tauri::command]
pub async fn torrent_options(
    fast_resume: Option<bool>,
    app: tauri::AppHandle,
) -> CommandResult<bool> {
    work(move || {
        use std::sync::atomic::Ordering;
        let downloads = app.state::<Downloads>();
        if let Some(on) = fast_resume {
            static SAVE_OPTIONS: std::sync::Mutex<()> = std::sync::Mutex::new(());
            let _save = SAVE_OPTIONS.lock().map_err(|_| "Configuración ocupada.")?;
            app.state::<umd_core::CatalogStore>()
                .set_setting("torrent-fast-resume", if on { "on" } else { "off" })
                .map_err(|e| e.to_string())?;
            downloads.fast_resume.store(on, Ordering::Relaxed);
        }
        Ok(downloads.fast_resume.load(Ordering::Relaxed))
    })
    .await
}
