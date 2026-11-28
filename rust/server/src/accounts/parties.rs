use super::{authenticated, bad, bearer};
use crate::{ApiError, ApiResult, ServerState};
use axum::{
    extract::{
        ws::{Message, WebSocket},
        DefaultBodyLimit, State, WebSocketUpgrade,
    },
    http::{HeaderMap, StatusCode},
    response::IntoResponse,
    routing::{get, post, put},
    Extension, Json, Router,
};
use chrono::{DateTime, Utc};
use serde::Deserialize;
use serde_json::{json, Value};
use std::{
    collections::HashMap,
    sync::Arc,
    time::{Duration, Instant},
};
use tokio::sync::{watch, Mutex};
use umd_core::{PartyCommand, PartyContent};
use uuid::Uuid;

const ALPHABET: &[u8] = b"ABCDEFGHJKMNPQRSTUVWXYZ23456789";
const MAX_MEMBERS: usize = 16;
const IDLE: Duration = Duration::from_secs(6 * 3600);
type Registry = Arc<Mutex<Rooms>>;

#[derive(Clone)]
struct Person {
    id: String,
    name: String,
}
struct Member {
    person: Person,
    sockets: HashMap<Uuid, watch::Sender<Arc<Value>>>,
    buffering: bool,
}
impl Member {
    fn new(person: Person) -> Self {
        Self {
            person,
            sockets: HashMap::new(),
            buffering: false,
        }
    }
}
struct Playback {
    playing: bool,
    seconds: f64,
    updated: DateTime<Utc>,
    by: String,
}
impl Playback {
    fn at(&self, now: DateTime<Utc>) -> f64 {
        self.seconds
            + if self.playing {
                (now - self.updated).num_milliseconds().max(0) as f64 / 1000.0
            } else {
                0.0
            }
    }
    fn set(&mut self, seconds: f64, now: DateTime<Utc>) {
        self.seconds = seconds.clamp(0.0, 172800.0);
        self.updated = now;
    }
}
struct Room {
    id: Uuid,
    code: String,
    members: Vec<Member>,
    content: Option<Value>,
    playback: Playback,
    created: DateTime<Utc>,
    active: Instant,
    sequence: u64,
    locked: bool,
    waiting: bool,
}

fn content(input: PartyContent) -> Result<Value, ApiError> {
    fn optional(s: Option<&str>) -> Option<&str> {
        s.map(str::trim).filter(|s| !s.is_empty())
    }
    let media = input.media_type.trim().to_lowercase();
    let name = input.name.trim();
    let title = optional(input.title.as_deref()).unwrap_or(name);
    if media.is_empty()
        || media.len() > 64
        || name.is_empty()
        || name.encode_utf16().count() > 512
        || title.encode_utf16().count() > 512
        || [input.unit_kind.as_ref(), input.display_number.as_ref()]
            .into_iter()
            .flatten()
            .any(|s| s.encode_utf16().count() > 32)
        || input.season_number.is_some_and(|n| n > 10000)
    {
        return Err(bad("El título o la unidad de la sala son inválidos."));
    }
    Ok(
        json!({"mediaType":media,"itemKey":umd_core::friend_item_key(&media,name),"name":name,"title":title,
        "unitKind":optional(input.unit_kind.as_deref()),"seasonNumber":input.season_number,"displayNumber":optional(input.display_number.as_deref())}),
    )
}

impl Room {
    fn host(&self) -> &str {
        &self.members[0].person.id
    }
    fn state(&self) -> Value {
        let members = self
            .members
            .iter()
            .enumerate()
            .map(|(i, m)| {
                json!({
            "accountId":m.person.id,"displayName":m.person.name,"isHost":i==0,
            "isConnected":!m.sockets.is_empty(),"isBuffering":m.buffering})
            })
            .collect::<Vec<_>>();
        json!({"partyId":self.id,"joinCode":self.code,"hostAccountId":self.host(),"members":members,"content":self.content,
            "playback":{"isPlaying":self.playback.playing,"positionSeconds":self.playback.seconds,
                "updatedAt":self.playback.updated,"updatedByDisplayName":self.playback.by},
            "createdAt":self.created,"hostControlsOnly":self.locked})
    }
    fn broadcast(&mut self, reason: &str, actor: &Person) {
        self.sequence += 1;
        self.active = Instant::now();
        let event = Arc::new(
            json!({"reason":reason,"sequence":self.sequence,"initiatedByAccountId":actor.id,
            "initiatedByDisplayName":actor.name,"state":self.state()}),
        );
        for socket in self.members.iter().flat_map(|m| m.sockets.values()) {
            socket.send_replace(event.clone());
        }
    }
    fn resume_if_ready(&mut self, now: DateTime<Utc>) {
        if self.waiting && !self.members.iter().any(|m| m.buffering) {
            self.waiting = false;
            self.playback.playing = true;
            self.playback.updated = now;
        }
    }
    fn command(&mut self, actor: &Person, socket: Uuid, cmd: PartyCommand) {
        let Some(index) = self
            .members
            .iter()
            .position(|m| m.person.id == actor.id && m.sockets.contains_key(&socket))
        else {
            return;
        };
        let host = index == 0;
        let now = Utc::now();
        let playing = matches!(cmd, PartyCommand::Play { .. });
        let reason = match cmd {
            PartyCommand::Play { position_seconds } | PartyCommand::Pause { position_seconds }
                if host || !self.locked =>
            {
                self.playback.set(
                    position_seconds.unwrap_or_else(|| self.playback.at(now)),
                    now,
                );
                self.playback.playing = playing;
                self.playback.by = actor.name.clone();
                self.waiting = false;
                if playing {
                    "play"
                } else {
                    "pause"
                }
            }
            PartyCommand::Seek { position_seconds } if host || !self.locked => {
                self.playback.set(position_seconds, now);
                self.playback.by = actor.name.clone();
                "seek"
            }
            PartyCommand::Position { position_seconds } if host => {
                self.playback.set(position_seconds, now);
                "position"
            }
            PartyCommand::Content {
                content: next,
                position_seconds,
            } if host => {
                let Ok(next) = content(next) else { return };
                self.content = Some(next);
                self.playback.set(position_seconds.unwrap_or_default(), now);
                self.playback.playing = true;
                self.playback.by = actor.name.clone();
                self.waiting = false;
                for m in &mut self.members {
                    m.buffering = false;
                }
                "content"
            }
            PartyCommand::Buffering { is_buffering }
                if self.members[index].buffering != is_buffering =>
            {
                self.members[index].buffering = is_buffering;
                if is_buffering && self.playback.playing {
                    self.playback.set(self.playback.at(now), now);
                    self.playback.playing = false;
                    self.waiting = true;
                    self.playback.by = actor.name.clone();
                } else {
                    self.resume_if_ready(now);
                }
                "buffering"
            }
            _ => return,
        };
        self.broadcast(reason, actor);
    }
}

#[derive(Default)]
struct Rooms {
    rooms: HashMap<String, Room>,
    by_member: HashMap<String, String>,
}
fn missing() -> ApiError {
    ApiError(StatusCode::NOT_FOUND, "No se encontró la sala.".into())
}
impl Rooms {
    fn expire(&mut self, now: Instant) {
        self.rooms.retain(|_, r| {
            if now.saturating_duration_since(r.active) < IDLE
                || r.members.iter().any(|m| !m.sockets.is_empty())
            {
                return true;
            }
            for member in &r.members {
                self.by_member.remove(&member.person.id);
            }
            false
        });
    }
    fn current(&mut self, id: &str) -> Result<&mut Room, ApiError> {
        self.by_member
            .get(id)
            .and_then(|code| self.rooms.get_mut(code))
            .ok_or_else(missing)
    }
    fn leave(&mut self, actor: &Person) -> Result<(), ApiError> {
        let code = self.by_member.remove(&actor.id).ok_or_else(missing)?;
        let room = self.rooms.get_mut(&code).ok_or_else(missing)?;
        room.members.retain(|m| m.person.id != actor.id);
        if room.members.is_empty() {
            self.rooms.remove(&code);
        } else {
            room.resume_if_ready(Utc::now());
            room.broadcast("left", actor);
        }
        Ok(())
    }
    fn create(&mut self, actor: Person, input: Create) -> Result<Value, ApiError> {
        let content = input.content.map(content).transpose()?;
        let code = loop {
            let mut bytes = [0; 6];
            getrandom::fill(&mut bytes).map_err(|_| {
                ApiError(
                    StatusCode::INTERNAL_SERVER_ERROR,
                    "No se pudo generar el código.".into(),
                )
            })?;
            let code: String = bytes
                .iter()
                .map(|b| ALPHABET[*b as usize % ALPHABET.len()] as char)
                .collect();
            if !self.rooms.contains_key(&code) {
                break code;
            }
        };
        let _ = self.leave(&actor);
        let now = Utc::now();
        let room = Room {
            id: Uuid::new_v4(),
            code: code.clone(),
            members: vec![Member::new(actor.clone())],
            content,
            playback: Playback {
                playing: false,
                seconds: input.position_seconds.clamp(0.0, 172800.0),
                updated: now,
                by: actor.name,
            },
            created: now,
            active: Instant::now(),
            sequence: 0,
            locked: false,
            waiting: false,
        };
        let result = room.state();
        self.by_member.insert(actor.id, code.clone());
        self.rooms.insert(code, room);
        Ok(result)
    }
    fn join(&mut self, actor: Person, code: String) -> Result<Value, ApiError> {
        let code = code.trim().to_ascii_uppercase();
        let room = self.rooms.get(&code).ok_or_else(missing)?;
        if self.by_member.get(&actor.id) == Some(&code) {
            return Ok(room.state());
        }
        if room.members.len() >= MAX_MEMBERS {
            return Err(ApiError(StatusCode::CONFLICT, "La sala está llena.".into()));
        }
        let _ = self.leave(&actor);
        let room = self.rooms.get_mut(&code).ok_or_else(missing)?;
        room.members.push(Member::new(actor.clone()));
        self.by_member.insert(actor.id.clone(), code);
        room.broadcast("joined", &actor);
        Ok(room.state())
    }
}
async fn person(state: ServerState, h: &HeaderMap) -> Result<Person, ApiError> {
    let token = bearer(h)?;
    state
        .accounts
        .work(move |store| {
            let db = store.lock()?;
            let id = authenticated(&db, &token)?;
            let name = db.query_row(
                "SELECT display_name FROM accounts WHERE id=?1",
                [&id],
                |r| r.get(0),
            )?;
            Ok(Person { id, name })
        })
        .await
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Create {
    content: Option<PartyContent>,
    #[serde(default)]
    position_seconds: f64,
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Join {
    join_code: String,
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Options {
    host_controls_only: bool,
}

async fn create(
    State(s): State<ServerState>,
    Extension(reg): Extension<Registry>,
    h: HeaderMap,
    Json(input): Json<Create>,
) -> ApiResult {
    let actor = person(s, &h).await?;
    let mut rooms = reg.lock().await;
    rooms.expire(Instant::now());
    Ok(Json(rooms.create(actor, input)?).into_response())
}
async fn join(
    State(s): State<ServerState>,
    Extension(reg): Extension<Registry>,
    h: HeaderMap,
    Json(input): Json<Join>,
) -> ApiResult {
    let actor = person(s, &h).await?;
    let mut rooms = reg.lock().await;
    rooms.expire(Instant::now());
    Ok(Json(rooms.join(actor, input.join_code)?).into_response())
}
async fn current(
    State(s): State<ServerState>,
    Extension(reg): Extension<Registry>,
    h: HeaderMap,
) -> ApiResult {
    let actor = person(s, &h).await?;
    let mut rooms = reg.lock().await;
    rooms.expire(Instant::now());
    Ok(Json(rooms.current(&actor.id)?.state()).into_response())
}
async fn leave(
    State(s): State<ServerState>,
    Extension(reg): Extension<Registry>,
    h: HeaderMap,
) -> ApiResult {
    let actor = person(s, &h).await?;
    reg.lock().await.leave(&actor)?;
    Ok(StatusCode::NO_CONTENT.into_response())
}
async fn options(
    State(s): State<ServerState>,
    Extension(reg): Extension<Registry>,
    h: HeaderMap,
    Json(input): Json<Options>,
) -> ApiResult {
    let actor = person(s, &h).await?;
    let mut rooms = reg.lock().await;
    rooms.expire(Instant::now());
    let room = rooms.current(&actor.id)?;
    if room.host() != actor.id {
        return Err(ApiError(
            StatusCode::FORBIDDEN,
            "Solo el anfitrión puede cambiar los controles.".into(),
        ));
    }
    if room.locked != input.host_controls_only {
        room.locked = input.host_controls_only;
        room.broadcast("options", &actor);
    }
    Ok(Json(room.state()).into_response())
}
async fn socket(
    State(s): State<ServerState>,
    Extension(reg): Extension<Registry>,
    h: HeaderMap,
    ws: WebSocketUpgrade,
) -> ApiResult {
    let actor = person(s, &h).await?;
    let party = {
        let mut rooms = reg.lock().await;
        rooms.expire(Instant::now());
        rooms.current(&actor.id)?.id
    };
    Ok(ws
        .max_message_size(16 * 1024)
        .max_frame_size(16 * 1024)
        .on_upgrade(move |socket| connected(socket, reg, actor, party)))
}
async fn connected(mut socket: WebSocket, reg: Registry, actor: Person, party: Uuid) {
    let id = Uuid::new_v4();
    let (tx, mut rx) = watch::channel(Arc::new(Value::Null));
    {
        let mut rooms = reg.lock().await;
        let Ok(room) = rooms.current(&actor.id) else {
            return;
        };
        if room.id != party {
            return;
        }
        let Some(member) = room.members.iter_mut().find(|m| m.person.id == actor.id) else {
            return;
        };
        member.sockets.insert(id, tx);
        room.broadcast("connected", &actor);
    }
    let mut previous = None;
    loop {
        tokio::select! {
            changed=rx.changed()=>{
                if changed.is_err(){break}
                let event=rx.borrow_and_update().clone();
                let sequence=event["sequence"].as_u64().unwrap_or_default();
                let text=wire_event(&event,previous,sequence);
                previous=Some(sequence);
                let frame=Message::Text(text.into());
                if !matches!(tokio::time::timeout(Duration::from_secs(10),socket.send(frame)).await,Ok(Ok(()))){break}
            }
            incoming=socket.recv()=>match incoming{
                Some(Ok(Message::Text(text)))=>{
                    if let Ok(cmd)=serde_json::from_str::<PartyCommand>(&text){
                        let mut rooms=reg.lock().await;
                        let Ok(room)=rooms.current(&actor.id) else{break};
                        if room.id!=party{break}
                        room.command(&actor,id,cmd);
                    }
                }
                Some(Ok(Message::Ping(_) | Message::Pong(_)))=>{}
                _=>break
            }
        }
    }
    let mut rooms = reg.lock().await;
    if let Ok(room) = rooms.current(&actor.id) {
        if room.id == party {
            if let Some(m) = room.members.iter_mut().find(|m| m.person.id == actor.id) {
                if m.sockets.remove(&id).is_none() {
                    return;
                }
                if m.sockets.is_empty() {
                    m.buffering = false;
                }
            } else {
                return;
            }
            room.resume_if_ready(Utc::now());
            room.broadcast("disconnected", &actor);
        }
    }
}
// Older clients reconcile their player only on playback events. A first or
// coalesced snapshot must also reconcile transport when its reason was "joined".
fn wire_event(event: &Value, previous: Option<u64>, sequence: u64) -> String {
    if previous.and_then(|n| n.checked_add(1)) == Some(sequence) {
        return event.to_string();
    }
    let mut snapshot = event.clone();
    snapshot["reason"] = json!("position");
    snapshot["initiatedByAccountId"] = Value::Null;
    snapshot["initiatedByDisplayName"] = Value::Null;
    snapshot.to_string()
}
pub(super) fn routes() -> Router<ServerState> {
    Router::new()
        .route("/api/v1/watch-parties", post(create))
        .route("/api/v1/watch-parties/join", post(join))
        .route("/api/v1/watch-parties/current", get(current).delete(leave))
        .route("/api/v1/watch-parties/current/options", put(options))
        .route("/api/v1/watch-parties/socket", get(socket))
        .layer(DefaultBodyLimit::max(16 * 1024))
        .layer(Extension(Registry::default()))
}
#[cfg(test)]
mod tests {
    use super::*;
    fn actor(id: &str) -> Person {
        Person {
            id: id.to_owned(),
            name: id.to_owned(),
        }
    }
    fn create_room(rooms: &mut Rooms, host: &Person) -> String {
        rooms
            .create(
                host.clone(),
                Create {
                    content: None,
                    position_seconds: 3.0,
                },
            )
            .unwrap()["joinCode"]
            .as_str()
            .unwrap()
            .to_owned()
    }
    fn attach(room: &mut Room, index: usize) -> Uuid {
        let id = Uuid::new_v4();
        let (tx, _) = watch::channel(Arc::new(Value::Null));
        room.members[index].sockets.insert(id, tx);
        id
    }

    #[test]
    fn buffering_respects_explicit_pause_and_removed_connections() {
        let mut rooms = Rooms::default();
        let host = actor("host");
        let guest = actor("guest");
        let code = create_room(&mut rooms, &host);
        rooms.join(guest.clone(), code.to_lowercase()).unwrap();
        let room = rooms.current(&guest.id).unwrap();
        let h = attach(room, 0);
        let g = attach(room, 1);
        let play = PartyCommand::Play {
            position_seconds: Some(10.),
        };
        let stall = PartyCommand::Buffering { is_buffering: true };
        let ready = PartyCommand::Buffering {
            is_buffering: false,
        };
        room.locked = true;
        room.command(&guest, g, play.clone());
        assert!(!room.playback.playing);
        room.command(&host, h, play.clone());
        room.command(&guest, g, stall.clone());
        assert!(!room.playback.playing && room.waiting);
        room.command(
            &host,
            h,
            PartyCommand::Pause {
                position_seconds: None,
            },
        );
        room.command(&guest, g, ready);
        assert!(!room.playback.playing && !room.waiting);
        room.command(&host, h, play);
        room.command(&guest, g, stall);
        rooms.leave(&guest).unwrap();
        assert!(rooms.current(&host.id).unwrap().playback.playing);
        rooms.join(guest.clone(), code).unwrap();
        rooms.leave(&host).unwrap();
        let room = rooms.current(&guest.id).unwrap();
        assert_eq!(room.host(), guest.id);
        room.command(
            &guest,
            g,
            PartyCommand::Seek {
                position_seconds: 70.,
            },
        );
        assert!(room.playback.seconds < 70.);
    }

    #[test]
    fn failed_join_preserves_membership_and_only_idle_rooms_expire() {
        let mut rooms = Rooms::default();
        let host = actor("host");
        let full = create_room(&mut rooms, &host);
        for i in 1..MAX_MEMBERS {
            rooms.join(actor(&i.to_string()), full.clone()).unwrap();
        }
        let guest = actor("guest");
        let own = create_room(&mut rooms, &guest);
        assert_eq!(
            rooms.join(guest.clone(), full).unwrap_err().0,
            StatusCode::CONFLICT
        );
        assert_eq!(rooms.current(&guest.id).unwrap().code, own);
        let room = rooms.current(&host.id).unwrap();
        attach(room, 0);
        rooms.expire(Instant::now() + IDLE);
        assert!(rooms.current(&guest.id).is_err());
        assert!(rooms.current(&host.id).is_ok());
    }

    #[test]
    fn first_and_skipped_snapshots_resync_older_players() {
        let event = json!({"sequence":4,"reason":"joined","initiatedByAccountId":"user"});
        for prev in [None, Some(1)] {
            let state: Value = serde_json::from_str(&wire_event(&event, prev, 4)).unwrap();
            assert_eq!(state["reason"], "position");
            assert!(state["initiatedByAccountId"].is_null());
        }
        assert_eq!(wire_event(&event, Some(3), 4), event.to_string());
    }
}
