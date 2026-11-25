use crate::CommandResult;
use libloading::Library;
use serde_json::{json, Value};
use std::{
    ffi::{c_char, c_int, c_void, CStr, CString},
    sync::Mutex,
};
pub(super) type Handle = *mut c_void;

#[repr(C)]
struct Track {
    id: c_int,
    name: *const c_char,
    next: *const Track,
}
struct Player {
    #[cfg(windows)]
    viewport: super::viewport::Viewport,
    discovery: Option<super::cast::Discovery>,
    selected: u32,
    lib: Library,
    core: Handle,
    player: Handle,
    id: String,
    stop: unsafe extern "C" fn(Handle),
    release: unsafe extern "C" fn(Handle),
    close: unsafe extern "C" fn(Handle),
}
// Commands borrow the active player under the mutex. Removed players own their
// handles and can stop outside the lock, including while a new source starts.
unsafe impl Send for Player {}
static PLAYER: Mutex<Option<Player>> = Mutex::new(None);
impl Drop for Player {
    fn drop(&mut self) {
        unsafe {
            (self.stop)(self.player);
            (self.release)(self.player);
            drop(self.discovery.take());
            (self.close)(self.core);
        }
    }
}

pub(super) unsafe fn symbol<T: Copy>(lib: &Library, name: &[u8]) -> CommandResult<T> {
    lib.get::<T>(name).map(|f| *f).map_err(|e| e.to_string())
}
fn library() -> CommandResult<Library> {
    #[cfg(windows)]
    {
        let mut paths = Vec::new();
        if let Ok(exe) = std::env::current_exe() {
            if let Some(dir) = exe.parent() {
                let arch = match std::env::consts::ARCH {
                    "x86_64" => "x64",
                    "aarch64" => "arm64",
                    _ => "x86",
                };
                paths.push(dir.join("vlc").join(arch).join("libvlc.dll"));
            }
        }
        if let Some(parent) = std::env::var_os("ProgramFiles") {
            paths.push(std::path::PathBuf::from(parent).join("VideoLAN/VLC/libvlc.dll"));
        }
        for path in paths.into_iter().filter(|p| p.is_file()) {
            if let Ok(lib) =
                unsafe { libloading::os::windows::Library::load_with_flags(path, 0x100 | 0x800) }
            {
                return Ok(lib.into());
            }
        }
    }
    #[cfg(target_os = "linux")]
    {
        if let Some(bundled) = std::env::current_exe()
            .ok()
            .and_then(|exe| exe.parent().map(|dir| dir.join("libvlc.so")))
        {
            if let Ok(lib) = unsafe { Library::new(bundled) } {
                return Ok(lib);
            }
        }
        if let Ok(lib) = unsafe { Library::new("libvlc.so.5") } {
            return Ok(lib);
        }
    }
    Err("No se encontró LibVLC 3 en este equipo.".into())
}
impl Player {
    unsafe fn start(
        id: &str,
        source: &str,
        tracks: &Value,
        #[cfg(windows)] viewport: super::viewport::Viewport,
    ) -> CommandResult<Self> {
        let lib = library()?;
        let new: unsafe extern "C" fn(c_int, *const *const c_char) -> Handle =
            symbol(&lib, b"libvlc_new")?;
        let close = symbol::<unsafe extern "C" fn(Handle)>(&lib, b"libvlc_release")?;
        let media_new: unsafe extern "C" fn(Handle, *const c_char) -> Handle =
            symbol(&lib, b"libvlc_media_new_location")?;
        let media_release: unsafe extern "C" fn(Handle) = symbol(&lib, b"libvlc_media_release")?;
        let from_media: unsafe extern "C" fn(Handle) -> Handle =
            symbol(&lib, b"libvlc_media_player_new_from_media")?;
        let release = symbol(&lib, b"libvlc_media_player_release")?;
        let stop = symbol(&lib, b"libvlc_media_player_stop")?;
        let play: unsafe extern "C" fn(Handle) -> c_int =
            symbol(&lib, b"libvlc_media_player_play")?;
        let src = CString::new(source).map_err(|e| e.to_string())?;
        let args = [
            c"--no-video-title-show".as_ptr(),
            c"--no-snapshot-preview".as_ptr(),
        ];
        let add_slave: unsafe extern "C" fn(Handle, c_int, u32, *const c_char) -> c_int =
            symbol(&lib, b"libvlc_media_slaves_add")?;
        let core = new(args.len() as c_int, args.as_ptr());
        if core.is_null() {
            return Err("VLC no pudo iniciarse.".into());
        }
        let media = media_new(core, src.as_ptr());
        if media.is_null() {
            close(core);
            return Err("VLC no pudo abrir la fuente.".into());
        }
        for t in tracks.as_array().into_iter().flatten() {
            if let Some(url) = t["url"].as_str().and_then(|s| CString::new(s).ok()) {
                add_slave(
                    media,
                    0,
                    if t["default"].as_bool() == Some(true) {
                        4
                    } else {
                        2
                    },
                    url.as_ptr(),
                );
            }
        }
        let player = from_media(media);
        media_release(media);
        if player.is_null() {
            close(core);
            return Err("VLC no pudo crear el reproductor.".into());
        }
        let owner = Self {
            #[cfg(windows)]
            viewport,
            discovery: None,
            selected: 0,
            lib,
            core,
            player,
            id: id.into(),
            stop,
            release,
            close,
        };
        #[cfg(windows)]
        {
            symbol::<unsafe extern "C" fn(Handle, Handle)>(
                &owner.lib,
                b"libvlc_media_player_set_hwnd",
            )?(player, owner.viewport.handle());
        }
        if play(player) != 0 {
            return Err("VLC no pudo reproducir la fuente.".into());
        }
        Ok(owner)
    }
    unsafe fn tracks(&self, name: &[u8]) -> CommandResult<Vec<Value>> {
        let get: unsafe extern "C" fn(Handle) -> *const Track = symbol(&self.lib, name)?;
        let free: unsafe extern "C" fn(*const Track) =
            symbol(&self.lib, b"libvlc_track_description_list_release")?;
        let head = get(self.player);
        let mut current = head;
        let mut rows = vec![];
        while !current.is_null() && rows.len() < 256 {
            let track = &*current;
            let name = if track.name.is_null() {
                String::new()
            } else {
                CStr::from_ptr(track.name).to_string_lossy().into_owned()
            };
            rows.push(json!({"id":track.id,"name":name}));
            current = track.next;
        }
        if !head.is_null() {
            free(head);
        }
        Ok(rows)
    }
    unsafe fn state(&mut self, op: &str, a: &Value) -> CommandResult<Value> {
        let number = a["value"].as_f64().unwrap_or_default();
        match op {
            #[cfg(windows)]
            "layout" => self.viewport.layout(&a["box"]),
            "play" => {
                symbol::<unsafe extern "C" fn(Handle) -> c_int>(
                    &self.lib,
                    b"libvlc_media_player_play",
                )?(self.player);
            }
            "pause" => {
                symbol::<unsafe extern "C" fn(Handle, c_int)>(
                    &self.lib,
                    b"libvlc_media_player_set_pause",
                )?(self.player, 1);
            }
            "discover" => {
                if self.discovery.is_none() {
                    self.discovery = Some(super::cast::Discovery::open(&self.lib, self.core)?);
                }
            }
            "renderer" => {
                let id = a["value"]
                    .as_u64()
                    .and_then(|n| u32::try_from(n).ok())
                    .ok_or("Pantalla inválida.")?;
                let discovery = self
                    .discovery
                    .as_ref()
                    .ok_or("Primero buscá una pantalla.")?;
                let play: unsafe extern "C" fn(Handle) -> c_int =
                    symbol(&self.lib, b"libvlc_media_player_play")?;
                (self.stop)(self.player);
                if let Err(e) = discovery.select(&self.lib, self.player, id) {
                    let _ = discovery.select(&self.lib, self.player, self.selected);
                    play(self.player);
                    return Err(e);
                }
                if play(self.player) != 0 {
                    let _ = discovery.select(&self.lib, self.player, 0);
                    self.selected = 0;
                    play(self.player);
                    return Err("No se pudo reproducir en esa pantalla.".into());
                }
                self.selected = id;
            }
            "volume" => {
                let set = symbol::<unsafe extern "C" fn(Handle, c_int) -> c_int>(
                    &self.lib,
                    b"libvlc_audio_set_volume",
                )?;
                if set(self.player, number.clamp(0., 100.) as c_int) < 0 {
                    return Err("No se pudo cambiar el volumen.".into());
                }
            }
            "seek" => {
                symbol::<unsafe extern "C" fn(Handle, i64)>(
                    &self.lib,
                    b"libvlc_media_player_set_time",
                )?(self.player, (number.max(0.) * 1000.) as i64);
            }
            "audio" | "subtitle" => {
                let name: &[u8] = if op == "audio" {
                    b"libvlc_audio_set_track"
                } else {
                    b"libvlc_video_set_spu"
                };
                symbol::<unsafe extern "C" fn(Handle, c_int) -> c_int>(&self.lib, name)?(
                    self.player,
                    number as i32,
                );
            }
            _ => {}
        }
        let read = |name: &[u8]| -> CommandResult<c_int> {
            Ok(symbol::<unsafe extern "C" fn(Handle) -> c_int>(
                &self.lib, name,
            )?(self.player))
        };
        let seconds = |name: &[u8]| -> CommandResult<f64> {
            Ok(
                symbol::<unsafe extern "C" fn(Handle) -> i64>(&self.lib, name)?(self.player).max(0)
                    as f64
                    / 1000.,
            )
        };
        let state = read(b"libvlc_media_player_get_state")?;
        if state == 7 {
            return Ok(json!({"error":"VLC"}));
        }
        Ok(
            json!({"renderers":self.discovery.as_ref().map(|d|d.list()),"selectedRenderer":self.selected,"position":seconds(b"libvlc_media_player_get_time")?,"duration":seconds(b"libvlc_media_player_get_length")?,
          "ready":matches!(state,3|4|6),"ended":state==6,"paused":state!=3,
          "audio":self.tracks(b"libvlc_audio_get_track_description")?,"subtitle":self.tracks(b"libvlc_video_get_spu_description")?,
          "selectedAudio":read(b"libvlc_audio_get_track")?,"selectedSubtitle":read(b"libvlc_video_get_spu")?}),
        )
    }
}
pub(super) fn command(
    op: &str,
    args: &Value,
    #[cfg(windows)] viewport: Option<super::viewport::Viewport>,
) -> CommandResult<Value> {
    let id = args["id"].as_str().unwrap_or_default();
    if op == "start" {
        let mut next = unsafe {
            Player::start(
                id,
                args["source"].as_str().ok_or("Falta la fuente.")?,
                &args["tracks"],
                #[cfg(windows)]
                viewport.ok_or("Falta la superficie de video.")?,
            )?
        };
        let initial = unsafe { next.state(op, args) };
        let previous = PLAYER.lock().map_err(|e| e.to_string())?.replace(next);
        drop(previous);
        return initial;
    }
    let mut slot = PLAYER.lock().map_err(|e| e.to_string())?;
    let Some(p) = slot.as_mut().filter(|p| p.id == id) else {
        return Ok(json!({"closed":true}));
    };
    if op == "close" {
        let previous = slot.take();
        drop(slot);
        drop(previous);
        return Ok(json!({"closed":true}));
    }
    unsafe { p.state(op, args) }
}
