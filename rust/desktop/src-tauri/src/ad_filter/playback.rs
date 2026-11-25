use crate::CommandResult;
use serde::{Deserialize, Serialize};
use tauri::Manager;
use uuid::Uuid;

#[derive(Clone, Copy, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Bounds {
    pub(super) x: f64,
    pub(super) y: f64,
    pub(super) width: f64,
    pub(super) height: f64,
    pub(super) page_width: f64,
}

impl Bounds {
    pub(super) fn validate(self) -> CommandResult<Self> {
        let dimensions = [self.x, self.y, self.width, self.height, self.page_width];
        if dimensions.iter().all(|v| (0.0..=32_768.0).contains(v))
            && self.width >= 1.0
            && self.height >= 1.0
            && self.page_width >= 1.0
        {
            Ok(self)
        } else {
            Err("Tamaño de reproductor inválido.".into())
        }
    }
}

#[tauri::command]
pub async fn provider_layout(
    window: tauri::WebviewWindow,
    session: Uuid,
    bounds: Bounds,
    focus: Option<bool>,
) -> CommandResult<()> {
    if window.label() != "main" {
        return Err("Acción reservada a la biblioteca.".into());
    }
    let bounds = bounds.validate()?;
    #[cfg(windows)]
    return super::windows::layout(window.app_handle(), session, bounds, focus.unwrap_or_default());
    #[cfg(target_os = "linux")]
    return super::linux::layout(window.app_handle(), session, bounds, focus.unwrap_or_default());
    #[cfg(target_os = "android")]
    return super::android::layout(window.app_handle(), session, bounds, focus).await;
    #[cfg(not(any(windows, target_os = "linux", target_os = "android")))]
    {
        let _ = (session, bounds, focus);
        Err("Plataforma no compatible.".into())
    }
}

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
    let result = super::windows::command(
        window.app_handle(),
        session,
        script,
        action == Action::Close,
    )
    .await?;
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
    if action != Action::Close && super::online() {
        super::player_in_use();
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
) -> CommandResult<Handoff> {
    if window.label() != "main" {
        return Err("Acción reservada a la biblioteca.".into());
    }
    let unavailable = "Este video necesita el reproductor del proveedor.";
    let script = format!(
        "{}('public-source',0)",
        include_str!("../../player-assets/provider-media.js")
    );
    let raw = super::eval_public(window.app_handle(), session, script).await?;
    if raw.len() > 96 << 10 {
        return Err(unavailable.into());
    }
    #[derive(Deserialize)]
    struct Observed {
        page: String,
        addresses: Vec<String>,
        position: f64,
        paused: bool,
    }
    let observed: Observed = serde_json::from_str(&raw).map_err(|_| unavailable)?;
    let parse = |raw: &str| {
        (raw.len() <= 16_384).then_some(())?;
        reqwest::Url::parse(raw)
            .ok()
            .filter(|url| url.scheme() == "https" && umd_core::public_url::web_url(url))
    };
    let mut page = parse(&observed.page).ok_or(unavailable)?;
    page.set_fragment(None);
    page.set_query(None);
    let resolution = async {
        for address in observed
            .addresses
            .iter()
            .take(4)
            .filter_map(|raw| parse(raw))
        {
            if let Ok(stream) = crate::streams::probe_public_media(address, &page).await {
                return Ok(Handoff {
                    stream,
                    position: observed.position.max(0.0),
                    paused: observed.paused,
                });
            }
        }
        Err(unavailable.to_owned())
    };
    // A separate HTTP client checks the media with no cookies or session headers.
    tokio::time::timeout(std::time::Duration::from_secs(20), resolution)
        .await
        .map_err(|_| unavailable.to_owned())?
}

#[derive(Serialize)]
pub struct Handoff {
    stream: crate::streams::Stream,
    position: f64,
    paused: bool,
}
