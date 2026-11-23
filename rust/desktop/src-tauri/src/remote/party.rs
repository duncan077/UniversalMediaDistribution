use super::{json_response, RemoteState};
use crate::CommandResult;
use futures_util::{SinkExt, StreamExt};
use reqwest::{Method, StatusCode};
use serde::Deserialize;
use serde_json::{json, Value};
use std::time::Duration;
use tauri::{ipc::Channel, State};
use tokio::{sync::mpsc, task::JoinHandle, time::timeout};
use tokio_tungstenite::{
    connect_async_with_config,
    tungstenite::{client::IntoClientRequest, protocol::WebSocketConfig, Message},
};
use umd_core::{PartyCommand, PartyContent};
use uuid::Uuid;

pub(super) struct Connection {
    room: Uuid,
    outgoing: mpsc::Sender<String>,
    task: JoinHandle<()>,
}
impl Drop for Connection {
    fn drop(&mut self) {
        self.task.abort();
    }
}

#[derive(Deserialize)]
#[serde(
    tag = "kind",
    rename_all = "camelCase",
    rename_all_fields = "camelCase"
)]
pub enum Action {
    Current,
    Create {
        content: Option<PartyContent>,
        position_seconds: f64,
    },
    Join {
        join_code: String,
    },
    Leave,
    Options {
        host_controls_only: bool,
    },
}
const LOGIN: &str = "Iniciá sesión en Cuenta y respaldo para usar las salas.";
#[tauri::command]
pub async fn party_action(
    profile_id: Uuid,
    action: Action,
    remote: State<'_, RemoteState>,
) -> CommandResult<Value> {
    let mut sessions = remote.0.lock().await;
    let s = sessions.get_mut(&profile_id).ok_or(LOGIN)?;
    let (method, path, body) = match action {
        Action::Current => (Method::GET, "/current", None),
        Action::Create {
            content,
            position_seconds,
        } => (
            Method::POST,
            "",
            Some(json!({"content":content,"positionSeconds":position_seconds})),
        ),
        Action::Join { join_code } => (Method::POST, "/join", Some(json!({"joinCode":join_code}))),
        Action::Leave => (Method::DELETE, "/current", None),
        Action::Options { host_controls_only } => (
            Method::PUT,
            "/current/options",
            Some(json!({"hostControlsOnly":host_controls_only})),
        ),
    };
    let url = s
        .base
        .join(&format!("api/v1/watch-parties{path}"))
        .map_err(|e| e.to_string())?;
    let mut req = s.client.request(method, url).bearer_auth(&s.token);
    if let Some(body) = body {
        req = req.json(&body);
    }
    let response = req.send().await.map_err(|e| e.to_string())?;
    let server_time = response
        .headers()
        .get(reqwest::header::DATE)
        .and_then(|date| date.to_str().ok())
        .map(str::to_owned);
    let state = if response.status() == StatusCode::NO_CONTENT
        || (path == "/current" && response.status() == StatusCode::NOT_FOUND)
    {
        Value::Null
    } else {
        json_response(response).await?
    };
    if s.party_socket
        .as_ref()
        .is_some_and(|c| state["partyId"] != json!(c.room))
    {
        s.party_socket = None;
    }
    Ok(json!({"accountId":s.account,"state":state,"serverTime":server_time}))
}
#[tauri::command]
pub async fn party_connect(
    profile_id: Uuid,
    party_id: Uuid,
    events: Channel<Value>,
    remote: State<'_, RemoteState>,
) -> CommandResult<()> {
    let mut sessions = remote.0.lock().await;
    let s = sessions.get_mut(&profile_id).ok_or(LOGIN)?;
    let mut url = s
        .base
        .join("api/v1/watch-parties/socket")
        .map_err(|e| e.to_string())?;
    let scheme = if url.scheme() == "https" { "wss" } else { "ws" };
    url.set_scheme(scheme)
        .map_err(|_| "Dirección de sala inválida.")?;
    let mut request = url
        .as_str()
        .into_client_request()
        .map_err(|e| e.to_string())?;
    request.headers_mut().insert(
        "authorization",
        format!("Bearer {}", s.token)
            .parse()
            .map_err(|_| "Sesión inválida.")?,
    );
    let limit = Some(128 * 1024);
    let config = WebSocketConfig::default()
        .max_message_size(limit)
        .max_frame_size(limit);
    let (mut socket, _) = timeout(
        Duration::from_secs(10),
        connect_async_with_config(request, Some(config), true),
    )
    .await
    .map_err(|_| "La sala tardó demasiado en conectar.")?
    .map_err(|e| format!("No se pudo conectar a la sala: {e}"))?;
    let (outgoing, mut commands) = mpsc::channel::<String>(32);
    let task = tokio::spawn(async move {
        loop {
            tokio::select! {
                command=commands.recv()=>{let Some(command)=command else{break};
                    if !matches!(timeout(Duration::from_secs(8),socket.send(Message::Text(command.into()))).await,Ok(Ok(()))){break}
                }
                frame=socket.next()=>match frame{
                    Some(Ok(Message::Text(text)))=>{
                        let Ok(payload)=serde_json::from_str::<Value>(&text) else{continue};
                        if payload["state"]["partyId"]!=json!(party_id) || events.send(payload).is_err(){break}
                    }
                    Some(Ok(Message::Ping(_)))=>{if !matches!(timeout(Duration::from_secs(8),socket.flush()).await,Ok(Ok(()))){break}}
                    Some(Ok(Message::Pong(_)))=>{}
                    _=>break,
                }
            }
        }
        let _ = events.send(json!({"reason":"socketClosed"}));
    });
    s.party_socket = Some(Connection {
        room: party_id,
        outgoing,
        task,
    });
    Ok(())
}
#[tauri::command]
pub async fn party_send(
    profile_id: Uuid,
    party_id: Uuid,
    command: PartyCommand,
    remote: State<'_, RemoteState>,
) -> CommandResult<()> {
    let sessions = remote.0.lock().await;
    let connection = sessions
        .get(&profile_id)
        .and_then(|s| s.party_socket.as_ref())
        .filter(|c| c.room == party_id)
        .ok_or("La sala está desconectada.")?;
    connection
        .outgoing
        .try_send(serde_json::to_string(&command).map_err(|e| e.to_string())?)
        .map_err(|_| "La conexión está ocupada o cerrada.".into())
}
