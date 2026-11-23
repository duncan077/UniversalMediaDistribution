use super::CommandResult;
use serde_json::Value;
use windows_sys::Win32::Foundation::{HWND, RECT};
use windows_sys::Win32::UI::WindowsAndMessaging::*;

pub(super) struct Viewport {
    parent: usize,
    child: usize,
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
    pub(super) fn layout(&self, area: &Value) {
        unsafe {
            let mut client = RECT::default();
            if GetClientRect(self.parent as HWND, &mut client) == 0 {
                return;
            }
            let scale = f64::from(client.right - client.left)
                / area["pageWidth"].as_f64().unwrap_or(1.).max(1.);
            let pixels = |key: &str| (area[key].as_f64().unwrap_or(0.) * scale).round() as i32;
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
