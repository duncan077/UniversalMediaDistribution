use super::{authenticated, bearer, hash_token, AccountStore};
use crate::{ApiError, ApiResult, ServerState};
use axum::{
    extract::{
        ws::{Message, WebSocket},
        State, WebSocketUpgrade,
    },
    http::{HeaderMap, StatusCode},
    routing::get,
    Router,
};
use serde_json::{json, Value};
use std::{
    collections::HashMap,
    sync::Arc,
    time::{Duration, Instant},
};
use tokio::{
    sync::{mpsc, Mutex},
    time::{interval, timeout, MissedTickBehavior},
};
use umd_core::device_playback::{
    playback_position, DevicePlaybackCommand, PlaybackControl, PlaybackEnd,
};
use uuid::Uuid;

const OFFER_TTL: Duration = Duration::from_secs(45);
const HEARTBEAT: Duration = Duration::from_secs(15);
const MAX_CONNECTIONS: usize = 256;
const SOCKET_LIMIT: usize = 8192;
const SOCKET_WRITE: Duration = Duration::from_secs(5);

#[derive(Clone, Default)]
pub(super) struct Registry(Arc<Mutex<HashMap<String, Devices>>>);

struct Peer {
    name: String,
    token_hash: String,
    outgoing: mpsc::Sender<Value>,
}

#[derive(Default)]
struct Devices {
    peers: HashMap<Uuid, Peer>,
    links: HashMap<Uuid, Link>,
}

struct Link {
    sender: Uuid,
    receiver: Uuid,
    expires: Instant,
    position_seconds: f64,
    paused: bool,
    ready: bool,
}

impl Link {
    fn includes(&self, device: Uuid) -> bool {
        self.sender == device || self.receiver == device
    }
}

impl Devices {
    fn send(&self, to: Uuid, message: Value) -> bool {
        self.peers
            .get(&to)
            .is_some_and(|peer| peer.outgoing.try_send(message).is_ok())
    }

    fn presence(&self) {
        for id in self.peers.keys().copied() {
            let mut devices: Vec<_> = self
                .peers
                .iter()
                .filter(|(other, _)| **other != id)
                .collect();
            devices.sort_unstable_by(|(left_id, left), (right_id, right)| {
                left.name.cmp(&right.name).then(left_id.cmp(right_id))
            });
            let devices: Vec<_> = devices
                .into_iter()
                .map(|(other, peer)| json!({"id":other,"name":peer.name}))
                .collect();
            self.send(id, json!({"kind":"devices","devices":devices}));
        }
    }

    fn end(&mut self, id: Uuid, reason: PlaybackEnd) {
        if let Some(link) = self.links.remove(&id) {
            let message = json!({"kind":"end","id":id,"reason":reason});
            self.send(link.sender, message.clone());
            self.send(link.receiver, message);
        }
    }

    fn expire(&mut self) {
        let now = Instant::now();
        let expired: Vec<_> = self
            .links
            .iter()
            .filter(|(_, link)| !link.ready && link.expires <= now)
            .map(|(&id, _)| id)
            .collect();
        for id in expired {
            self.end(id, PlaybackEnd::Unavailable);
        }
    }

    fn remove(&mut self, device: Uuid) {
        if self.peers.remove(&device).is_none() {
            return;
        }
        if let Some(id) = self
            .links
            .iter()
            .find(|(_, link)| link.includes(device))
            .map(|(&id, _)| id)
        {
            self.end(id, PlaybackEnd::Unavailable);
        }
        self.presence();
    }

    fn device_for_session(&self, token_hash: &str) -> Option<Uuid> {
        self.peers
            .iter()
            .find(|(_, p)| p.token_hash == token_hash)
            .map(|(&id, _)| id)
    }

    fn dispatch(&mut self, from: Uuid, command: DevicePlaybackCommand) {
        use DevicePlaybackCommand::*;
        self.expire();
        let Some(peer) = self.peers.get(&from) else {
            return;
        };
        match command {
            Offer {
                id,
                to,
                item,
                position_seconds,
                paused,
            } => {
                let available = from != to
                    && !id.is_nil()
                    && !self.links.contains_key(&id)
                    && !self
                        .links
                        .values()
                        .any(|link| link.includes(from) || link.includes(to));
                if available
                    && item.valid()
                    && playback_position(position_seconds)
                    && self.send(
                        to,
                        json!({"kind":"offer","id":id,"from":{"id":from,"name":peer.name},
                        "item":item,"positionSeconds":position_seconds,"paused":paused}),
                    )
                {
                    self.links.insert(
                        id,
                        Link {
                            sender: from,
                            receiver: to,
                            expires: Instant::now() + OFFER_TTL,
                            position_seconds,
                            paused,
                            ready: false,
                        },
                    );
                } else {
                    self.send(
                        from,
                        json!({"kind":"end","id":id,"reason":PlaybackEnd::Unavailable}),
                    );
                }
            }
            Ready { id, status } => {
                let Some(link) = self
                    .links
                    .get_mut(&id)
                    .filter(|link| link.receiver == from && !link.ready)
                else {
                    return;
                };
                if !status.valid()
                    || status.paused != link.paused
                    || (status.position_seconds - link.position_seconds).abs() > 4.0
                {
                    return;
                }
                link.ready = true;
                let sender = link.sender;
                if !self.send(sender, json!({"kind":"ready","id":id,"status":status})) {
                    self.end(id, PlaybackEnd::Unavailable);
                }
            }
            Update { id, status } => {
                if let Some(link) = self
                    .links
                    .get(&id)
                    .filter(|link| link.receiver == from && link.ready && status.valid())
                {
                    self.send(
                        link.sender,
                        json!({"kind":"update","id":id,"status":status}),
                    );
                }
            }
            Control { id, action } => {
                if let PlaybackControl::Seek { position_seconds } = &action {
                    if !playback_position(*position_seconds) {
                        return;
                    }
                }
                if let Some(link) = self
                    .links
                    .get(&id)
                    .filter(|link| link.sender == from && link.ready)
                {
                    if !self.send(
                        link.receiver,
                        json!({"kind":"control","id":id,"action":action}),
                    ) {
                        self.end(id, PlaybackEnd::Unavailable);
                    }
                }
            }
            End { id, reason } => {
                if self.links.get(&id).is_some_and(|link| link.includes(from)) {
                    self.end(id, reason);
                }
            }
        }
    }
}

impl Registry {
    pub(super) async fn revoke(&self, account: &str, token_hash: &str) {
        let mut registry = self.0.lock().await;
        if let Some(devices) = registry.get_mut(account) {
            if let Some(id) = devices.device_for_session(token_hash) {
                devices.remove(id);
            }
            if devices.peers.is_empty() {
                registry.remove(account);
            }
        }
    }
}

pub(super) fn routes() -> Router<ServerState> {
    Router::new().route("/api/v1/devices/playback", get(upgrade))
}

async fn authenticate(store: &AccountStore, token: String) -> Result<String, ApiError> {
    store
        .read(move |db| {
            let db = db.lock()?;
            authenticated(&db, &token)
        })
        .await
}

async fn upgrade(
    State(state): State<ServerState>,
    headers: HeaderMap,
    ws: WebSocketUpgrade,
) -> ApiResult {
    let token = bearer(&headers)?;
    let owner = authenticate(&state.accounts, token.clone()).await?;
    let name = headers
        .get("x-umd-device")
        .and_then(|value| std::str::from_utf8(value.as_bytes()).ok())
        .map(str::trim)
        .filter(|name| !name.is_empty() && name.len() <= 160 && !name.chars().any(char::is_control))
        .unwrap_or("UMD")
        .to_owned();
    let store = state.accounts;
    Ok(ws
        .max_message_size(SOCKET_LIMIT)
        .max_frame_size(SOCKET_LIMIT)
        .on_upgrade(move |socket| connected(socket, store, owner, token, name)))
}

async fn connected(
    mut socket: WebSocket,
    store: AccountStore,
    owner: String,
    token: String,
    name: String,
) {
    let registry = &store.device_playback.0;
    let hash = hash_token(&token);
    let id = Uuid::new_v4();
    let (outgoing, mut messages) = mpsc::channel(24);
    {
        let mut accounts = registry.lock().await;
        let replaced = accounts
            .get(&owner)
            .and_then(|devices| devices.device_for_session(&hash));
        if replaced.is_none()
            && accounts
                .values()
                .map(|devices| devices.peers.len())
                .sum::<usize>()
                >= MAX_CONNECTIONS
        {
            return;
        }
        let devices = accounts.entry(owner.clone()).or_default();
        if let Some(old) = replaced {
            devices.remove(old);
        }
        if devices.peers.len() >= 16 {
            return;
        }
        devices.peers.insert(
            id,
            Peer {
                name,
                token_hash: hash,
                outgoing,
            },
        );
        devices.presence();
    }
    let mut heartbeat = interval(HEARTBEAT);
    heartbeat.set_missed_tick_behavior(MissedTickBehavior::Skip);
    heartbeat.tick().await;
    let mut last_seen = Instant::now();
    let mut window = last_seen;
    let mut commands: u32 = 0;
    loop {
        tokio::select! {
            _ = heartbeat.tick() => {
                if last_seen.elapsed() > HEARTBEAT * 3 { break }
                match authenticate(&store, token.clone()).await {
                    Ok(account) if account == owner => {},
                    Err(ApiError(StatusCode::TOO_MANY_REQUESTS | StatusCode::SERVICE_UNAVAILABLE, _)) => {},
                    _ => break,
                }
                if let Some(devices) = registry.lock().await.get_mut(&owner) { devices.expire(); }
                if !matches!(timeout(SOCKET_WRITE, socket.send(Message::Ping(Default::default()))).await, Ok(Ok(()))) { break }
            }
            next = messages.recv() => {
                let Some(message) = next else { break };
                if !matches!(timeout(SOCKET_WRITE, socket.send(Message::Text(message.to_string().into()))).await, Ok(Ok(()))) { break }
            }
            incoming = socket.recv() => {
                match incoming {
                    Some(Ok(Message::Text(text))) => {
                        last_seen = Instant::now();
                        if window.elapsed() >= Duration::from_secs(10) { window = last_seen; commands = 0; }
                        commands += 1;
                        if commands > 60 { break }
                        if let Ok(command) = serde_json::from_str(&text) {
                            if let Some(devices) = registry.lock().await.get_mut(&owner) { devices.dispatch(id, command); }
                        }
                    }
                    Some(Ok(Message::Pong(_) | Message::Ping(_))) => { last_seen = Instant::now(); }
                    _ => break,
                }
            }
        }
    }
    let mut accounts = registry.lock().await;
    if let Some(devices) = accounts.get_mut(&owner) {
        devices.remove(id);
        if devices.peers.is_empty() {
            accounts.remove(&owner);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use umd_core::device_playback::{PlaybackStatus, VideoReference};

    fn peer(devices: &mut Devices, name: &str) -> (Uuid, mpsc::Receiver<Value>) {
        let id = Uuid::new_v4();
        let (outgoing, messages) = mpsc::channel(24);
        devices.peers.insert(
            id,
            Peer {
                name: name.into(),
                token_hash: name.into(),
                outgoing,
            },
        );
        (id, messages)
    }

    fn offer(id: Uuid, to: Uuid) -> DevicePlaybackCommand {
        DevicePlaybackCommand::Offer {
            id,
            to,
            position_seconds: 82.,
            paused: false,
            item: VideoReference {
                item_id: Uuid::new_v4(),
                server_id: None,
                name: "Serie".into(),
                subtype: "series".into(),
                release_year: None,
                unit_kind: Some("episode".into()),
                display_number: Some("2".into()),
                season_number: Some(1),
                provider: None,
                provider_ref: None,
            },
        }
    }

    fn status() -> PlaybackStatus {
        PlaybackStatus {
            title: "Episodio 2".into(),
            position_seconds: 82.,
            duration_seconds: Some(600.),
            paused: false,
            can_previous: true,
            can_next: true,
        }
    }

    #[test]
    fn only_the_target_can_accept_and_only_the_sender_can_control() {
        use DevicePlaybackCommand::*;
        let mut devices = Devices::default();
        let (phone, mut phone_events) = peer(&mut devices, "Teléfono");
        let (tv, mut tv_events) = peer(&mut devices, "TV");
        let (laptop, mut laptop_events) = peer(&mut devices, "Portátil");
        let id = Uuid::new_v4();
        devices.dispatch(phone, offer(id, tv));
        let sent = tv_events.try_recv().unwrap();
        assert_eq!(sent["kind"], "offer");
        assert_eq!(sent["positionSeconds"], 82.);
        assert_eq!(sent["from"]["name"], "Teléfono");
        assert!(laptop_events.try_recv().is_err());
        devices.dispatch(
            phone,
            Control {
                id,
                action: PlaybackControl::Play,
            },
        );
        devices.dispatch(
            laptop,
            Ready {
                id,
                status: status(),
            },
        );
        assert!(tv_events.try_recv().is_err());
        assert!(phone_events.try_recv().is_err());

        devices.dispatch(
            tv,
            Ready {
                id,
                status: PlaybackStatus {
                    position_seconds: 120.,
                    ..status()
                },
            },
        );
        assert!(phone_events.try_recv().is_err());
        devices.dispatch(
            tv,
            Ready {
                id,
                status: status(),
            },
        );
        assert_eq!(phone_events.try_recv().unwrap()["kind"], "ready");
        devices.dispatch(
            laptop,
            Control {
                id,
                action: PlaybackControl::Next,
            },
        );
        devices.dispatch(
            tv,
            Control {
                id,
                action: PlaybackControl::Pause,
            },
        );
        assert!(tv_events.try_recv().is_err());
        devices.dispatch(
            phone,
            Control {
                id,
                action: PlaybackControl::Seek {
                    position_seconds: 15.,
                },
            },
        );
        assert_eq!(
            tv_events.try_recv().unwrap()["action"]["positionSeconds"],
            15.
        );
        devices.dispatch(
            tv,
            Update {
                id,
                status: status(),
            },
        );
        assert_eq!(phone_events.try_recv().unwrap()["kind"], "update");
        devices.remove(tv);
        assert_eq!(phone_events.try_recv().unwrap()["reason"], "unavailable");
        assert_eq!(
            phone_events.try_recv().unwrap()["devices"][0]["name"],
            "Portátil"
        );
        assert!(devices.links.is_empty());
    }

    #[test]
    fn expired_and_rejected_offers_release_both_devices() {
        use DevicePlaybackCommand::*;
        let mut devices = Devices::default();
        let (phone, mut phone_events) = peer(&mut devices, "Teléfono");
        let (tv, mut tv_events) = peer(&mut devices, "TV");
        let id = Uuid::new_v4();
        devices.dispatch(phone, offer(id, tv));
        tv_events.try_recv().unwrap();
        devices.links.get_mut(&id).unwrap().expires = Instant::now();
        devices.dispatch(
            tv,
            Ready {
                id,
                status: status(),
            },
        );
        assert_eq!(phone_events.try_recv().unwrap()["reason"], "unavailable");
        tv_events.try_recv().unwrap();
        devices.dispatch(phone, offer(id, tv));
        assert_eq!(tv_events.try_recv().unwrap()["kind"], "offer");
        devices.dispatch(
            tv,
            End {
                id,
                reason: PlaybackEnd::Rejected,
            },
        );
        assert_eq!(phone_events.try_recv().unwrap()["reason"], "rejected");
        assert!(devices.links.is_empty());
    }
}
