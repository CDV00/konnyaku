//! Realtime gateway of Konnyaku (image `konnyaku-signaling`).
//!
//! - Classroom signaling (`/rtc/ws`): browsers connect to each other directly (WebRTC mesh); this service only lets
//!   them find each other: it checks the ticket issued by the API, keeps who is in which room, relays SDP/ICE messages,
//!   and reports joins, leaves and the end of a room back to the API.
//! - Realtime signals (`/rt`): one socket per signed-in tab; signals published on Redis (`user:{id}`) by the API and
//!   the Worker are forwarded to the sockets of that user. Ids only, never content (doc 10.9).

pub mod bus;
pub mod config;
pub mod hub;
pub mod metrics;
pub mod protocol;
pub mod report;
pub mod rooms;
pub mod server;
pub mod ticket;

use std::sync::atomic::AtomicBool;
use std::sync::{Arc, Mutex};

pub use config::Config;
pub use report::Reporter;

/// Builds the shared state and the router; the caller serves it and runs [`server::sweeper`] (and [`bus::run`] when
/// Redis is configured).
pub fn app(config: Config, reporter: Reporter) -> (axum::Router, server::Shared) {
    let state = Arc::new(server::AppState {
        config,
        rooms: Mutex::new(rooms::Rooms::default()),
        reporter,
        hub: Arc::new(hub::Hub::default()),
        redis_up: Arc::new(AtomicBool::new(false)),
    });
    (server::router(state.clone()), state)
}
