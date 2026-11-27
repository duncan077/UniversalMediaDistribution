use serde::{Deserialize, Serialize};
use uuid::Uuid;

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct VideoReference {
    pub item_id: Uuid,
    pub server_id: Option<Uuid>,
    pub name: String,
    pub subtype: String,
    pub release_year: Option<u16>,
    pub unit_kind: Option<String>,
    pub display_number: Option<String>,
    pub season_number: Option<u32>,
    pub provider: Option<String>,
    pub provider_ref: Option<String>,
}

impl VideoReference {
    pub fn valid(&self) -> bool {
        !self.item_id.is_nil()
            && self.server_id.is_none_or(|id| !id.is_nil())
            && text(&self.name, 2048)
            && text(&self.subtype, 48)
            && self.provider.as_ref().is_none_or(|site| {
                (1..=64).contains(&site.len())
                    && site.bytes().all(|c| c.is_ascii_alphanumeric() || c == b'-')
            })
            && self
                .provider_ref
                .as_ref()
                .is_none_or(|id| self.provider.is_some() && crate::provider_reference(id))
            && match (self.unit_kind.as_deref(), self.display_number.as_deref()) {
                (None, None) => self.season_number.is_none(),
                (Some("episode"), Some(number)) => text(number, 64),
                _ => false,
            }
    }
}

fn text(value: &str, max: usize) -> bool {
    !value.trim().is_empty() && value.len() <= max && !value.chars().any(char::is_control)
}

pub fn playback_position(value: f64) -> bool {
    (0.0..=7.0 * 86_400.0).contains(&value)
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct PlaybackStatus {
    pub title: String,
    pub position_seconds: f64,
    pub duration_seconds: Option<f64>,
    pub paused: bool,
    pub can_previous: bool,
    pub can_next: bool,
}

impl PlaybackStatus {
    pub fn valid(&self) -> bool {
        text(&self.title, 2048)
            && playback_position(self.position_seconds)
            && self.duration_seconds.is_none_or(playback_position)
    }
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(
    tag = "kind",
    rename_all = "camelCase",
    rename_all_fields = "camelCase",
    deny_unknown_fields
)]
pub enum PlaybackControl {
    Play,
    Pause,
    Seek { position_seconds: f64 },
    Previous,
    Next,
}

#[derive(Clone, Copy, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum PlaybackEnd {
    Stopped,
    Rejected,
    Unavailable,
}

#[derive(Debug, Deserialize, Serialize)]
#[serde(
    tag = "kind",
    rename_all = "camelCase",
    rename_all_fields = "camelCase",
    deny_unknown_fields
)]
pub enum DevicePlaybackCommand {
    Offer {
        id: Uuid,
        to: Uuid,
        item: VideoReference,
        position_seconds: f64,
        paused: bool,
    },
    Ready {
        id: Uuid,
        status: PlaybackStatus,
    },
    Update {
        id: Uuid,
        status: PlaybackStatus,
    },
    Control {
        id: Uuid,
        action: PlaybackControl,
    },
    End {
        id: Uuid,
        reason: PlaybackEnd,
    },
}
