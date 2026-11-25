use super::playback::Bounds;
use super::{blocked, CommandResult};
use reqwest::Url;
use std::{cell::RefCell, collections::HashMap};
use tauri::{
    webview::NewWindowResponse, AppHandle, Manager, WebviewUrl, WebviewWindow, WebviewWindowBuilder,
};
use uuid::Uuid;
use webview2_com::{
    take_pwstr, Microsoft::Web::WebView2::Win32::*, PermissionRequestedEventHandler,
    WebResourceRequestedEventHandler,
};
use windows_core::{w, Interface, PWSTR};
use wry::{
    dpi::{LogicalPosition, LogicalSize},
    WebView, WebViewBuilder, WebViewExtWindows,
};

thread_local! {
    static PLAYERS: RefCell<HashMap<Uuid, WebView>> = RefCell::new(HashMap::new());
}

fn resource_type(context: COREWEBVIEW2_WEB_RESOURCE_CONTEXT) -> &'static str {
    match context {
        COREWEBVIEW2_WEB_RESOURCE_CONTEXT_SCRIPT => "script",
        COREWEBVIEW2_WEB_RESOURCE_CONTEXT_STYLESHEET => "stylesheet",
        COREWEBVIEW2_WEB_RESOURCE_CONTEXT_IMAGE => "image",
        COREWEBVIEW2_WEB_RESOURCE_CONTEXT_FONT => "font",
        COREWEBVIEW2_WEB_RESOURCE_CONTEXT_MEDIA | COREWEBVIEW2_WEB_RESOURCE_CONTEXT_TEXT_TRACK => {
            "media"
        }
        COREWEBVIEW2_WEB_RESOURCE_CONTEXT_DOCUMENT => "subdocument",
        COREWEBVIEW2_WEB_RESOURCE_CONTEXT_FETCH
        | COREWEBVIEW2_WEB_RESOURCE_CONTEXT_XML_HTTP_REQUEST => "xmlhttprequest",
        COREWEBVIEW2_WEB_RESOURCE_CONTEXT_PING => "ping",
        COREWEBVIEW2_WEB_RESOURCE_CONTEXT_WEBSOCKET => "websocket",
        _ => "other",
    }
}

fn attach(
    core: ICoreWebView2,
    environment: ICoreWebView2Environment,
    provider: Option<Url>,
) -> windows_core::Result<()> {
    // The controller and its event handlers are used on WebView2's UI thread.
    unsafe {
        unsafe extern "C" {
            fn umd_audio_attach(pid: u32);
        }
        let mut pid = 0;
        if core.BrowserProcessId(&mut pid).is_ok() {
            umd_audio_attach(pid);
        }
        if provider.is_some() {
            core.cast::<ICoreWebView2_18>()?
                .add_LaunchingExternalUriScheme(
                    &webview2_com::LaunchingExternalUriSchemeEventHandler::create(Box::new(
                        |_, args| {
                            if let Some(args) = args {
                                args.SetCancel(true)?;
                            }
                            Ok(())
                        },
                    )),
                    &mut 0,
                )?;
            let settings = core.Settings()?;
            settings.SetIsWebMessageEnabled(false)?;
            settings.SetAreHostObjectsAllowed(false)?;
            settings.SetAreDefaultContextMenusEnabled(false)?;
            core.add_PermissionRequested(
                &PermissionRequestedEventHandler::create(Box::new(|_, args| {
                    if let Some(args) = args {
                        args.SetState(COREWEBVIEW2_PERMISSION_STATE_DENY)?;
                    }
                    Ok(())
                })),
                &mut 0,
            )?;
        }
        if let Ok(all_frames) = core.cast::<ICoreWebView2_22>() {
            all_frames.AddWebResourceRequestedFilterWithRequestSourceKinds(
                w!("*"),
                COREWEBVIEW2_WEB_RESOURCE_CONTEXT_ALL,
                COREWEBVIEW2_WEB_RESOURCE_REQUEST_SOURCE_KINDS_ALL,
            )?;
        } else {
            core.AddWebResourceRequestedFilter(w!("*"), COREWEBVIEW2_WEB_RESOURCE_CONTEXT_ALL)?;
        }
        let refuse = environment.CreateWebResourceResponse(
            None,
            403,
            w!("Blocked"),
            w!("Cache-Control: no-store"),
        )?;
        core.add_WebResourceRequested(
            &WebResourceRequestedEventHandler::create(Box::new(move |_, args| {
                let Some(args) = args else {
                    return Ok(());
                };
                let req = args.Request()?;
                let mut ptr = PWSTR::null();
                req.Uri(&mut ptr)?;
                let address = take_pwstr(ptr);
                let mut method = PWSTR::null();
                req.Method(&mut method)?;
                let method = take_pwstr(method);
                let mut context = COREWEBVIEW2_WEB_RESOURCE_CONTEXT_OTHER;
                args.ResourceContext(&mut context)?;
                let mut referer = PWSTR::null();
                if let Ok(headers) = req.Headers() {
                    let _ = headers.GetHeader(w!("Referer"), &mut referer);
                }
                let referer = take_pwstr(referer);
                let page = if referer.is_empty() {
                    provider
                        .as_ref()
                        .map_or("https://tauri.localhost", Url::as_str)
                } else {
                    &referer
                };
                let private_resource = provider.is_some() && !super::provider_resource(&address);
                if private_resource || blocked(&address, page, resource_type(context), &method) {
                    args.SetResponse(&refuse)?;
                }
                Ok(())
            })),
            &mut 0,
        )?;
    }
    Ok(())
}

pub fn attach_main(main: &WebviewWindow) -> Result<(), Box<dyn std::error::Error>> {
    let (send, recv) = std::sync::mpsc::channel();
    main.with_webview(move |view| {
        let _ = send.send(
            unsafe { view.controller().CoreWebView2() }
                .and_then(|core| attach(core, view.environment(), None)),
        );
    })?;
    recv.recv()??;
    Ok(())
}

fn rect(bounds: Bounds) -> wry::Rect {
    wry::Rect {
        position: LogicalPosition::new(bounds.x, bounds.y).into(),
        size: LogicalSize::new(bounds.width, bounds.height).into(),
    }
}

pub async fn open(
    app: AppHandle,
    url: Url,
    session: Uuid,
    bounds: Option<Bounds>,
) -> CommandResult<()> {
    let origin = url.origin();
    let label = format!("provider-{session}");
    let blank = Url::parse("about:blank").unwrap();
    if let Some(bounds) = bounds {
        let main = app
            .get_webview_window("main")
            .ok_or("Falta la ventana principal.")?;
        let cache = app
            .path()
            .app_cache_dir()
            .map_err(|e| e.to_string())?
            .join("provider-webview");
        let (send, recv) = tokio::sync::oneshot::channel();
        app.run_on_main_thread(move || {
            let result = (|| {
                // This child has no Tauri protocols, IPC handler or app capabilities.
                let mut context = wry::WebContext::new(Some(cache));
                let child = WebViewBuilder::new_with_web_context(&mut context)
                    .with_url(blank.as_str())
                    .with_incognito(true)
                    .with_focused(false)
                    .with_devtools(false)
                    .with_bounds(rect(bounds))
                    .with_navigation_handler(move |next| {
                        next == "about:blank"
                            || Url::parse(&next)
                                .is_ok_and(|u| u.origin() == origin && u.scheme() == "https")
                    })
                    .with_new_window_req_handler(|_, _| wry::NewWindowResponse::Deny)
                    .with_download_started_handler(|_, _| false)
                    .with_initialization_script_for_main_only(super::player_payload(&url), false)
                    .build_as_child(&main)
                    .map_err(|e| e.to_string())?;
                attach(child.webview(), child.environment(), Some(url.clone()))
                    .map_err(|e| e.to_string())?;
                child.load_url(url.as_str()).map_err(|e| e.to_string())?;
                PLAYERS.with(|players| players.borrow_mut().insert(session, child));
                Ok(())
            })();
            let _ = send.send(result);
        })
        .map_err(|e| e.to_string())?;
        return recv.await.map_err(|e| e.to_string())?;
    }
    let builder = WebviewWindowBuilder::new(&app, label, WebviewUrl::External(blank))
        .title(format!("UMD · {}", url.host_str().unwrap_or_default()))
        .inner_size(1000.0, 680.0)
        .incognito(true)
        .visible(false)
        .devtools(false)
        .disable_drag_drop_handler()
        .on_navigation(move |next| {
            next.as_str() == "about:blank" || next.origin() == origin && next.scheme() == "https"
        })
        .on_new_window(|_, _| NewWindowResponse::Deny)
        .on_download(|_, _| false);
    let builder = builder.initialization_script_for_all_frames(super::payload(&url));
    let window = builder.build().map_err(|e| e.to_string())?;
    let (send, recv) = tokio::sync::oneshot::channel();
    let source = url.clone();
    let result = async {
        window
            .with_webview(move |view| {
                let _ = send.send(
                    unsafe { view.controller().CoreWebView2() }
                        .and_then(|core| attach(core, view.environment(), Some(source))),
                );
            })
            .map_err(|e| e.to_string())?;
        recv.await
            .map_err(|e| e.to_string())?
            .map_err(|e| e.to_string())?;
        window.navigate(url).map_err(|e| e.to_string())?;
        window.show().map_err(|e| e.to_string())?;
        Ok(())
    }
    .await;
    if result.is_err() {
        let _ = window.destroy();
    }
    result
}

pub(super) fn layout(
    app: &AppHandle,
    session: Uuid,
    bounds: Bounds,
    focus: bool,
) -> CommandResult<()> {
    app.run_on_main_thread(move || {
        PLAYERS.with(|players| {
            if let Some(child) = players.borrow().get(&session) {
                let _ = child.set_bounds(rect(bounds));
                if focus {
                    let _ = child.focus();
                }
            }
        })
    })
    .map_err(|e| e.to_string())
}

pub(super) async fn command(
    app: &AppHandle,
    session: Uuid,
    script: String,
    close: bool,
) -> CommandResult<String> {
    if let Some(window) = app.get_webview_window(&format!("provider-{session}")) {
        if close {
            window.destroy().map_err(|e| e.to_string())?;
            return Ok("false".into());
        }
        let (send, mut recv) = tokio::sync::mpsc::channel(1);
        window
            .eval_with_callback(script, move |value| {
                let _ = send.try_send(value);
            })
            .map_err(|e| e.to_string())?;
        return tokio::time::timeout(std::time::Duration::from_secs(4), recv.recv())
            .await
            .map_err(|_| "El reproductor no respondió.")?
            .ok_or("Se cerró el reproductor.".into());
    }
    let (send, mut recv) = tokio::sync::mpsc::channel(1);
    app.run_on_main_thread(move || {
        PLAYERS.with(|players| {
            if close {
                players.borrow_mut().remove(&session);
                let _ = send.try_send(Ok("false".into()));
            } else if let Some(child) = players.borrow().get(&session) {
                let notify = send.clone();
                if let Err(e) = child.evaluate_script_with_callback(&script, move |value| {
                    let _ = notify.try_send(Ok(value));
                }) {
                    let _ = send.try_send(Err(e.to_string()));
                }
            } else {
                let _ = send.try_send(Ok("false".into()));
            }
        })
    })
    .map_err(|e| e.to_string())?;
    tokio::time::timeout(std::time::Duration::from_secs(4), recv.recv())
        .await
        .map_err(|_| "El reproductor no respondió.")?
        .ok_or("Se cerró el reproductor.")?
}
