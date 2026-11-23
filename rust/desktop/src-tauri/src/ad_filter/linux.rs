use super::{Mode, SEED};
use crate::CommandResult;
use adblock::lists::{FilterSet, ParseOptions, RuleTypes};
use gtk::{gio, glib, glib::translate::*, prelude::*};
use javascriptcore::ValueExt;
use reqwest::Url;
use serde_json::json;
use std::{
    cell::RefCell,
    collections::HashMap,
    ffi::{c_void, CString},
    path::PathBuf,
    ptr,
};
use tauri::Manager;
use uuid::Uuid;
use webkit2gtk::SettingsExt;
use webkit2gtk::*;
use webkit2gtk::{ffi, PolicyDecisionType, UserContentManager, WebContext, WebView};

thread_local! {
    static PLAYERS:RefCell<HashMap<Uuid,(gtk::Window,WebView)>>=RefCell::new(HashMap::new());
}
pub(super) fn network_rules(lists: &[String]) -> String {
    let mut set = FilterSet::new(true);
    for text in std::iter::once(SEED).chain(lists.iter().map(String::as_str)) {
        set.add_filter_list(
            text.to_owned(),
            ParseOptions {
                rule_types: RuleTypes::NetworkOnly,
                ..Default::default()
            },
        );
    }
    let (rules, _) = set
        .into_content_blocking()
        .expect("debug FilterSet retains original rules");
    serde_json::to_string(&rules).expect("WebKit network rules serialize")
}

fn isolated_rules(mode: Mode) -> String {
    let standard = super::STANDARD.read().unwrap();
    let external = if mode.online() {
        standard.as_ref().map(|r| r.webkit.clone())
    } else {
        None
    };
    let mut rules: Vec<serde_json::Value> = if mode == Mode::Off {
        vec![]
    } else {
        serde_json::from_str(&external.unwrap_or_else(|| network_rules(&[])))
            .expect("serialized rules")
    };
    if mode.online() {
        // Keep same-site video segments and subtitles usable even if a subscription
        // matches a media filename. Reapply bundled blocks before isolation rules.
        rules.push(json!({"trigger":{"url-filter":r"\.(m3u8|mpd|m4s|ts|vtt|srt|ttml|mp4|m4a)([?#].*)?$","load-type":["first-party"]},"action":{"type":"ignore-previous-rules"}}));
        rules.extend(
            serde_json::from_str::<Vec<serde_json::Value>>(&network_rules(&[]))
                .expect("bundled WebKit rules"),
        );
    }
    // These apply after subscription exceptions, including when ad blocking is off.
    let mut block = |pattern: String| {
        rules.push(json!({"trigger":{"url-filter":pattern},"action":{"type":"block"}}))
    };
    for scheme in ["file", "ftp", "tauri", "asset", "ipc", "umdstream"] {
        block(format!("^{scheme}:"));
    }
    for scheme in ["https?", "wss?"] {
        for domain in ["localhost", "local"] {
            for suffix in ["[/:]", "$"] {
                block(format!(r"^{scheme}://([^/]*\.)?{domain}{suffix}"));
            }
        }
        for prefix in [
            r"127\.",
            r"0\.",
            r"10\.",
            r"169\.254\.",
            r"192\.168\.",
            r"172\.1[6-9]\.",
            r"172\.2[0-9]\.",
            r"172\.3[01]\.",
            r"\[",
        ] {
            block(format!("^{scheme}://{prefix}"));
        }
    }
    serde_json::to_string(&rules).expect("WebKit rule list")
}

type Compilation = (
    UserContentManager,
    tokio::sync::oneshot::Sender<CommandResult<()>>,
);
// The high-level GTK bindings omit WebKitUserContentFilterStore. WebKit retains
// its async arguments until completion on the GTK main context.
async fn attach(manager: &UserContentManager, root: PathBuf, rules: String) -> CommandResult<()> {
    unsafe extern "C" fn finished(
        object: *mut glib::gobject_ffi::GObject,
        result: *mut gio::ffi::GAsyncResult,
        data: *mut c_void,
    ) {
        let (manager, send) = *Box::from_raw(data.cast::<Compilation>());
        let mut error = ptr::null_mut();
        let filter =
            ffi::webkit_user_content_filter_store_save_finish(object.cast(), result, &mut error);
        let outcome = if filter.is_null() {
            Err(if error.is_null() {
                "No se pudo preparar el filtro web.".into()
            } else {
                glib::Error::from_glib_full(error).to_string()
            })
        } else {
            ffi::webkit_user_content_manager_add_filter(manager.to_glib_none().0, filter);
            ffi::webkit_user_content_filter_unref(filter);
            Ok(())
        };
        let _ = send.send(outcome);
    }
    let (send, recv) = tokio::sync::oneshot::channel();
    let path = CString::new(root.as_os_str().as_encoded_bytes()).map_err(|e| e.to_string())?;
    let bytes = glib::Bytes::from_owned(rules.into_bytes());
    unsafe {
        let store = ffi::webkit_user_content_filter_store_new(path.as_ptr());
        if store.is_null() {
            return Err("No se pudo abrir el filtro web.".into());
        }
        let state = Box::new((manager.clone(), send));
        ffi::webkit_user_content_filter_store_save(
            store,
            c"umd-player".as_ptr(),
            bytes.to_glib_none().0,
            ptr::null_mut(),
            Some(finished),
            Box::into_raw(state).cast(),
        );
        glib::gobject_ffi::g_object_unref(store.cast());
    }
    recv.await.map_err(|e| e.to_string())?
}

pub(super) async fn open(app: tauri::AppHandle, url: Url, session: Uuid) -> CommandResult<()> {
    let rules = isolated_rules(*super::MODE.lock().unwrap());
    let script = super::payload(&url);
    let root = app.state::<super::Directory>().0.join("webkit");
    std::fs::create_dir_all(&root).map_err(|e| e.to_string())?;
    let (send, recv) = tokio::sync::oneshot::channel();
    app.run_on_main_thread(move || {
        glib::MainContext::default().spawn_local(async move {
            let manager = UserContentManager::new();
            if let Err(e) = attach(&manager, root, rules).await {
                let _ = send.send(Err(e));
                return;
            }
            manager.add_script(&webkit2gtk::UserScript::new(
                &script,
                webkit2gtk::UserContentInjectedFrames::AllFrames,
                webkit2gtk::UserScriptInjectionTime::Start,
                &[],
                &[],
            ));
            let context = WebContext::new_ephemeral();
            context.connect_download_started(|_, download| download.cancel());
            let web = WebView::builder()
                .web_context(&context)
                .user_content_manager(&manager)
                .build();
            if let Some(settings) = WebViewExt::settings(&web) {
                settings.set_enable_developer_extras(false);
                settings.set_allow_file_access_from_file_urls(false);
                settings.set_allow_universal_access_from_file_urls(false);
                settings.set_javascript_can_open_windows_automatically(false);
            }
            web.connect_permission_request(|_, req| {
                req.deny();
                true
            });
            let origin = url.origin();
            web.connect_decide_policy(move |_, decision, kind| {
                let deny = match kind {
                    PolicyDecisionType::NewWindowAction => true,
                    PolicyDecisionType::NavigationAction => decision
                        .downcast_ref::<webkit2gtk::NavigationPolicyDecision>()
                        .and_then(|d| d.navigation_action())
                        .and_then(|a| a.request())
                        .and_then(|r| r.uri())
                        .is_none_or(|u| {
                            u.as_str() != "about:blank"
                                && !Url::parse(&u).is_ok_and(|u| {
                                    u.scheme() == "https" && umd_core::public_url::web_url(&u)
                                })
                        }),
                    PolicyDecisionType::Response => decision
                        .downcast_ref::<webkit2gtk::ResponsePolicyDecision>()
                        .is_some_and(|d| {
                            d.is_main_frame_main_resource()
                                && d.request().and_then(|r| r.uri()).is_none_or(|u| {
                                    !Url::parse(&u).is_ok_and(|u| u.origin() == origin)
                                })
                        }),
                    _ => false,
                };
                if deny {
                    decision.ignore();
                }
                deny
            });
            let window = gtk::Window::new(gtk::WindowType::Toplevel);
            window.set_default_size(1000, 680);
            window.set_title(&format!("UMD · {}", url.host_str().unwrap_or_default()));
            window.add(&web);
            window.connect_destroy(move |_| {
                PLAYERS.with(|players| {
                    players.borrow_mut().remove(&session);
                });
            });
            PLAYERS.with(|p| {
                p.borrow_mut()
                    .insert(session, (window.clone(), web.clone()))
            });
            window.show_all();
            web.load_uri(url.as_str());
            let _ = send.send(Ok(()));
        });
    })
    .map_err(|e| e.to_string())?;
    recv.await.map_err(|e| e.to_string())?
}

pub(super) async fn command(
    app: &tauri::AppHandle,
    id: Uuid,
    script: String,
    close: bool,
) -> CommandResult<String> {
    let (send, recv) = tokio::sync::oneshot::channel();
    app.run_on_main_thread(move || {
        let player = PLAYERS.with(|p| p.borrow().get(&id).cloned());
        let Some((window, web)) = player else {
            let _ = send.send(Ok("false".into()));
            return;
        };
        if close {
            window.close();
            let _ = send.send(Ok("false".into()));
            return;
        }
        glib::MainContext::default().spawn_local(async move {
            let result = web
                .evaluate_javascript_future(&script, None, None)
                .await
                .map(|v| {
                    v.to_json(0)
                        .map_or_else(|| "null".into(), |s| s.to_string())
                })
                .map_err(|e| e.to_string());
            let _ = send.send(result);
        });
    })
    .map_err(|e| e.to_string())?;
    tokio::time::timeout(std::time::Duration::from_secs(4), recv)
        .await
        .map_err(|_| "El reproductor no respondió.")?
        .map_err(|e| e.to_string())?
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    #[ignore = "requires a Linux graphical session or Xvfb"]
    fn webkit_compiles_rules_and_applies_host_payload() {
        gtk::init().unwrap();
        let dir = tempfile::tempdir().unwrap();
        glib::MainContext::default().block_on(async {
            let manager=UserContentManager::new();
            attach(&manager,dir.path().into(),isolated_rules(Mode::Basic)).await.unwrap();
            if let Some(source) = std::env::var_os("UMD_TEST_FILTER_LIST_DIR") {
                let lists: Vec<_> = std::fs::read_dir(source).unwrap()
                    .map(|file| std::fs::read_to_string(file.unwrap().path()).unwrap()).collect();
                assert!(!lists.is_empty());
                attach(&manager, dir.path().into(), network_rules(&lists)).await.unwrap();
            }
            let mut rules=super::super::cosmetic::Rules::default();
            rules.add("media.example##.sponsored",false);
            rules.add("media.example##div:unsupported-selector(test)",false);
            rules.add("media.example##+js(set-constant, qaAdEnabled, false)",false);
            let script=super::super::cosmetic::script("media.example",Some((&rules,false)),true);
            manager.add_script(&webkit2gtk::UserScript::new(&script,webkit2gtk::UserContentInjectedFrames::AllFrames,
                webkit2gtk::UserScriptInjectionTime::Start,&[],&[]));
            let web=WebView::builder().web_context(&WebContext::new_ephemeral()).user_content_manager(&manager).build();
            let window=gtk::Window::new(gtk::WindowType::Toplevel);
            window.add(&web);
            window.show_all();
            web.load_html("<p class='sponsored'>Ad</p><p>Film</p>",Some("https://media.example/watch"));
            let mut last=String::new();
            for _ in 0..100 {
                glib::timeout_future(std::time::Duration::from_millis(100)).await;
                if let Ok(v)=web.evaluate_javascript_future("location.href+' '+String(globalThis.qaAdEnabled)+' '+getComputedStyle(document.querySelector('.sponsored')).display",None,None).await {
                    last=v.to_str().to_string();
                    if last.ends_with("false none"){window.close();return;}
                }
            }
            panic!("WebKit filter did not apply: {last}");
        });
    }
}
