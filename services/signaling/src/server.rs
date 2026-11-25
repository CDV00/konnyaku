//! HTTP routes and one task pair (reader + writer) per WebSocket connection: classroom rooms (`/rtc/ws`) and the
//! realtime socket of every signed-in tab (`/rt`).

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use axum::Router;
use axum::extract::State;
use axum::extract::ws::{CloseFrame, Message, WebSocket, WebSocketUpgrade};
use axum::response::IntoResponse;
use axum::routing::get;
use futures_util::{SinkExt, StreamExt};
use tokio::sync::mpsc::{UnboundedReceiver, unbounded_channel};

use crate::config::Config;
use crate::hub::Hub;
use crate::metrics;
use crate::protocol::{ClientMessage, ErrorCode, ServerMessage};
use crate::report::Reporter;
use crate::rooms::{Outbound, Rooms};
use crate::ticket::{self, TicketError, unix_now};

pub struct AppState {
    pub config: Config,
    pub rooms: Mutex<Rooms>,
    pub reporter: Reporter,
    pub hub: Arc<Hub>,
    /// The Redis subscription feeding `/rt` is up (see `bus::run`).
    pub redis_up: Arc<AtomicBool>,
}

pub type Shared = Arc<AppState>;

/// Largest message accepted from a client (SDP of a few tracks is well below).
const MAX_MESSAGE_BYTES: usize = 64 * 1024;
const JOIN_TIMEOUT: Duration = Duration::from_secs(10);

pub fn router(state: Shared) -> Router {
    Router::new()
        .route("/rtc/ws", get(upgrade))
        .route("/rtc/health", get(health))
        .route("/rt", get(realtime_upgrade))
        .route("/rt/health", get(health))
        .route("/health", get(health))
        .route("/metrics", get(prometheus))
        .with_state(state)
}

async fn health(State(state): State<Shared>) -> impl IntoResponse {
    let (rooms, peers) = {
        let rooms = state.rooms.lock().expect("rooms lock");
        (rooms.room_count(), rooms.peer_count())
    };
    axum::Json(serde_json::json!({
        "status": "ok",
        "rooms": rooms,
        "peers": peers,
        "realtime": {
            "connections": state.hub.connection_count(),
            "redis": state.redis_up.load(Ordering::Relaxed),
        },
    }))
}

async fn prometheus(State(state): State<Shared>) -> impl IntoResponse {
    let (rooms, peers) = {
        let rooms = state.rooms.lock().expect("rooms lock");
        (rooms.room_count(), rooms.peer_count())
    };
    let gauges = metrics::Gauges {
        rooms,
        peers,
        rt_connections: state.hub.connection_count(),
        rt_users: state.hub.user_count(),
        redis_up: state.redis_up.load(Ordering::Relaxed),
    };
    (
        [(axum::http::header::CONTENT_TYPE, "text/plain; version=0.0.4")],
        metrics::render(&gauges),
    )
}

async fn upgrade(ws: WebSocketUpgrade, State(state): State<Shared>) -> impl IntoResponse {
    ws.max_message_size(MAX_MESSAGE_BYTES)
        .on_upgrade(move |socket| connection(socket, state))
}

/// Closes rooms nobody closed (see `Rooms::sweep`), forever.
pub async fn sweeper(state: Shared) {
    let mut interval = tokio::time::interval(state.config.sweep_interval);
    loop {
        interval.tick().await;
        let events = state
            .rooms
            .lock()
            .expect("rooms lock")
            .sweep(unix_now(), state.config.grace.as_secs() as i64);
        for event in events {
            tracing::info!(session = %event.session_id, "room closed automatically");
            state.reporter.send(event);
        }
    }
}

fn text(message: &ServerMessage) -> Message {
    Message::Text(message.to_json().into())
}

async fn connection(socket: WebSocket, state: Shared) {
    let (mut sink, mut stream) = socket.split();

    // 1. The first message must be a valid ticket.
    let first = tokio::time::timeout(JOIN_TIMEOUT, stream.next()).await;
    let Ok(Some(Ok(Message::Text(first)))) = first else {
        return;
    };
    let (ticket, media) = match serde_json::from_str::<ClientMessage>(&first) {
        Ok(ClientMessage::Join {
            ticket: token,
            state: media,
        }) => match ticket::verify(&state.config.secret, &token) {
            Ok(t) => (t, media),
            Err(e) => {
                let code = if e == TicketError::Expired {
                    ErrorCode::TicketExpired
                } else {
                    ErrorCode::InvalidTicket
                };
                metrics::inc(&metrics::JOIN_REJECTIONS);
                let _ = sink.send(text(&ServerMessage::Error { code })).await;
                let _ = sink.close().await;
                return;
            }
        },
        _ => {
            metrics::inc(&metrics::JOIN_REJECTIONS);
            let _ = sink
                .send(text(&ServerMessage::Error {
                    code: ErrorCode::BadMessage,
                }))
                .await;
            let _ = sink.close().await;
            return;
        }
    };

    // 2. Take a seat.
    let key = ticket.room_key();
    let (tx, rx) = unbounded_channel();
    let joined =
        state
            .rooms
            .lock()
            .expect("rooms lock")
            .join(&ticket, media, state.config.capacity, unix_now(), tx.clone());
    let peer_id = match joined {
        Ok(joined) => {
            for event in joined.events {
                state.reporter.send(event);
            }
            metrics::inc(&metrics::JOINS);
            joined.peer_id
        }
        Err(code) => {
            metrics::inc(&metrics::JOIN_REJECTIONS);
            let _ = sink.send(text(&ServerMessage::Error { code })).await;
            let _ = sink.close().await;
            return;
        }
    };
    tracing::info!(room = %key, peer = %peer_id, role = ?ticket.role, "joined");

    // 3. Writer: room messages and pings out; reader: client messages in.
    let writer = tokio::spawn(write_loop(sink, rx, state.config.ping_interval));
    let idle = state.config.ping_interval * 3;
    loop {
        let next = tokio::time::timeout(idle, stream.next()).await;
        let message = match next {
            Ok(Some(Ok(message))) => message,
            // Silent too long (no pong), closed, or broken.
            _ => break,
        };
        let raw = match message {
            Message::Text(raw) => raw,
            Message::Close(_) => break,
            _ => continue,
        };
        metrics::inc(&metrics::MESSAGES);
        match serde_json::from_str::<ClientMessage>(&raw) {
            Ok(ClientMessage::Signal { to, data }) => {
                state.rooms.lock().expect("rooms lock").relay(&key, peer_id, to, data)
            }
            Ok(ClientMessage::State(media)) => state.rooms.lock().expect("rooms lock").set_state(&key, peer_id, media),
            Ok(ClientMessage::End) => {
                let ended = state.rooms.lock().expect("rooms lock").end(&key, peer_id, unix_now());
                match ended {
                    Ok(event) => {
                        tracing::info!(room = %key, by = %peer_id, "room ended by host");
                        state.reporter.send(event);
                    }
                    Err(code) => {
                        let _ = tx.send(Outbound::Message(ServerMessage::Error { code }));
                    }
                }
            }
            Ok(ClientMessage::Leave) => break,
            Ok(ClientMessage::Join { .. }) | Err(_) => {
                let _ = tx.send(Outbound::Message(ServerMessage::Error {
                    code: ErrorCode::BadMessage,
                }));
            }
        }
    }

    // 4. Gone: free the seat (no-op when replaced or when the room ended) and stop the writer.
    if let Some(event) = state.rooms.lock().expect("rooms lock").leave(&key, peer_id, unix_now()) {
        state.reporter.send(event);
    }
    drop(tx);
    let _ = tokio::time::timeout(Duration::from_secs(2), writer).await;
    tracing::info!(room = %key, peer = %peer_id, "left");
}

async fn write_loop(
    mut sink: futures_util::stream::SplitSink<WebSocket, Message>,
    mut rx: UnboundedReceiver<Outbound>,
    ping_interval: Duration,
) {
    let mut ping = tokio::time::interval(ping_interval);
    ping.tick().await;
    loop {
        tokio::select! {
            outbound = rx.recv() => match outbound {
                Some(Outbound::Message(message)) => {
                    if sink.send(text(&message)).await.is_err() {
                        return;
                    }
                }
                Some(Outbound::Close(message)) => {
                    let _ = sink.send(text(&message)).await;
                    let _ = sink.send(Message::Close(Some(CloseFrame { code: 1000, reason: "bye".into() }))).await;
                    return;
                }
                None => {
                    let _ = sink.close().await;
                    return;
                }
            },
            _ = ping.tick() => {
                if sink.send(Message::Ping(Vec::new().into())).await.is_err() {
                    return;
                }
            }
        }
    }
}

// ---- realtime (/rt) -------------------------------------------------------------------------------------------------

/// Largest frame accepted from a browser on `/rt` (only the auth message is expected).
const MAX_RT_MESSAGE_BYTES: usize = 8 * 1024;
const RT_BUFFER_BYTES: usize = 4 * 1024;

#[derive(serde::Deserialize)]
#[serde(tag = "type", rename_all = "lowercase")]
enum RealtimeClientMessage {
    Auth { ticket: String },
}

fn rt_frame(value: serde_json::Value) -> Message {
    Message::Text(value.to_string().into())
}

async fn realtime_upgrade(ws: WebSocketUpgrade, State(state): State<Shared>) -> impl IntoResponse {
    // Signals are a few dozen bytes: small buffers keep an idle tab cheap (the 128 KiB defaults are for big frames).
    ws.max_message_size(MAX_RT_MESSAGE_BYTES)
        .read_buffer_size(RT_BUFFER_BYTES)
        .write_buffer_size(RT_BUFFER_BYTES)
        .on_upgrade(move |socket| realtime(socket, state))
}

/// One tab: `{"type":"auth","ticket":"…"}` first (ticket signed by the API, audience `konnyaku-realtime`), then only
/// the server talks: `{"t":"ready"}`, the signals of the user as they come, `{"t":"resync"}` after a Redis outage, and
/// `{"t":"reconnect"}` before closing at the end of the connection lifetime.
async fn realtime(socket: WebSocket, state: Shared) {
    let (mut sink, mut stream) = socket.split();

    let first = tokio::time::timeout(JOIN_TIMEOUT, stream.next()).await;
    let Ok(Some(Ok(Message::Text(first)))) = first else {
        metrics::inc(&metrics::RT_REJECTIONS);
        return;
    };
    let user = match serde_json::from_str::<RealtimeClientMessage>(&first) {
        Ok(RealtimeClientMessage::Auth { ticket: token }) => match ticket::verify_user(&state.config.secret, &token) {
            Ok(t) => t,
            Err(e) => {
                let code = if e == TicketError::Expired {
                    "ticket_expired"
                } else {
                    "invalid_ticket"
                };
                metrics::inc(&metrics::RT_REJECTIONS);
                let _ = sink
                    .send(rt_frame(serde_json::json!({ "t": "error", "code": code })))
                    .await;
                let _ = sink.close().await;
                return;
            }
        },
        Err(_) => {
            metrics::inc(&metrics::RT_REJECTIONS);
            let _ = sink
                .send(rt_frame(serde_json::json!({ "t": "error", "code": "bad_message" })))
                .await;
            let _ = sink.close().await;
            return;
        }
    };

    let Some(registration) = state.hub.register(user.sub, state.config.max_connections_per_user) else {
        metrics::inc(&metrics::RT_REJECTIONS);
        let _ = sink
            .send(rt_frame(
                serde_json::json!({ "t": "error", "code": "too_many_connections" }),
            ))
            .await;
        let _ = sink.close().await;
        return;
    };
    if sink.send(rt_frame(serde_json::json!({ "t": "ready" }))).await.is_err() {
        state.hub.unregister(user.sub, registration.id);
        return;
    }
    tracing::debug!(user = %user.sub, tenant = %user.tid, "realtime connected");

    let mut writer = tokio::spawn(realtime_write_loop(
        sink,
        registration.rx,
        state.config.ping_interval,
        state.config.connection_lifetime,
    ));
    let idle = state.config.ping_interval * 3;
    loop {
        tokio::select! {
            next = tokio::time::timeout(idle, stream.next()) => match next {
                // Pongs and anything else keep the socket alive; the browser has nothing to say yet.
                Ok(Some(Ok(Message::Close(_)))) | Ok(None) | Ok(Some(Err(_))) | Err(_) => break,
                Ok(Some(Ok(_))) => {}
            },
            // Lifetime over or the socket broke while writing.
            _ = &mut writer => break,
        }
    }

    state.hub.unregister(user.sub, registration.id);
    writer.abort();
    tracing::debug!(user = %user.sub, "realtime disconnected");
}

async fn realtime_write_loop(
    mut sink: futures_util::stream::SplitSink<WebSocket, Message>,
    mut rx: UnboundedReceiver<String>,
    ping_interval: Duration,
    lifetime: Duration,
) {
    let mut ping = tokio::time::interval(ping_interval);
    ping.tick().await;
    let deadline = tokio::time::sleep(lifetime);
    tokio::pin!(deadline);
    loop {
        tokio::select! {
            signal = rx.recv() => match signal {
                Some(text) => {
                    if sink.send(Message::Text(text.into())).await.is_err() {
                        return;
                    }
                }
                None => {
                    let _ = sink.close().await;
                    return;
                }
            },
            _ = ping.tick() => {
                if sink.send(Message::Ping(Vec::new().into())).await.is_err() {
                    return;
                }
            }
            () = &mut deadline => {
                let _ = sink.send(rt_frame(serde_json::json!({ "t": "reconnect" }))).await;
                let _ = sink.send(Message::Close(Some(CloseFrame { code: 1000, reason: "reconnect".into() }))).await;
                return;
            }
        }
    }
}
