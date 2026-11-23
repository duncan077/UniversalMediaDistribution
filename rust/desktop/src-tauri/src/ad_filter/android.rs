use super::{blocked, CommandResult};
use jni::{
    objects::{JClass, JString, JValue},
    sys::jboolean,
    JNIEnv,
};
use reqwest::Url;
use tauri::Manager;

#[no_mangle]
pub extern "system" fn Java_org_universalmediadistribution_rust_PlayerFilter_denied(
    mut env: JNIEnv,
    _class: JClass,
    address: JString,
    page: JString,
    kind: JString,
    method: JString,
    isolated: jboolean,
) -> jboolean {
    let mut read = |v: &JString| env.get_string(v).map(String::from);
    let (Ok(address), Ok(page), Ok(kind), Ok(method)) =
        (read(&address), read(&page), read(&kind), read(&method))
    else {
        return isolated;
    };
    if isolated != 0 && !super::provider_resource(&address) {
        return 1;
    }
    blocked(&address, &page, &kind, &method) as jboolean
}

#[no_mangle]
pub extern "system" fn Java_org_universalmediadistribution_rust_PlayerFilter_injection(
    mut env: JNIEnv,
    _: JClass,
    address: JString,
) -> jni::sys::jstring {
    let script = env
        .get_string(&address)
        .ok()
        .and_then(|s| Url::parse(&String::from(s)).ok())
        .map(|u| super::payload(&u))
        .unwrap_or_default();
    env.new_string(script)
        .map(|s| s.into_raw())
        .unwrap_or(std::ptr::null_mut())
}

pub async fn open(
    app: tauri::AppHandle,
    url: Url,
    session: uuid::Uuid,
    english: bool,
    interactive: bool,
) -> CommandResult<()> {
    let hide_ads = *super::MODE.lock().unwrap() != super::Mode::Off;
    let main = app
        .get_webview_window("main")
        .ok_or("Falta la ventana principal.")?;
    let (send, recv) = tokio::sync::oneshot::channel();
    main.with_webview(move |view| {
        view.jni_handle().exec(move |env, activity, _| {
            let result = env.new_string(url.as_str()).and_then(|address| {
                let id = env.new_string(session.to_string())?;
                env.call_method(
                    activity,
                    "openFilteredPlayer",
                    "(Ljava/lang/String;ZLjava/lang/String;ZZ)V",
                    &[
                        JValue::Object(&address),
                        JValue::Bool(hide_ads as jboolean),
                        JValue::Object(&id),
                        JValue::Bool(english as jboolean),
                        JValue::Bool(interactive as jboolean),
                    ],
                )
                .map(|_| ())
            });
            if result.is_err() {
                let _ = env.exception_clear();
            }
            let _ = send.send(result.map_err(|_| "No se pudo abrir el reproductor.".to_string()));
        })
    })
    .map_err(|e| e.to_string())?;
    recv.await.map_err(|e| e.to_string())?
}

type Pending = std::collections::HashMap<i64, tokio::sync::oneshot::Sender<String>>;
static PENDING: std::sync::LazyLock<std::sync::Mutex<Pending>> =
    std::sync::LazyLock::new(Default::default);
static NEXT: std::sync::atomic::AtomicI64 = std::sync::atomic::AtomicI64::new(1);

#[no_mangle]
pub extern "system" fn Java_org_universalmediadistribution_rust_PlayerFilter_mediaResult(
    mut env: JNIEnv,
    _class: JClass,
    key: i64,
    value: JString,
) {
    if let Some(send) = PENDING.lock().unwrap().remove(&key) {
        if let Ok(result) = env.get_string(&value) {
            let _ = send.send(result.into());
        }
    }
}
pub async fn command(
    app: &tauri::AppHandle,
    session: uuid::Uuid,
    script: String,
    close: bool,
) -> CommandResult<String> {
    let main = app
        .get_webview_window("main")
        .ok_or("Falta la biblioteca.")?;
    let key = NEXT.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
    let (send, recv) = tokio::sync::oneshot::channel();
    PENDING.lock().unwrap().insert(key, send);
    let sent = main.with_webview(move |view| {
        view.jni_handle().exec(move |env, activity, _| {
            let call = (|| {
                let id = env.new_string(session.to_string())?;
                let script = env.new_string(script)?;
                env.call_method(
                    activity,
                    "providerMedia",
                    "(Ljava/lang/String;Ljava/lang/String;ZJ)V",
                    &[
                        JValue::Object(&id),
                        JValue::Object(&script),
                        JValue::Bool(close as jboolean),
                        JValue::Long(key),
                    ],
                )
            })();
            if call.is_err() {
                let _ = env.exception_clear();
                PENDING.lock().unwrap().remove(&key);
            }
        })
    });
    let result = if sent.is_ok() {
        tokio::time::timeout(std::time::Duration::from_secs(4), recv)
            .await
            .ok()
            .and_then(std::result::Result::ok)
    } else {
        None
    };
    PENDING.lock().unwrap().remove(&key);
    result.ok_or_else(|| "El reproductor no respondió.".into())
}
