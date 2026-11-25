//! HTTP routes and one task pair (reader + writer) per WebSocket connection.

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
use crate::protocol::{ClientMessage, ErrorCode, ServerMessage};
use crate::report::Reporter;
use crate::rooms::{Outbound, Rooms};
use crate::ticket::{self, TicketError, unix_now};

pub struct AppState {
    pub config: Config,
    pub rooms: Mutex<Rooms>,
    pub reporter: Reporter,
}

pub type Shared = Arc<AppState>;

/// Largest message accepted from a client (SDP of a few tracks is well below).
const MAX_MESSAGE_BYTES: usize = 64 * 1024;
const JOIN_TIMEOUT: Duration = Duration::from_secs(10);

pub fn router(state: Shared) -> Router {
    Router::new()
        .route("/rtc/ws", get(upgrade))
        .route("/rtc/health", get(health))
        .route("/health", get(health))
        .with_state(state)
}

async fn health(State(state): State<Shared>) -> impl IntoResponse {
    let rooms = state.rooms.lock().expect("rooms lock");
    axum::Json(serde_json::json!({ "status": "ok", "rooms": rooms.room_count(), "peers": rooms.peer_count() }))
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
                let _ = sink.send(text(&ServerMessage::Error { code })).await;
                let _ = sink.close().await;
                return;
            }
        },
        _ => {
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
            joined.peer_id
        }
        Err(code) => {
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
