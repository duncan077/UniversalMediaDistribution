use crate::CommandResult;
use adblock::{
    lists::{FilterSet, ParseOptions, RuleTypes},
    request::Request,
    Engine,
};
use reqwest::Url;
use serde::{Deserialize, Serialize};
use std::{
    path::{Path, PathBuf},
    sync::{LazyLock, Mutex, RwLock},
    time::{Duration, Instant},
};
use tauri::{Manager, State};
use umd_core::CatalogStore;

#[cfg(target_os = "android")]
mod android;
mod cosmetic;
mod device;
#[cfg(target_os = "linux")]
mod linux;
pub mod playback;
#[cfg(windows)]
mod windows;

const SEED: &str = include_str!("seed.txt");
const LISTS: &[(&str, &str)] = &[
    ("easylist", "https://easylist.to/easylist/easylist.txt"),
    (
        "easyprivacy",
        "https://easylist.to/easylist/easyprivacy.txt",
    ),
    (
        "ublock",
        "https://ublockorigin.github.io/uAssets/filters/filters.txt",
    ),
];
const OTHER_LISTS: &[(&str, &str)] = &[
    (
        "badware",
        "https://ublockorigin.github.io/uAssets/filters/badware.txt",
    ),
    (
        "privacy",
        "https://ublockorigin.github.io/uAssets/filters/privacy.txt",
    ),
    (
        "quick-fixes",
        "https://ublockorigin.github.io/uAssets/filters/quick-fixes.txt",
    ),
    (
        "unbreak",
        "https://ublockorigin.github.io/uAssets/filters/unbreak.txt",
    ),
    ("peter-lowe", "https://pgl.yoyo.org/adservers/serverlist.php?hostformat=adblockplus&showintro=0&mimetype=plaintext"),
    (
        "annoyances",
        "https://easylist.to/easylist/fanboy-annoyance.txt",
    ),
];
const MAX_LIST: usize = 12 * 1024 * 1024;
const WEEK: Duration = Duration::from_secs(7 * 24 * 3600);
static BASIC: LazyLock<Engine> = LazyLock::new(|| compile(Vec::new(), false));
static STANDARD: RwLock<Option<Compiled>> = RwLock::new(None);
static MODE: Mutex<Mode> = Mutex::new(Mode::Basic);
static REFRESH: tokio::sync::Mutex<()> = tokio::sync::Mutex::const_new(());
const FILTER_IDLE: Duration = Duration::from_secs(120);
static LAST_PLAYER_USE: Mutex<Option<Instant>> = Mutex::new(None);

fn player_in_use() {
    let mut last = LAST_PLAYER_USE.lock().unwrap();
    if last.replace(Instant::now()).is_some() {
        return;
    }
    tauri::async_runtime::spawn(async {
        let mut delay = FILTER_IDLE;
        loop {
            tokio::time::sleep(delay).await;
            let mut last = LAST_PLAYER_USE.lock().unwrap();
            delay = last.map_or(Duration::ZERO, |time| {
                FILTER_IDLE.saturating_sub(time.elapsed())
            });
            if !delay.is_zero() {
                continue;
            }
            *last = None;
            *STANDARD.write().unwrap() = None;
            break;
        }
    });
}

#[cfg(target_os = "android")]
pub(crate) fn trim() {
    *STANDARD.write().unwrap() = None;
}

#[derive(Clone, Copy, PartialEq, Eq, Deserialize, Serialize)]
#[serde(rename_all = "lowercase")]
pub enum Mode {
    Off,
    Basic,
    Standard,
    Complete,
}
impl Mode {
    fn online(self) -> bool {
        matches!(self, Self::Standard | Self::Complete)
    }
}
#[derive(Clone, Copy, PartialEq, Eq)]
struct FilterProfile {
    complete: bool,
    constrained: bool,
}
struct Compiled {
    engine: Engine,
    cosmetics: cosmetic::Rules,
    profile: FilterProfile,
    #[cfg(target_os = "linux")]
    webkit: String,
}
fn payload(url: &Url) -> String {
    let mode = *MODE.lock().unwrap();
    let rules = STANDARD.read().unwrap();
    let active = rules.as_ref().filter(|_| mode.online());
    cosmetic::script(
        url.host_str().unwrap_or(""),
        active.map(|c| (&c.cosmetics, c.profile.constrained)),
        mode != Mode::Off,
    )
}

fn player_payload(url: &Url) -> String {
    format!(
        "{}\n;({})({});",
        payload(url),
        include_str!("../../player-assets/provider-frames.js"),
        include_str!("../../player-assets/provider-media.js"),
    )
}

fn full_regex(line: &str) -> bool {
    let line = line.trim();
    let line = line.strip_prefix("@@").unwrap_or(line);
    line.starts_with('/')
        && line.rsplit_once('/').is_some_and(|(pattern, tail)| {
            pattern.len() > 1 && (tail.is_empty() || tail.starts_with('$'))
        })
}

fn compile(lists: Vec<String>, skip_regex: bool) -> Engine {
    let mut set = FilterSet::new(false);
    for text in std::iter::once(SEED.to_owned()).chain(lists) {
        let text = if skip_regex {
            let mut filtered = String::with_capacity(text.len());
            for line in text.lines().filter(|line| !full_regex(line)) {
                filtered.push_str(line);
                filtered.push('\n');
            }
            filtered
        } else {
            text
        };
        set.add_filter_list(
            text,
            ParseOptions {
                rule_types: RuleTypes::NetworkOnly,
                ..Default::default()
            },
        );
    }
    Engine::new_with_filter_set(set)
}

/// Compiling the network rules from about 10 MB of list text takes seconds on
/// a television box and used to run on every launch before the first player
/// could open. The compiled engine is kept next to the lists and reused while
/// the list text is the same; any change in the lists rebuilds it.
fn compile_cached(lists: Vec<String>, directory: &Path, skip_regex: bool) -> Engine {
    use std::hash::{Hash, Hasher};
    let mut hasher = std::collections::hash_map::DefaultHasher::new();
    // The format belongs to this adblock release; bump the tag with the crate.
    ("adblock-0.13", SEED, skip_regex).hash(&mut hasher);
    for text in &lists {
        text.hash(&mut hasher);
    }
    let key = hasher.finish().to_le_bytes();
    let path = directory.join("engine.cache");
    if let Ok(bytes) = std::fs::read(&path) {
        if bytes.len() > key.len() && bytes[..key.len()] == key {
            let mut engine = Engine::default();
            if engine.deserialize(&bytes[key.len()..]).is_ok() {
                return engine;
            }
        }
    }
    let engine = compile(lists, skip_regex);
    if let Ok(mut file) = tempfile::NamedTempFile::new_in(directory) {
        use std::io::{BufWriter, Write};
        let mut writer = BufWriter::new(file.as_file_mut());
        let saved = writer
            .write_all(&key)
            .and_then(|()| writer.write_all(&engine.serialize()))
            .and_then(|()| writer.flush())
            .is_ok();
        drop(writer);
        if saved {
            let _ = file.persist(&path);
        }
    }
    engine
}

fn valid_list(text: &str) -> bool {
    let first = text.trim_start_matches('\u{feff}').trim_start();
    (first.starts_with("[Adblock") || first.starts_with('!') || first.starts_with("||"))
        && first
            .lines()
            .any(|s| s.starts_with("||") || s.contains("##"))
}

#[cfg(any(windows, target_os = "android"))]
fn provider_resource(address: &str) -> bool {
    let Ok(mut url) = Url::parse(address) else {
        return false;
    };
    match url.scheme() {
        "blob" | "data" => true,
        "about" => matches!(url.path(), "blank" | "srcdoc"),
        "wss" => url.set_scheme("https").is_ok() && umd_core::public_url::web_url(&url),
        _ => umd_core::public_url::web_url(&url),
    }
}

pub fn blocked(address: &str, page: &str, kind: &str, method: &str) -> bool {
    let mode = *MODE.lock().unwrap();
    if mode == Mode::Off {
        return false;
    }
    let Ok(url) = Url::parse(address) else {
        return false;
    };
    // Keep IPC, local media, the user's LAN server and custom protocols outside the filter.
    if !matches!(url.scheme(), "http" | "https" | "ws" | "wss") {
        return false;
    }
    let Some(host) = url.host_str() else {
        return false;
    };
    let host = host.trim_end_matches('.');
    if !host.contains('.')
        || host.ends_with(".localhost")
        || host.ends_with(".local")
        || host
            .trim_matches(['[', ']'])
            .parse::<std::net::IpAddr>()
            .is_ok_and(|ip| !umd_core::public_url::public_ip(ip))
    {
        return false;
    }
    let Ok(req) = Request::new(address, page, kind, method) else {
        return false;
    };
    // Preserve same-site playback assets explicitly identified as media, while
    // keeping the offline advertising rules active for known advertising URLs.
    let ext = url
        .path()
        .rsplit('.')
        .next()
        .unwrap_or("")
        .to_ascii_lowercase();
    if matches!(kind, "media" | "xmlhttprequest" | "other")
        && matches!(
            ext.as_str(),
            "m3u8" | "mpd" | "m4s" | "ts" | "aac" | "vtt" | "srt" | "ass"
        )
        && Url::parse(page)
            .ok()
            .is_some_and(|p| p.origin() == url.origin())
    {
        return BASIC.check_network_request(&req).should_block();
    }
    if mode.online() {
        if let Some(engine) = STANDARD.read().unwrap().as_ref() {
            return engine.engine.check_network_request(&req).should_block();
        }
    }
    BASIC.check_network_request(&req).should_block()
}

fn cached(path: &Path) -> Option<String> {
    if path.metadata().ok()?.len() > MAX_LIST as u64 {
        return None;
    }
    std::fs::read_to_string(path).ok().filter(|s| valid_list(s))
}

async fn updated(url: &str) -> Option<String> {
    let mut response = crate::streams::get_public(Url::parse(url).ok()?, Duration::from_secs(20))
        .await
        .ok()?;
    let mut bytes = Vec::new();
    while let Some(chunk) = response.chunk().await.ok()? {
        if bytes.len() + chunk.len() > MAX_LIST {
            return None;
        }
        bytes.extend_from_slice(&chunk);
    }
    String::from_utf8(bytes).ok().filter(|s| valid_list(s))
}

fn online() -> bool {
    MODE.lock().unwrap().online()
}

fn player_recently_used() -> bool {
    LAST_PLAYER_USE.lock().unwrap().is_some()
}
fn profile() -> FilterProfile {
    let low = device::current().constrained;
    FilterProfile {
        complete: *MODE.lock().unwrap() == Mode::Complete && !low,
        constrained: low,
    }
}

async fn install(lists: Vec<String>, wanted: FilterProfile, directory: PathBuf) {
    if let Ok(compiled) = tauri::async_runtime::spawn_blocking(move || {
        let mut cosmetics = cosmetic::Rules::default();
        for line in lists.iter().flat_map(|s| s.lines()) {
            cosmetics.add(line, wanted.constrained);
        }
        #[cfg(target_os = "linux")]
        let webkit = linux::network_rules(&lists);
        Compiled {
            cosmetics,
            engine: compile_cached(
                lists,
                &directory,
                cfg!(target_os = "android") && wanted.constrained,
            ),
            profile: wanted,
            #[cfg(target_os = "linux")]
            webkit,
        }
    })
    .await
    {
        let last = LAST_PLAYER_USE.lock().unwrap();
        if last.is_some() && online() && profile() == wanted {
            *STANDARD.write().unwrap() = Some(compiled);
        }
    }
}

fn subscriptions(wanted: FilterProfile) -> Vec<(&'static str, &'static str)> {
    LISTS
        .iter()
        .chain(OTHER_LISTS)
        .filter(|(key, _)| {
            (!wanted.constrained || !matches!(*key, "easyprivacy" | "privacy"))
                && (*key != "annoyances" || wanted.complete)
        })
        .copied()
        .collect()
}

static PREPARE: tokio::sync::Mutex<()> = tokio::sync::Mutex::const_new(());

async fn prepare(directory: &Path, wanted: FilterProfile) {
    let _guard = PREPARE.lock().await;
    if STANDARD
        .read()
        .unwrap()
        .as_ref()
        .is_some_and(|c| c.profile == wanted)
    {
        return;
    }
    let directory = directory.to_owned();
    let folder = directory.clone();
    let lists = tauri::async_runtime::spawn_blocking(move || {
        subscriptions(wanted)
            .iter()
            .filter_map(|(key, _)| cached(&directory.join(key)))
            .collect()
    })
    .await;
    if let Ok(lists) = lists {
        install(lists, wanted, folder).await;
    }
}

async fn refresh(directory: PathBuf, force: bool) -> (usize, usize) {
    let _guard = REFRESH.lock().await;
    if !online() || std::fs::create_dir_all(&directory).is_err() {
        return (0, 0);
    }
    let wanted = profile();
    let subscriptions = subscriptions(wanted);
    if player_recently_used() {
        prepare(&directory, wanted).await;
    }
    let total = subscriptions.len();
    let mut count = 0;
    for (key, url) in &subscriptions {
        if !online() || profile() != wanted || (!force && device::current().defer_downloads) {
            break;
        }
        let path = directory.join(key);
        let fresh = path
            .metadata()
            .and_then(|m| m.modified())
            .ok()
            .and_then(|t| t.elapsed().ok())
            .is_some_and(|age| age < WEEK);
        if !force
            && fresh
            && path
                .metadata()
                .is_ok_and(|m| (1..=MAX_LIST as u64).contains(&m.len()))
        {
            continue;
        }
        if let Some(text) = updated(url).await {
            use std::io::Write;
            if let Ok(mut file) = tempfile::NamedTempFile::new_in(&directory) {
                if file.write_all(text.as_bytes()).is_ok() && file.persist(&path).is_ok() {
                    count += 1;
                }
            }
        }
    }
    if count > 0 && player_recently_used() {
        let _guard = PREPARE.lock().await;
        let folder = directory.clone();
        let lists = tauri::async_runtime::spawn_blocking(move || {
            subscriptions
                .iter()
                .filter_map(|(key, _)| cached(&directory.join(key)))
                .collect()
        })
        .await;
        if let Ok(lists) = lists {
            install(lists, wanted, folder).await;
        }
    }
    (count, total)
}

pub struct Directory(PathBuf);

pub(crate) async fn prepare_player(directory: &Directory) {
    if online() {
        player_in_use();
        prepare(&directory.0, profile()).await;
    }
}
pub fn setup(
    app: &tauri::AppHandle,
    root: &Path,
    store: &CatalogStore,
) -> Result<(), Box<dyn std::error::Error>> {
    let saved = store.setting("player_ad_filter")?;
    let mode = match saved.as_deref() {
        Some("off") => Mode::Off,
        Some("complete") => Mode::Complete,
        Some("standard") => Mode::Standard,
        _ => Mode::Basic,
    };
    *MODE.lock().unwrap() = mode;
    let dir = root.join("ad-filter");
    app.manage(Directory(dir));
    #[cfg(windows)]
    if let Some(main) = app.get_webview_window("main") {
        windows::attach_main(&main)?;
    }
    Ok(())
}

#[tauri::command]
pub async fn filter_status(
    directory: State<'_, Directory>,
    force: bool,
) -> CommandResult<serde_json::Value> {
    let folder = directory.0.clone();
    let (updated, requested) = if force {
        if !online() {
            return Err("Elegí el modo estándar o completo para descargar las listas.".into());
        }
        refresh(folder.clone(), true).await
    } else {
        (0, 0)
    };
    let sources=subscriptions(profile()).into_iter().map(|(key,_)|{
        let modified=std::fs::metadata(folder.join(key)).ok().and_then(|m|m.modified().ok());
        serde_json::json!({"name":key,"updatedAt":modified.map(chrono::DateTime::<chrono::Utc>::from)})
    }).collect::<Vec<_>>();
    Ok(serde_json::json!({"updated":updated,"requested":requested,"sources":sources}))
}

#[tauri::command]
pub async fn player_ad_filter(
    mode: Option<Mode>,
    store: State<'_, CatalogStore>,
) -> CommandResult<Mode> {
    let store = store.inner().clone();
    crate::work(move || set_player_ad_filter(mode, &store)).await
}

fn set_player_ad_filter(mode: Option<Mode>, store: &CatalogStore) -> CommandResult<Mode> {
    if let Some(mode) = mode {
        static SAVE_MODE: std::sync::Mutex<()> = std::sync::Mutex::new(());
        let _save = SAVE_MODE.lock().map_err(|_| "Configuración ocupada.")?;
        let value = match mode {
            Mode::Basic => "basic",
            Mode::Off => "off",
            Mode::Standard => "standard",
            Mode::Complete => "complete",
        };
        store
            .set_setting("player_ad_filter", value)
            .map_err(|e| e.to_string())?;
        *MODE.lock().unwrap() = mode;
        if !mode.online() {
            *STANDARD.write().unwrap() = None;
        }
    }
    Ok(*MODE.lock().unwrap())
}

#[tauri::command]
pub async fn open_filtered_player(
    app: tauri::AppHandle,
    address: String,
    language: Option<String>,
    interactive: Option<bool>,
    bounds: Option<playback::Bounds>,
) -> CommandResult<uuid::Uuid> {
    let bounds = if interactive == Some(true) {
        None
    } else {
        Some(
            bounds
                .ok_or("Falta el espacio del reproductor.")?
                .validate()?,
        )
    };
    let url = Url::parse(&address)
        .ok()
        .filter(|u| u.scheme() == "https" && umd_core::public_url::web_url(u))
        .ok_or("Ingresá una dirección pública HTTPS.")?;
    let url = if bounds.is_some() {
        crate::streams::provider_web_address(url).await?
    } else {
        url
    };
    if online() {
        let dir = app.state::<Directory>().0.clone();
        prepare_player(&app.state::<Directory>()).await;
        tauri::async_runtime::spawn(refresh(dir, false));
    }
    if blocked(url.as_str(), url.as_str(), "document", "GET") {
        return Err("El bloqueador identificó esta dirección como publicidad.".into());
    }
    #[cfg(not(target_os = "android"))]
    let _ = (language, interactive);
    let session = uuid::Uuid::new_v4();
    #[cfg(target_os = "linux")]
    return linux::open(app, url, session, bounds)
        .await
        .map(|_| session);
    #[cfg(windows)]
    return windows::open(app, url, session, bounds)
        .await
        .map(|_| session);
    #[cfg(target_os = "android")]
    return android::open(
        app,
        url,
        session,
        language.as_deref() == Some("en"),
        interactive == Some(true),
        bounds,
    )
    .await
    .map(|_| session);
    #[cfg(not(any(windows, target_os = "android", target_os = "linux")))]
    {
        let _ = (app, session, bounds);
        Err("Esta plataforma no tiene un reproductor web protegido.".into())
    }
}

/// Only used for public catalog GETs after the user opens that provider.
pub(crate) async fn verified_read(
    app: &tauri::AppHandle,
    id: uuid::Uuid,
    origin: &Url,
    url: &Url,
    limit: usize,
) -> CommandResult<Vec<u8>> {
    let bytes = verified_bytes(app, id, origin, url, limit, None).await?;
    let text = bytes.strip_prefix(&[0xef, 0xbb, 0xbf]).unwrap_or(&bytes);
    Ok(String::from_utf8_lossy(text).into_owned().into_bytes())
}

pub(crate) async fn verified_image(
    app: &tauri::AppHandle,
    id: uuid::Uuid,
    origin: &Url,
    url: &Url,
) -> CommandResult<Vec<u8>> {
    verified_bytes(
        app,
        id,
        origin,
        url,
        4 << 20,
        Some("image/avif,image/webp,image/png,image/jpeg,image/*"),
    )
    .await
}

async fn verified_bytes(
    app: &tauri::AppHandle,
    id: uuid::Uuid,
    origin: &Url,
    url: &Url,
    limit: usize,
    accept: Option<&str>,
) -> CommandResult<Vec<u8>> {
    if url.scheme() != "https"
        || origin.origin() != url.origin()
        || !umd_core::public_url::web_url(url)
    {
        return Err("Origen no permitido.".into());
    }
    let key = serde_json::json!(format!("umdCatalog{}", uuid::Uuid::new_v4().simple()));
    let url = serde_json::json!(url.as_str());
    let origin = serde_json::json!(origin.origin().ascii_serialization());
    let headers = accept.map_or_else(
        || serde_json::json!({}),
        |accept| serde_json::json!({"Accept":accept}),
    );
    let limit = limit.min(4 * 1024 * 1024);
    let start = format!(
        r#"(()=>{{if(location.origin!=={origin})return false;window[{key}]=null;
    (async()=>{{try{{const r=await fetch({url},{{mode:"same-origin",redirect:"error",headers:{headers},signal:AbortSignal.timeout(15000)}});if(!r.ok||!r.body)throw Error();
    const reader=r.body.getReader(),chunks=[];let len=0;while(true){{const p=await reader.read();if(p.done)break;
    len+=p.value.length;if(len>{limit}){{await reader.cancel();throw Error()}}chunks.push(p.value)}}
    const body=new Uint8Array(len);let offset=0;for(const chunk of chunks){{body.set(chunk,offset);offset+=chunk.length}}
    window[{key}]=body}}catch{{window[{key}]=false}}}})();return true}})()"#
    );
    if eval_public(app, id, start).await? != "true" {
        return Err("Abrí el sitio del proveedor.".into());
    }
    let read = async {
        loop {
            let size=eval_public(app,id,format!("window[{key}] instanceof Uint8Array?window[{key}].length:window[{key}]===null?-1:-2")).await?.parse::<i64>().unwrap_or(-2);
            if size == -1 {
                tokio::time::sleep(std::time::Duration::from_millis(150)).await;
                continue;
            }
            if size < 0 || size > limit as i64 {
                return Err("El proveedor denegó el acceso.".into());
            }
            let mut text = Vec::new();
            for offset in (0..size).step_by(32768) {
                let chunk=eval_public(app,id,format!("btoa(String.fromCharCode(...window[{key}].subarray({offset},{offset}+32768)))")).await?;
                use base64::Engine as _;
                let piece = serde_json::from_str::<String>(&chunk).map_err(|e| e.to_string())?;
                text.extend(
                    base64::engine::general_purpose::STANDARD
                        .decode(piece)
                        .map_err(|e| e.to_string())?,
                );
            }
            return Ok(text);
        }
    };
    let result = tokio::time::timeout(std::time::Duration::from_secs(20), read).await;
    let _ = eval_public(app, id, format!("delete window[{key}]")).await;
    result.map_err(|_| "Se agotó el tiempo del proveedor.")?
}

async fn eval_public(
    app: &tauri::AppHandle,
    id: uuid::Uuid,
    script: String,
) -> CommandResult<String> {
    #[cfg(windows)]
    {
        windows::command(app, id, script, false).await
    }
    #[cfg(target_os = "android")]
    {
        android::command(app, id, script, false).await
    }
    #[cfg(target_os = "linux")]
    {
        linux::command(app, id, script, false).await
    }
    #[cfg(not(any(windows, target_os = "android", target_os = "linux")))]
    {
        let _ = (app, id, script);
        Err("Plataforma no compatible.".into())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn blocks_ad_hosts_and_requests_but_preserves_video_and_exceptions() {
        let engine = compile(
            vec!["||commercial.example^\n@@||commercial.example/subs/".into()],
            false,
        );
        let page = "https://watch.example/show";
        let deny = |url, kind| {
            engine
                .check_network_request(&Request::new(url, page, kind, "GET").unwrap())
                .should_block()
        };
        assert!(deny("https://sub.doubleclick.net/ad.mp4", "media"));
        assert!(deny("https://watch.example/pagead/start.js", "script"));
        assert!(deny("https://commercial.example/pre.js", "script"));
        assert!(!deny("https://commercial.example/subs/es.vtt", "media"));
        assert!(!deny(
            "https://notdoubleclick.net/stream.m3u8",
            "xmlhttprequest"
        ));
        assert!(!deny("https://downloads.example/segment_1.ts", "media"));
        assert!(!blocked(
            "http://asset.localhost/pagead/test",
            "https://tauri.localhost",
            "image",
            "GET"
        ));
        assert!(!valid_list("<html>502 error</html>"));
    }
    #[test]
    fn a_cached_engine_is_reused_for_the_same_lists_and_rebuilt_for_others() {
        let dir = tempfile::tempdir().unwrap();
        let cache = dir.path().join("engine.cache");
        let first = "||blocked-one.example^\n".to_owned();
        let second = "||blocked-two.example^\n".to_owned();
        let deny = |engine: &Engine, url| {
            engine
                .check_network_request(
                    &Request::new(url, "https://watch.example/show", "script", "GET").unwrap(),
                )
                .should_block()
        };
        let fresh = compile_cached(vec![first.clone()], dir.path(), false);
        assert!(deny(&fresh, "https://blocked-one.example/a.js"));
        assert!(!deny(&fresh, "https://blocked-two.example/a.js"));

        // Put another engine under the first lists' key: getting its verdicts
        // back shows the next call loaded the file instead of compiling.
        let key = std::fs::read(&cache).unwrap()[..8].to_vec();
        let mut swapped = key.clone();
        swapped.extend(compile(vec![second.clone()], false).serialize());
        std::fs::write(&cache, swapped).unwrap();
        let reused = compile_cached(vec![first.clone()], dir.path(), false);
        assert!(deny(&reused, "https://blocked-two.example/a.js"));
        assert!(!deny(&reused, "https://blocked-one.example/a.js"));

        // Different lists have another key and replace the file.
        let other = compile_cached(vec![second], dir.path(), false);
        assert!(deny(&other, "https://blocked-two.example/a.js"));
        assert_ne!(std::fs::read(&cache).unwrap()[..8], key[..]);

        // A damaged file is ignored and rebuilt.
        std::fs::write(&cache, [key.as_slice(), &[7; 64]].concat()).unwrap();
        let rebuilt = compile_cached(vec![first], dir.path(), false);
        assert!(deny(&rebuilt, "https://blocked-one.example/a.js"));
        assert!(!deny(&rebuilt, "https://blocked-two.example/a.js"));
    }
}
