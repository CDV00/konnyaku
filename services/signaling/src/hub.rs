//! Realtime connections (`/rt`) of this replica, per user, and delivery of signals to them. Signals come from Redis
//! pub/sub (`user:{id}` channels, published by the API and the Worker after their database commit); every replica
//! receives every signal and forwards it to the sockets it holds, so replicas need nothing from each other.
//!
//! A signal says only "something new" (`{"t":"inbox","id":"…","seq":812}`): the browser fetches the content from the
//! API, where access is checked. The hub never looks inside beyond checking it is a small JSON object.

use std::collections::HashMap;
use std::sync::Mutex;
use std::sync::atomic::{AtomicU64, Ordering};

use tokio::sync::mpsc::{UnboundedReceiver, UnboundedSender, unbounded_channel};
use uuid::Uuid;

/// Redis channel of one user: `user:{uuid}`; the service subscribes to the pattern `user:*`.
pub const CHANNEL_PREFIX: &str = "user:";
pub const CHANNEL_PATTERN: &str = "user:*";

/// Signals are ids, not content: anything bigger is not forwarded.
pub const MAX_SIGNAL_BYTES: usize = 1024;

/// Sent to every connection after the Redis subscription was re-established: signals may have been missed, sync now.
pub const RESYNC: &str = r#"{"t":"resync"}"#;

/// One open `/rt` socket's inbox of outgoing frames.
pub struct Registration {
    pub id: u64,
    pub rx: UnboundedReceiver<String>,
}

#[derive(Default)]
pub struct Hub {
    users: Mutex<HashMap<Uuid, HashMap<u64, UnboundedSender<String>>>>,
    next_id: AtomicU64,
}

impl Hub {
    /// Adds a connection of `user`; `None` when the user already has `max_per_user` open here (runaway tabs or a script).
    pub fn register(&self, user: Uuid, max_per_user: usize) -> Option<Registration> {
        let mut users = self.users.lock().expect("hub lock");
        let connections = users.entry(user).or_default();
        if connections.len() >= max_per_user {
            return None;
        }
        let id = self.next_id.fetch_add(1, Ordering::Relaxed);
        let (tx, rx) = unbounded_channel();
        connections.insert(id, tx);
        Some(Registration { id, rx })
    }

    pub fn unregister(&self, user: Uuid, id: u64) {
        let mut users = self.users.lock().expect("hub lock");
        if let Some(connections) = users.get_mut(&user) {
            connections.remove(&id);
            if connections.is_empty() {
                users.remove(&user);
            }
        }
    }

    /// Forwards a signal from Redis channel `channel` to that user's sockets here. Returns how many got it.
    pub fn deliver(&self, channel: &str, payload: &str) -> usize {
        let Some(user) = user_of(channel) else {
            return 0;
        };
        if !is_signal(payload) {
            return 0;
        }
        let users = self.users.lock().expect("hub lock");
        users.get(&user).map_or(0, |connections| {
            connections
                .values()
                .filter(|tx| tx.send(payload.to_string()).is_ok())
                .count()
        })
    }

    /// Sends the same frame to every connection (e.g. [`RESYNC`]).
    pub fn broadcast(&self, payload: &str) -> usize {
        let users = self.users.lock().expect("hub lock");
        users
            .values()
            .flat_map(|connections| connections.values())
            .filter(|tx| tx.send(payload.to_string()).is_ok())
            .count()
    }

    pub fn connection_count(&self) -> usize {
        self.users.lock().expect("hub lock").values().map(HashMap::len).sum()
    }

    pub fn user_count(&self) -> usize {
        self.users.lock().expect("hub lock").len()
    }
}

/// `user:0193…` → the user id.
pub fn user_of(channel: &str) -> Option<Uuid> {
    channel
        .strip_prefix(CHANNEL_PREFIX)
        .and_then(|id| Uuid::parse_str(id).ok())
}

/// A small JSON object with a string `t` (type).
pub fn is_signal(payload: &str) -> bool {
    payload.len() <= MAX_SIGNAL_BYTES
        && serde_json::from_str::<serde_json::Value>(payload)
            .ok()
            .and_then(|v| v.get("t").map(serde_json::Value::is_string))
            .unwrap_or(false)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn channel(user: Uuid) -> String {
        format!("{CHANNEL_PREFIX}{user}")
    }

    #[test]
    fn delivers_to_every_tab_of_the_user_and_nobody_else() {
        let hub = Hub::default();
        let (alice, bob) = (Uuid::new_v4(), Uuid::new_v4());
        let mut tab1 = hub.register(alice, 5).unwrap();
        let mut tab2 = hub.register(alice, 5).unwrap();
        let mut other = hub.register(bob, 5).unwrap();

        let signal = r#"{"t":"inbox","id":"0193","seq":812}"#;
        assert_eq!(hub.deliver(&channel(alice), signal), 2);
        assert_eq!(tab1.rx.try_recv().unwrap(), signal);
        assert_eq!(tab2.rx.try_recv().unwrap(), signal);
        assert!(other.rx.try_recv().is_err());
        assert_eq!(hub.deliver(&channel(Uuid::new_v4()), signal), 0);
    }

    #[test]
    fn drops_bad_channels_and_anything_that_is_not_a_small_signal() {
        let hub = Hub::default();
        let user = Uuid::new_v4();
        let mut tab = hub.register(user, 5).unwrap();
        assert_eq!(hub.deliver("room:abc", r#"{"t":"inbox"}"#), 0);
        assert_eq!(hub.deliver(&format!("user:{user}x"), r#"{"t":"inbox"}"#), 0);
        assert_eq!(hub.deliver(&channel(user), "not json"), 0);
        assert_eq!(hub.deliver(&channel(user), r#"{"id":1}"#), 0);
        let big = format!(r#"{{"t":"inbox","pad":"{}"}}"#, "x".repeat(MAX_SIGNAL_BYTES));
        assert_eq!(hub.deliver(&channel(user), &big), 0);
        assert!(tab.rx.try_recv().is_err());
    }

    #[test]
    fn limits_connections_per_user_and_forgets_closed_ones() {
        let hub = Hub::default();
        let user = Uuid::new_v4();
        let first = hub.register(user, 2).unwrap();
        let _second = hub.register(user, 2).unwrap();
        assert!(hub.register(user, 2).is_none());
        assert_eq!((hub.connection_count(), hub.user_count()), (2, 1));

        hub.unregister(user, first.id);
        let _third = hub.register(user, 2).expect("a seat freed by the closed tab");
        assert_eq!(hub.broadcast(RESYNC), 2);
    }

    #[test]
    fn reads_the_user_from_the_channel() {
        let user = Uuid::new_v4();
        assert_eq!(user_of(&format!("user:{user}")), Some(user));
        assert_eq!(user_of("user:"), None);
        assert_eq!(user_of(&user.to_string()), None);
    }
}
