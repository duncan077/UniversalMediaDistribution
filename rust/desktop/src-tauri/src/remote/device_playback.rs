use super::RemoteState;
use crate::CommandResult;
use futures_util::{SinkExt, StreamExt};
use serde_json::{json, Value};
use std::time::Duration;
use tauri::{ipc::Channel, State};
use tokio::{sync::mpsc, task::JoinHandle, time::timeout};
use tokio_tungstenite::{
    connect_async_with_config,
    tungstenite::{client::IntoClientRequest, protocol::WebSocketConfig, Error, Message},
};
use umd_core::device_playback::DevicePlaybackCommand;
use uuid::Uuid;

pub(super) struct Connection {
    id: Uuid,
    outgoing: mpsc::Sender<DevicePlaybackCommand>,
    task: JoinHandle<()>,
}

impl Drop for Connection {
    fn drop(&mut self) {
        self.task.abort();
    }
}

#[tauri::command]
pub async fn device_playback_connect(
    profile_id: Uuid,
    connection_id: Uuid,
    label: String,
    events: Channel<Value>,
    remote: State<'_, RemoteState>,
) -> CommandResult<()> {
    let mut sessions = remote.0.lock().await;
    let session = sessions
        .get_mut(&profile_id)
        .ok_or("Iniciá sesión para transmitir a otro dispositivo.")?;
    let mut url = session
        .base
        .join("api/v1/devices/playback")
        .map_err(|e| e.to_string())?;
    let scheme = if url.scheme() == "https" { "wss" } else { "ws" };
    url.set_scheme(scheme)
        .map_err(|_| "Dirección de servidor inválida.")?;
    let mut request = url
        .as_str()
        .into_client_request()
        .map_err(|e| e.to_string())?;
    let name = session
        .device_name
        .as_deref()
        .filter(|name| !name.trim().is_empty())
        .unwrap_or(&label);
    let name: String = name
        .trim()
        .chars()
        .filter(|c| !c.is_control())
        .take(40)
        .collect();
    request.headers_mut().insert(
        "authorization",
        format!("Bearer {}", session.token)
            .parse()
            .map_err(|_| "Sesión inválida.")?,
    );
    if let Ok(value) = reqwest::header::HeaderValue::from_bytes(name.as_bytes()) {
        request.headers_mut().insert("x-umd-device", value);
    }
    let (outgoing, mut commands) = mpsc::channel::<DevicePlaybackCommand>(24);
    let task = tokio::spawn(async move {
        let config = WebSocketConfig::default()
            .max_message_size(Some(16 * 1024))
            .max_frame_size(Some(16 * 1024));
        let connection = timeout(
            Duration::from_secs(8),
            connect_async_with_config(request, Some(config), true),
        )
        .await;
        let mut socket = match connection {
            Ok(Ok((socket, _))) => socket,
            Ok(Err(Error::Http(response)))
                if matches!(response.status().as_u16(), 401 | 403 | 404 | 405 | 426) =>
            {
                let _ = events.send(json!({"kind":"closed","retry":false}));
                return;
            }
            _ => {
                let _ = events.send(json!({"kind":"closed","retry":true}));
                return;
            }
        };
        loop {
            tokio::select! {
                command = commands.recv() => {
                    let Some(command) = command else { break };
                    let Ok(message) = serde_json::to_string(&command) else { continue };
                    if !matches!(timeout(Duration::from_secs(5), socket.send(Message::Text(message.into()))).await, Ok(Ok(()))) { break }
                }
                next = timeout(Duration::from_secs(50), socket.next()) => {
                    match next {
                        Ok(Some(Ok(Message::Text(message)))) => {
                            if let Ok(payload) = serde_json::from_str::<Value>(&message) {
                                if events.send(payload).is_err() { break }
                            }
                        }
                        Ok(Some(Ok(Message::Ping(_)))) => {
                            if !matches!(timeout(Duration::from_secs(5), socket.flush()).await, Ok(Ok(()))) { break }
                        }
                        Ok(Some(Ok(Message::Pong(_)))) => {}
                        _ => break,
                    }
                }
            }
        }
        let _ = events.send(json!({"kind":"closed","retry":true}));
    });
    session.device_socket = Some(Connection {
        id: connection_id,
        outgoing,
        task,
    });
    Ok(())
}

#[tauri::command]
pub async fn device_playback_send(
    profile_id: Uuid,
    connection_id: Uuid,
    command: DevicePlaybackCommand,
    remote: State<'_, RemoteState>,
) -> CommandResult<()> {
    let sessions = remote.0.lock().await;
    let connection = sessions
        .get(&profile_id)
        .and_then(|session| session.device_socket.as_ref())
        .filter(|socket| socket.id == connection_id)
        .ok_or("La conexión de dispositivos se cerró.")?;
    connection
        .outgoing
        .try_send(command)
        .map_err(|_| "La conexión de dispositivos se cerró o está ocupada.".into())
}

#[tauri::command]
pub async fn device_playback_disconnect(
    profile_id: Uuid,
    connection_id: Uuid,
    remote: State<'_, RemoteState>,
) -> CommandResult<()> {
    let mut sessions = remote.0.lock().await;
    if let Some(session) = sessions.get_mut(&profile_id) {
        if session
            .device_socket
            .as_ref()
            .is_some_and(|socket| socket.id == connection_id)
        {
            session.device_socket = None;
        }
    }
    Ok(())
}
