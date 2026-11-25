use std::time::Duration;

/// Settings, read from environment variables (see README of the service).
#[derive(Clone, Debug)]
pub struct Config {
    /// `LISTEN_ADDR`, default `0.0.0.0:5100`.
    pub listen_addr: String,
    /// `SIGNALING_SECRET`: HMAC secret shared with the API (tickets in, reports out). At least 32 bytes.
    pub secret: String,
    /// `ROOM_CAPACITY`: people per room, everyone sending video to everyone (mesh). One seat is kept for the teacher.
    pub capacity: usize,
    /// `API_URL`: where presence reports go (`{API_URL}/api/workspace/internal/rooms/events`). Unset: not reported.
    pub api_url: Option<String>,
    /// `ROOM_GRACE_MINUTES`: a room still open this long after the session's planned end is closed.
    pub grace: Duration,
    /// How often the server pings each client; a client silent for 3 intervals is dropped.
    pub ping_interval: Duration,
    /// How often rooms are checked for automatic closing.
    pub sweep_interval: Duration,
}

pub const MIN_SECRET_BYTES: usize = 32;
pub const DEFAULT_CAPACITY: usize = 6;

impl Config {
    pub fn from_env() -> Result<Self, String> {
        let secret = std::env::var("SIGNALING_SECRET").unwrap_or_default();
        if secret.len() < MIN_SECRET_BYTES {
            return Err(format!(
                "SIGNALING_SECRET must be set to at least {MIN_SECRET_BYTES} bytes"
            ));
        }
        let capacity = match std::env::var("ROOM_CAPACITY") {
            Ok(v) => v
                .parse::<usize>()
                .map_err(|_| "ROOM_CAPACITY must be a number".to_string())?,
            Err(_) => DEFAULT_CAPACITY,
        };
        if capacity < 2 {
            return Err("ROOM_CAPACITY must be at least 2".into());
        }
        let grace_minutes = match std::env::var("ROOM_GRACE_MINUTES") {
            Ok(v) => v
                .parse::<u64>()
                .map_err(|_| "ROOM_GRACE_MINUTES must be a number".to_string())?,
            Err(_) => 30,
        };
        Ok(Self {
            listen_addr: std::env::var("LISTEN_ADDR").unwrap_or_else(|_| "0.0.0.0:5100".into()),
            secret,
            capacity,
            api_url: std::env::var("API_URL")
                .ok()
                .filter(|v| !v.is_empty())
                .map(|v| v.trim_end_matches('/').to_string()),
            grace: Duration::from_secs(grace_minutes * 60),
            ping_interval: Duration::from_secs(20),
            sweep_interval: Duration::from_secs(30),
        })
    }

    /// Settings for tests: small room, no reports, fast timers.
    pub fn for_tests(secret: &str, capacity: usize) -> Self {
        Self {
            listen_addr: "127.0.0.1:0".into(),
            secret: secret.into(),
            capacity,
            api_url: None,
            grace: Duration::from_secs(1800),
            ping_interval: Duration::from_secs(20),
            sweep_interval: Duration::from_millis(100),
        }
    }
}
