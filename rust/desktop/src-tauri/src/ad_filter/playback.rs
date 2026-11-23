use crate::CommandResult;
use serde::{Deserialize, Serialize};
use tauri::Manager;
use uuid::Uuid;

#[derive(Deserialize, Serialize, PartialEq)]
#[serde(rename_all = "lowercase")]
pub enum Action {
    Read,
    Play,
    Pause,
    Seek,
    Close,
}
#[derive(Deserialize, Serialize)]
pub struct Sample {
    position: f64,
    duration: Option<f64>,
    paused: bool,
    ended: bool,
    ready: u8,
    seeking: bool,
    seekable: bool,
    plays: u32,
    pauses: u32,
    seeks: u32,
}
#[derive(Serialize)]
pub struct Report {
    closed: bool,
    media: Option<Sample>,
}

#[tauri::command]
pub async fn provider_playback(
    window: tauri::WebviewWindow,
    session: Uuid,
    action: Action,
    position: Option<f64>,
) -> CommandResult<Report> {
    if window.label() != "main" {
        return Err("Acción reservada a la biblioteca.".into());
    }
    let position = position.unwrap_or_default();
    if !position.is_finite() || position < 0. {
        return Err("Posición inválida.".into());
    }
    let script = format!(
        "{}({},{})",
        include_str!("../../player-assets/provider-media.js"),
        serde_json::to_string(&action).map_err(|e| e.to_string())?,
        position
    );
    #[cfg(windows)]
    let result = {
        let Some(child) = window.get_webview_window(&format!("provider-{session}")) else {
            return Ok(Report {
                closed: true,
                media: None,
            });
        };
        if action == Action::Close {
            child.destroy().map_err(|e| e.to_string())?;
            return Ok(Report {
                closed: true,
                media: None,
            });
        }
        let (send, mut recv) = tokio::sync::mpsc::channel(1);
        child
            .eval_with_callback(script, move |value| {
                let _ = send.try_send(value);
            })
            .map_err(|e| e.to_string())?;
        tokio::time::timeout(std::time::Duration::from_secs(4), recv.recv())
            .await
            .map_err(|_| "El reproductor no respondió.")?
            .ok_or("Se cerró el reproductor.")?
    };
    #[cfg(target_os = "android")]
    let result = super::android::command(
        window.app_handle(),
        session,
        script,
        action == Action::Close,
    )
    .await?;
    #[cfg(target_os = "linux")]
    let result = super::linux::command(
        window.app_handle(),
        session,
        script,
        action == Action::Close,
    )
    .await?;
    #[cfg(not(any(windows, target_os = "android", target_os = "linux")))]
    let result = {
        let _ = (window, session, script);
        String::from("false")
    };
    if result == "false" {
        return Ok(Report {
            closed: true,
            media: None,
        });
    }
    let media: Option<Sample> =
        serde_json::from_str(&result).map_err(|_| "Respuesta de reproducción inválida.")?;
    let media =
        media.filter(|s| s.position >= 0. && s.ready <= 4 && s.duration.is_none_or(|d| d >= 0.));
    Ok(Report {
        closed: false,
        media,
    })
}

#[tauri::command]
pub async fn provider_public_stream(
    window: tauri::WebviewWindow,
    session: Uuid,
) -> CommandResult<crate::streams::Stream> {
    if window.label() != "main" {
        return Err("Acción reservada a la biblioteca.".into());
    }
    let unavailable = "Este video necesita el reproductor del proveedor.";
    let script = format!(
        "{}('public-source',0)",
        include_str!("../../player-assets/provider-media.js")
    );
    let raw = super::eval_public(window.app_handle(), session, script).await?;
    let address: String = serde_json::from_str(&raw).map_err(|_| unavailable)?;
    let url = reqwest::Url::parse(&address).map_err(|_| unavailable)?;
    let public = |u: &reqwest::Url| {
        u.scheme() == "https"
            && u.query().is_none()
            && u.fragment().is_none()
            && umd_core::public_url::web_url(u)
    };
    if !public(&url) {
        return Err(unavailable.into());
    }
    // Only hand off an ordinary public media response that plays independently of
    // the browser session. Keep authenticated and DRM playback in its original tab.
    let response = crate::streams::fetch_media(
        url,
        std::time::Duration::from_secs(10),
        None,
        Some("bytes=0-31"),
        None,
    )
    .await
    .map_err(|_| unavailable)?;
    let mime = response
        .headers()
        .get(reqwest::header::CONTENT_TYPE)
        .and_then(|v| v.to_str().ok())
        .unwrap_or("")
        .to_lowercase();
    if !public(response.url())
        || !(mime.starts_with("video/")
            || mime.starts_with("audio/")
            || mime.contains("mpegurl")
            || mime.starts_with("application/dash+xml"))
    {
        return Err(unavailable.into());
    }
    crate::streams::media(response.url().clone(), &mime).ok_or_else(|| unavailable.into())
}
