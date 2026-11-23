use super::{blocked, CommandResult};
use reqwest::Url;
use tauri::{
    webview::NewWindowResponse, AppHandle, WebviewUrl, WebviewWindow, WebviewWindowBuilder,
};
use webview2_com::{
    take_pwstr, Microsoft::Web::WebView2::Win32::*, PermissionRequestedEventHandler,
    WebResourceRequestedEventHandler,
};
use windows_core::{w, Interface, PWSTR};

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
    view: tauri::webview::PlatformWebview,
    provider: Option<Url>,
) -> windows_core::Result<()> {
    // The controller and its event handlers are used on WebView2's UI thread.
    unsafe {
        let core = view.controller().CoreWebView2()?;
        unsafe extern "C" {
            fn umd_audio_attach(pid: u32);
        }
        let mut pid = 0;
        if core.BrowserProcessId(&mut pid).is_ok() {
            umd_audio_attach(pid);
        }
        let environment = view.environment();
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
        let _ = send.send(attach(view, None));
    })?;
    recv.recv()??;
    Ok(())
}

pub async fn open(app: AppHandle, url: Url, session: uuid::Uuid) -> CommandResult<()> {
    let origin = url.origin();
    let label = format!("provider-{session}");
    let blank = Url::parse("about:blank").unwrap();
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
                let _ = send.send(attach(view, Some(source)));
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
