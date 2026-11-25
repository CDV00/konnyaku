//! Signaling for the Konnyaku built-in classroom. Browsers connect to each other directly (WebRTC mesh); this
//! service only lets them find each other: it checks the ticket issued by the API, keeps who is in which room,
//! relays SDP/ICE messages, and reports joins, leaves and the end of a room back to the API.

pub mod config;
pub mod metrics;
pub mod protocol;
pub mod report;
pub mod rooms;
pub mod server;
pub mod ticket;

use std::sync::{Arc, Mutex};

pub use config::Config;
pub use report::Reporter;

/// Builds the shared state and the router; the caller serves it and runs [`server::sweeper`].
pub fn app(config: Config, reporter: Reporter) -> (axum::Router, server::Shared) {
    let state = Arc::new(server::AppState {
        config,
        rooms: Mutex::new(rooms::Rooms::default()),
        reporter,
    });
    (server::router(state.clone()), state)
}
