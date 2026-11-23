use serde::{Deserialize, Serialize};

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PartyContent {
    pub media_type: String,
    pub name: String,
    pub title: Option<String>,
    pub unit_kind: Option<String>,
    pub season_number: Option<u32>,
    pub display_number: Option<String>,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(
    tag = "type",
    rename_all = "lowercase",
    rename_all_fields = "camelCase"
)]
pub enum PartyCommand {
    Play {
        position_seconds: Option<f64>,
    },
    Pause {
        position_seconds: Option<f64>,
    },
    Seek {
        position_seconds: f64,
    },
    Position {
        position_seconds: f64,
    },
    Content {
        content: PartyContent,
        position_seconds: Option<f64>,
    },
    Buffering {
        is_buffering: bool,
    },
}
