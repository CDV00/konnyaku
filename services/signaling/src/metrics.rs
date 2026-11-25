//! Prometheus metrics at `GET /metrics` (internal network only: Caddy proxies `/rtc/*` and `/rt`, not this path).
//! Counters are process-wide atomics; room, people and socket counts are read at scrape time. No ids or names: only
//! totals.

use std::fmt::Write;
use std::sync::atomic::{AtomicU64, Ordering};

pub static JOINS: AtomicU64 = AtomicU64::new(0);
pub static JOIN_REJECTIONS: AtomicU64 = AtomicU64::new(0);
pub static MESSAGES: AtomicU64 = AtomicU64::new(0);
pub static REPORTS_DROPPED: AtomicU64 = AtomicU64::new(0);
pub static SIGNALS: AtomicU64 = AtomicU64::new(0);
pub static RT_REJECTIONS: AtomicU64 = AtomicU64::new(0);

pub fn inc(counter: &AtomicU64) {
    counter.fetch_add(1, Ordering::Relaxed);
}

/// Values read at scrape time.
pub struct Gauges {
    pub rooms: usize,
    pub peers: usize,
    pub rt_connections: usize,
    pub rt_users: usize,
    pub redis_up: bool,
}

/// Text exposition format 0.0.4.
pub fn render(gauges: &Gauges) -> String {
    let mut out = String::with_capacity(2048);
    let mut metric = |name: &str, kind: &str, help: &str, value: u64| {
        let _ = writeln!(out, "# HELP {name} {help}\n# TYPE {name} {kind}\n{name} {value}");
    };
    metric(
        "konnyaku_signaling_rooms",
        "gauge",
        "Open classroom rooms.",
        gauges.rooms as u64,
    );
    metric(
        "konnyaku_signaling_peers",
        "gauge",
        "People connected to a classroom room.",
        gauges.peers as u64,
    );
    metric(
        "konnyaku_signaling_joins_total",
        "counter",
        "Successful joins (including reconnections).",
        JOINS.load(Ordering::Relaxed),
    );
    metric(
        "konnyaku_signaling_join_rejections_total",
        "counter",
        "Joins refused: invalid or expired ticket, full room, ended room, bad first message.",
        JOIN_REJECTIONS.load(Ordering::Relaxed),
    );
    metric(
        "konnyaku_signaling_messages_total",
        "counter",
        "Messages received from connected clients (signal, state, end, leave).",
        MESSAGES.load(Ordering::Relaxed),
    );
    metric(
        "konnyaku_signaling_reports_dropped_total",
        "counter",
        "Presence events the API never accepted after every retry (attendance may be missing).",
        REPORTS_DROPPED.load(Ordering::Relaxed),
    );
    metric(
        "konnyaku_realtime_connections",
        "gauge",
        "Open realtime sockets (/rt), one per browser tab.",
        gauges.rt_connections as u64,
    );
    metric(
        "konnyaku_realtime_users",
        "gauge",
        "People with at least one realtime socket on this replica.",
        gauges.rt_users as u64,
    );
    metric(
        "konnyaku_realtime_redis_up",
        "gauge",
        "1 while the Redis subscription that feeds the realtime sockets is up.",
        u64::from(gauges.redis_up),
    );
    metric(
        "konnyaku_realtime_signals_total",
        "counter",
        "Signals received from Redis (before routing to sockets).",
        SIGNALS.load(Ordering::Relaxed),
    );
    metric(
        "konnyaku_realtime_rejections_total",
        "counter",
        "Realtime sockets refused: invalid or expired ticket, too many sockets for one user, bad first message.",
        RT_REJECTIONS.load(Ordering::Relaxed),
    );
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn renders_gauges_and_counters() {
        let text = render(&Gauges {
            rooms: 2,
            peers: 7,
            rt_connections: 3,
            rt_users: 2,
            redis_up: true,
        });
        assert!(text.contains("# TYPE konnyaku_signaling_rooms gauge\nkonnyaku_signaling_rooms 2\n"));
        assert!(text.contains("konnyaku_signaling_peers 7\n"));
        assert!(text.contains("# TYPE konnyaku_signaling_joins_total counter\n"));
        assert!(text.contains("konnyaku_realtime_connections 3\n"));
        assert!(text.contains("konnyaku_realtime_redis_up 1\n"));
    }
}
