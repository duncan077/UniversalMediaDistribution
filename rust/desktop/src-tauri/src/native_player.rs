use crate::CommandResult;
use serde_json::Value;
use tauri::Manager;

#[cfg(any(target_os = "android", test))]
fn subtitle_cues(text: &str) -> Vec<serde_json::Value> {
    use regex::Regex;
    use std::sync::LazyLock;
    static BLOCKS: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"\n[ \t]*\n").unwrap());
    static TIMING: LazyLock<Regex> = LazyLock::new(|| {
        Regex::new(
            r"((?:\d{1,3}:)?\d{2}:\d{2}[.,]\d{3})\s+-->\s+((?:\d{1,3}:)?\d{2}:\d{2}[.,]\d{3})",
        )
        .unwrap()
    });
    static TAGS: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"<[^>]+>").unwrap());
    fn millis(stamp: &str) -> Option<u64> {
        let (time, fraction) = stamp.rsplit_once(['.', ','])?;
        let mut seconds = 0_u64;
        for part in time.split(':') {
            seconds = seconds * 60 + part.parse::<u64>().ok()?;
        }
        Some(seconds * 1000 + fraction.parse::<u64>().ok()?)
    }
    let normalized = text.replace('\r', "");
    let mut cues: Vec<_> = BLOCKS
        .split(&normalized)
        .filter_map(|block| {
            let mut lines = block.lines();
            let timing = lines.find_map(|line| TIMING.captures(line))?;
            let start = millis(&timing[1])?;
            let end = millis(&timing[2])?;
            let text = lines.collect::<Vec<_>>().join("\n");
            let clean = TAGS
                .replace_all(&text, "")
                .replace("&lt;", "<")
                .replace("&gt;", ">")
                .replace("&amp;", "&");
            Some((
                start,
                serde_json::json!({"start":start, "end":end, "text":clean}),
            ))
        })
        .collect();
    cues.sort_by_key(|(start, _)| *start);
    cues.into_iter().map(|(_, cue)| cue).collect()
}

#[cfg(test)]
#[test]
fn android_subtitles_accept_vtt_and_srt_and_sort_cues() {
    let cues = subtitle_cues("WEBVTT\r\n\r\n00:03.000 --> 00:04.500 align:start\r\n<i>hola</i> &amp; adiós\r\n\r\n1\r\n00:00:01,200 --> 00:00:02,500\r\nprimero");
    assert_eq!(
        cues,
        vec![
            serde_json::json!({"start":1200,"end":2500,"text":"primero"}),
            serde_json::json!({"start":3000,"end":4500,"text":"hola & adiós"}),
        ]
    );
}

struct ClosingRelay(Option<uuid::Uuid>);
impl Drop for ClosingRelay {
    fn drop(&mut self) {
        if let Some(id) = self.0 {
            crate::hls_proxy::end(&id.to_string());
        }
    }
}

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
    let id =
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
    let _relay = ClosingRelay((operation == "close").then_some(id));
    if operation == "start" {
        if let Some(tracks) = args["tracks"].as_array_mut() {
            tracks.truncate(100);
            tracks.retain(|t| {
                t["url"].as_str().is_some_and(|s| {
                    matches!(s.split_once(':').map(|v| v.0), Some("http" | "https"))
                        && source(&window, s).is_ok()
                })
            });
        }
        let address = args["source"].as_str().unwrap_or("");
        let download = reqwest::Url::parse(address)
            .ok()
            .filter(|u| u.scheme() == "umdstream" || u.host_str() == Some("umdstream.localhost"));
        let playback = if let Some(url) = download {
            let token = url
                .path()
                .trim_start_matches('/')
                .parse()
                .map_err(|_| "Descarga inválida.")?;
            crate::torrent_stream::native_url(window.app_handle(), token).await?
        } else if let Some(referer) = args["referer"].as_str() {
            crate::hls_proxy::start(id, address, Some(referer), args["hls"] == true, &[])
                .await?
                .url
        } else {
            source(&window, address)?
        };
        args["source"] = Value::String(playback);
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
                    let page = args["referer"].as_str()
                        .and_then(|value| reqwest::Url::parse(value).ok())
                        .filter(umd_core::public_url::web_url);
                    tokio::time::timeout(
                        timeout,
                        crate::streams::fetch_media(url, timeout, page.as_ref(), None, page.as_ref()),
                    )
                    .await
                    .map_err(|_| "No se pudo descargar el subtítulo.")??
                };
            if !response.status().is_success() {
                return Err("No se pudo descargar el subtítulo.".into());
            }
            let text = crate::streams::bounded_text(response, 1024 * 1024).await?;
            let cues = crate::work(move || Ok(subtitle_cues(&text))).await?;
            if cues.is_empty() {
                return Err("No se encontraron subtítulos válidos.".into());
            }
            args["cues"] = Value::Array(cues);
        }
        let data = args.to_string();
        let (tx, rx) = tokio::sync::oneshot::channel();
        window
            .with_webview(move |web| {
                web.jni_handle().exec(move |env, activity, _| {
                    let result = (|| -> jni::errors::Result<String> {
                        let op = env.new_string(operation)?;
                        let json = env.new_string(data)?;
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
