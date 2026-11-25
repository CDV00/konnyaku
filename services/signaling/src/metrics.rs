//! Prometheus metrics at `GET /metrics` (internal network only: Caddy proxies `/rtc/*`, not this path). Counters are
//! process-wide atomics; room and people counts are read from the rooms at scrape time. No ids or names: only totals.

use std::fmt::Write;
use std::sync::atomic::{AtomicU64, Ordering};

pub static JOINS: AtomicU64 = AtomicU64::new(0);
pub static JOIN_REJECTIONS: AtomicU64 = AtomicU64::new(0);
pub static MESSAGES: AtomicU64 = AtomicU64::new(0);
pub static REPORTS_DROPPED: AtomicU64 = AtomicU64::new(0);

pub fn inc(counter: &AtomicU64) {
    counter.fetch_add(1, Ordering::Relaxed);
}

/// Text exposition format 0.0.4.
pub fn render(rooms: usize, peers: usize) -> String {
    let mut out = String::with_capacity(1024);
    let mut metric = |name: &str, kind: &str, help: &str, value: u64| {
        let _ = writeln!(out, "# HELP {name} {help}\n# TYPE {name} {kind}\n{name} {value}");
    };
    metric(
        "konnyaku_signaling_rooms",
        "gauge",
        "Open classroom rooms.",
        rooms as u64,
    );
    metric(
        "konnyaku_signaling_peers",
        "gauge",
        "People connected to a classroom room.",
        peers as u64,
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
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn renders_gauges_and_counters() {
        let text = render(2, 7);
        assert!(text.contains("# TYPE konnyaku_signaling_rooms gauge\nkonnyaku_signaling_rooms 2\n"));
        assert!(text.contains("konnyaku_signaling_peers 7\n"));
        assert!(text.contains("# TYPE konnyaku_signaling_joins_total counter\n"));
    }
}
