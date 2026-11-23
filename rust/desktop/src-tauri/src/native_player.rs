use crate::CommandResult;
use serde_json::Value;
use tauri::Manager;

/// Resolve file handles only from locations already authorized by the native picker.
fn source(window: &tauri::WebviewWindow, value: &str) -> CommandResult<String> {
    let url = reqwest::Url::parse(value).map_err(|_| "Dirección de reproducción inválida.")?;
    if (umd_core::public_url::web_url(&url)
        || crate::streams::trusted_server(&window.state::<umd_core::CatalogStore>(), &url)?)
        && !matches!(
            url.host_str(),
            Some("asset.localhost" | "umdstream.localhost")
        )
    {
        return Ok(url.into());
    }
    if url.scheme() == "file" {
        let path = url
            .to_file_path()
            .map_err(|_| "Archivo inválido.")?
            .canonicalize()
            .map_err(|e| e.to_string())?;
        if window.asset_protocol_scope().is_allowed(&path) && path.is_file() {
            return reqwest::Url::from_file_path(path)
                .map(String::from)
                .map_err(|_| "Archivo inválido.".into());
        }
    }
    Err("Esta fuente requiere el reproductor web.".into())
}

#[tauri::command]
pub async fn native_playback(
    window: tauri::WebviewWindow,
    operation: String,
    mut args: Value,
) -> CommandResult<Value> {
    if window.label() != "main" {
        return Err("Acción reservada a la biblioteca.".into());
    }
    uuid::Uuid::parse_str(args["id"].as_str().unwrap_or("")).map_err(|_| "Sesión inválida.")?;
    #[cfg(windows)]
    if operation == "wireless" {
        use windows_sys::Win32::UI::Input::KeyboardAndMouse::*;
        if !window.is_focused().unwrap_or(false) {
            return Err("La biblioteca debe estar en primer plano.".into());
        }
        let keys = [
            (VK_LWIN, 0),
            (0x4b, 0),
            (0x4b, KEYEVENTF_KEYUP),
            (VK_LWIN, KEYEVENTF_KEYUP),
        ]
        .map(|(key, flags)| INPUT {
            r#type: INPUT_KEYBOARD,
            Anonymous: INPUT_0 {
                ki: KEYBDINPUT {
                    wVk: key,
                    dwFlags: flags,
                    ..Default::default()
                },
            },
        });
        unsafe {
            let size = std::mem::size_of::<INPUT>() as i32;
            if SendInput(4, keys.as_ptr(), size) != 4 {
                SendInput(2, keys[2..].as_ptr(), size);
                return Err("Windows no pudo abrir las pantallas inalámbricas.".into());
            }
        }
        return Ok(serde_json::json!({}));
    }
    if operation == "close" {
        crate::hls_proxy::end(args["id"].as_str().unwrap_or(""));
    }
    if operation == "start" {
        let address = args["source"].as_str().unwrap_or("");
        let download = reqwest::Url::parse(address)
            .ok()
            .filter(|u| u.scheme() == "umdstream" || u.host_str() == Some("umdstream.localhost"));
        args["source"] = Value::String(if let Some(url) = download {
            let token = url
                .path()
                .trim_start_matches('/')
                .parse()
                .map_err(|_| "Descarga inválida.")?;
            crate::torrent_stream::native_url(window.app_handle(), token).await?
        } else if args["hls"].as_bool() == Some(true) && args["referer"].is_string() {
            crate::hls_proxy::start(&args, address).await?
        } else {
            source(&window, address)?
        });
        if let Some(tracks) = args["tracks"].as_array_mut() {
            tracks.truncate(100);
            tracks.retain(|t| {
                t["url"].as_str().is_some_and(|s| {
                    matches!(s.split_once(':').map(|v| v.0), Some("http" | "https"))
                        && source(&window, s).is_ok()
                })
            });
        }
    }
    #[cfg(target_os = "android")]
    {
        use jni::objects::{JString, JValue};
        if operation == "subtitle" && args["value"].as_i64().is_some_and(|v| v < -1) {
            let url = reqwest::Url::parse(args["address"].as_str().unwrap_or(""))
                .map_err(|_| "Subtítulo inválido.")?;
            let timeout = std::time::Duration::from_secs(12);
            let response =
                if crate::streams::trusted_server(&window.state::<umd_core::CatalogStore>(), &url)?
                {
                    crate::direct_client()?
                        .get(url)
                        .timeout(timeout)
                        .send()
                        .await
                        .map_err(|e| e.to_string())?
                } else {
                    crate::streams::get_public(url, timeout).await?
                };
            if !response.status().is_success() {
                return Err("No se pudo descargar el subtítulo.".into());
            }
            args["text"] =
                Value::String(crate::streams::bounded_text(response, 1024 * 1024).await?);
        }
        let (tx, rx) = tokio::sync::oneshot::channel();
        window
            .with_webview(move |web| {
                web.jni_handle().exec(move |env, activity, _| {
                    let result = (|| -> jni::errors::Result<String> {
                        let op = env.new_string(operation)?;
                        let json = env.new_string(args.to_string())?;
                        let text = env
                            .call_method(
                                activity,
                                "nativePlayback",
                                "(Ljava/lang/String;Ljava/lang/String;)Ljava/lang/String;",
                                &[JValue::Object(&op), JValue::Object(&json)],
                            )?
                            .l()?;
                        let value = JString::from(text);
                        let output = env.get_string(&value)?.into();
                        Ok(output)
                    })();
                    if result.is_err() {
                        let _ = env.exception_clear();
                    }
                    let _ = tx.send(result.map_err(|_| "El reproductor no respondió.".to_owned()));
                })
            })
            .map_err(|e| e.to_string())?;
        serde_json::from_str(&rx.await.map_err(|e| e.to_string())??).map_err(|e| e.to_string())
    }
    #[cfg(any(windows, target_os = "linux"))]
    {
        #[cfg(windows)]
        let viewport = if operation == "start" {
            Some(viewport::Viewport::create(&window, args["box"].clone()).await?)
        } else {
            None
        };
        crate::work(move || {
            desktop::command(
                &operation,
                &args,
                #[cfg(windows)]
                viewport,
            )
        })
        .await
    }
    #[cfg(not(any(windows, target_os = "linux", target_os = "android")))]
    {
        Err("Plataforma no compatible.".into())
    }
}

#[cfg(any(windows, target_os = "linux"))]
mod desktop;

#[cfg(windows)]
mod viewport;

#[cfg(any(windows, target_os = "linux"))]
mod cast;
