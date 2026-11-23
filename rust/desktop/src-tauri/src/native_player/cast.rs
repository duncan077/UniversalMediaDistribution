//! LibVLC 3 renderer discovery. The callback data lives until both events are detached.
use super::desktop::{symbol, Handle};
use crate::CommandResult;
use libloading::Library;
use serde_json::{json, Value};
use std::{
    ffi::{c_char, c_int, CStr},
    sync::Mutex,
};

#[repr(C)]
struct Event {
    kind: c_int,
    object: Handle,
    item: Handle,
}
#[repr(C)]
struct Service {
    name: *const c_char,
    long_name: *const c_char,
}
const ADDED: c_int = 0x502;
const DELETED: c_int = 0x503;
type Callback = unsafe extern "C" fn(*const Event, Handle);
type Release = unsafe extern "C" fn(Handle);
type Detach = unsafe extern "C" fn(Handle, c_int, Callback, Handle);
type RendererEntries = (u32, Vec<(u32, usize, String)>);
struct Items {
    entries: Mutex<RendererEntries>,
    hold: unsafe extern "C" fn(Handle) -> Handle,
    name: unsafe extern "C" fn(Handle) -> *const c_char,
    free: Release,
}
unsafe extern "C" fn changed(event: *const Event, data: Handle) {
    let items = &*(data as *const Items);
    let mut rows = items.entries.lock().unwrap_or_else(|e| e.into_inner());
    let item = (*event).item;
    if item.is_null() {
        return;
    }
    let key = item as usize;
    if let Some(i) = rows.1.iter().position(|row| row.1 == key) {
        if (*event).kind == DELETED {
            rows.1.remove(i);
            (items.free)(item);
        }
    } else if (*event).kind == ADDED && rows.1.len() < 256 {
        let p = (items.name)(item);
        if p.is_null() {
            return;
        }
        let name = CStr::from_ptr(p).to_string_lossy().into_owned();
        let id = rows.0;
        rows.0 = rows.0.saturating_add(1);
        (items.hold)(item);
        rows.1.push((id, key, name));
    }
}

pub(super) struct Discovery {
    service: Handle,
    manager: Handle,
    items: Box<Items>,
    stop: Release,
    release: Release,
    detach: Detach,
    attached: usize,
    started: bool,
}
impl Discovery {
    pub(super) unsafe fn open(lib: &Library, core: Handle) -> CommandResult<Self> {
        let list: unsafe extern "C" fn(Handle, *mut *mut *mut Service) -> usize =
            symbol(lib, b"libvlc_renderer_discoverer_list_get")?;
        let free: unsafe extern "C" fn(*mut *mut Service, usize) =
            symbol(lib, b"libvlc_renderer_discoverer_list_release")?;
        let create: unsafe extern "C" fn(Handle, *const c_char) -> Handle =
            symbol(lib, b"libvlc_renderer_discoverer_new")?;
        let manager: unsafe extern "C" fn(Handle) -> Handle =
            symbol(lib, b"libvlc_renderer_discoverer_event_manager")?;
        let start: unsafe extern "C" fn(Handle) -> c_int =
            symbol(lib, b"libvlc_renderer_discoverer_start")?;
        let attach: unsafe extern "C" fn(Handle, c_int, Callback, Handle) -> c_int =
            symbol(lib, b"libvlc_event_attach")?;
        let detach = symbol(lib, b"libvlc_event_detach")?;
        let release = symbol(lib, b"libvlc_renderer_discoverer_release")?;
        let stop = symbol(lib, b"libvlc_renderer_discoverer_stop")?;
        let items = Box::new(Items {
            entries: Mutex::new((1, Vec::new())),
            hold: symbol(lib, b"libvlc_renderer_item_hold")?,
            free: symbol(lib, b"libvlc_renderer_item_release")?,
            name: symbol(lib, b"libvlc_renderer_item_name")?,
        });
        let mut services = std::ptr::null_mut();
        let count = list(core, &mut services);
        if count == 0 || services.is_null() {
            return Err("VLC no tiene un servicio de transmisión disponible.".into());
        }
        let mut service = std::ptr::null_mut();
        for &s in std::slice::from_raw_parts(services, count) {
            if !s.is_null() {
                service = create(core, (*s).name);
                if !service.is_null() {
                    break;
                }
            }
        }
        free(services, count);
        if service.is_null() {
            return Err("No se pudo iniciar la búsqueda de pantallas.".into());
        }
        let mut discovery = Self {
            service,
            manager: manager(service),
            items,
            stop,
            release,
            detach,
            attached: 0,
            started: false,
        };
        for kind in [ADDED, DELETED] {
            if attach(discovery.manager, kind, changed, discovery.data()) != 0 {
                return Err("VLC no pudo recibir eventos de pantallas.".into());
            }
            discovery.attached += 1;
        }
        if start(service) != 0 {
            return Err("No se pudo buscar Chromecast en la red.".into());
        }
        discovery.started = true;
        Ok(discovery)
    }
    fn data(&self) -> Handle {
        (&*self.items as *const Items).cast_mut().cast()
    }
    pub(super) fn list(&self) -> Vec<Value> {
        self.items
            .entries
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .1
            .iter()
            .map(|(id, _, name)| json!({"id":id,"name":name}))
            .collect()
    }
    pub(super) unsafe fn select(
        &self,
        lib: &Library,
        player: Handle,
        id: u32,
    ) -> CommandResult<()> {
        let set: unsafe extern "C" fn(Handle, Handle) -> c_int =
            symbol(lib, b"libvlc_media_player_set_renderer")?;
        let held = if id == 0 {
            std::ptr::null_mut()
        } else {
            let rows = self.items.entries.lock().unwrap_or_else(|e| e.into_inner());
            let row = rows
                .1
                .iter()
                .find(|row| row.0 == id)
                .ok_or("La pantalla ya no está disponible.")?;
            (self.items.hold)(row.1 as Handle)
        };
        let rc = set(player, held);
        if !held.is_null() {
            (self.items.free)(held);
        }
        if rc != 0 {
            return Err("VLC no pudo cambiar de pantalla.".into());
        }
        Ok(())
    }
}
impl Drop for Discovery {
    fn drop(&mut self) {
        unsafe {
            if self.started {
                (self.stop)(self.service);
            }
            for &kind in [ADDED, DELETED].iter().take(self.attached) {
                (self.detach)(self.manager, kind, changed, self.data());
            }
            for (_, item, _) in self
                .items
                .entries
                .lock()
                .unwrap_or_else(|e| e.into_inner())
                .1
                .drain(..)
            {
                (self.items.free)(item as Handle);
            }
            (self.release)(self.service);
        }
    }
}
