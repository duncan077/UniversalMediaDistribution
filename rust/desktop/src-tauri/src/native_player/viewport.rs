use super::CommandResult;
use serde_json::Value;
use windows_sys::Win32::Foundation::{HWND, POINT, RECT};
use windows_sys::Win32::Graphics::Gdi::{CombineRgn, CreateEllipticRgn, CreateRectRgn, DeleteObject, SetWindowRgn, RGN_DIFF};
use windows_sys::Win32::UI::WindowsAndMessaging::*;

pub(super) struct Viewport {
    parent: usize,
    child: usize,
    pointer: Option<(i32, i32)>,
}
impl Viewport {
    pub(super) async fn create(window: &tauri::WebviewWindow, area: Value) -> CommandResult<Self> {
        let parent = window.hwnd().map_err(|e| e.to_string())?.0 as usize;
        let (tx, rx) = tokio::sync::oneshot::channel();
        window
            .run_on_main_thread(move || unsafe {
                let child = CreateWindowExW(
                    WS_EX_NOACTIVATE,
                    windows_sys::w!("STATIC"),
                    windows_sys::w!("UMD video"),
                    WS_CHILD | WS_CLIPCHILDREN | WS_CLIPSIBLINGS,
                    0,
                    0,
                    0,
                    0,
                    parent as HWND,
                    std::ptr::null_mut(),
                    std::ptr::null_mut(),
                    std::ptr::null(),
                );
                let result = if child.is_null() {
                    Err(std::io::Error::last_os_error().to_string())
                } else {
                    let host = Self {
                        parent,
                        child: child as usize,
                        pointer: None,
                    };
                    host.layout(&area);
                    Ok(host)
                };
                let _ = tx.send(result);
            })
            .map_err(|e| e.to_string())?;
        rx.await.map_err(|e| e.to_string())?
    }
    pub(super) fn handle(&self) -> HWND {
        self.child as HWND
    }
    pub(super) fn pointer_moved(&mut self) -> bool {
        unsafe {
            let mut point = POINT::default();
            let mut bounds = RECT::default();
            let inside = GetForegroundWindow() == self.parent as HWND
                && IsWindowVisible(self.handle()) != 0 && GetCursorPos(&mut point) != 0
                && GetWindowRect(self.handle(), &mut bounds) != 0
                && (bounds.left..bounds.right).contains(&point.x)
                && (bounds.top..bounds.bottom).contains(&point.y);
            let current = inside.then_some((point.x, point.y));
            let moved = current.is_some() && self.pointer != current;
            self.pointer = current;
            moved
        }
    }
    pub(super) fn layout(&self, area: &Value) {
        unsafe {
            let mut client = RECT::default();
            if GetClientRect(self.parent as HWND, &mut client) == 0 {
                return;
            }
            let scale = f64::from(client.right - client.left)
                / area["pageWidth"].as_f64().unwrap_or(1.).max(1.);
            let pixels = |key: &str| (area[key].as_f64().unwrap_or(0.) * scale).round() as i32;
            // The system owns the region only after SetWindowRgn succeeds.
            let region = CreateRectRgn(0, 0, pixels("width").max(1), pixels("height").max(1));
            if !region.is_null() {
                for cut in area["cutouts"].as_array().into_iter().flatten().take(8) {
                    let coord = |key: &str| (cut[key].as_f64().unwrap_or_default() * scale).round() as i32;
                    let create = if cut["round"] == true { CreateEllipticRgn } else { CreateRectRgn };
                    let hole = create(coord("left"), coord("top"), coord("right"), coord("bottom"));
                    if !hole.is_null() {
                        CombineRgn(region, region, hole, RGN_DIFF);
                        DeleteObject(hole);
                    }
                }
                if SetWindowRgn(self.handle(), region, 1) == 0 {
                    DeleteObject(region);
                }
            }
            let show = if area["visible"].as_bool() == Some(true) {
                SWP_SHOWWINDOW
            } else {
                SWP_HIDEWINDOW
            };
            SetWindowPos(
                self.handle(),
                HWND_TOP,
                pixels("x"),
                pixels("y"),
                pixels("width").max(1),
                pixels("height").max(1),
                SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS | show,
            );
        }
    }
}
impl Drop for Viewport {
    fn drop(&mut self) {
        unsafe {
            PostMessageW(self.handle(), WM_CLOSE, 0, 0);
        }
    }
}
