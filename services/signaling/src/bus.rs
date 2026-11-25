//! Redis pub/sub → [`Hub`]. One pattern subscription (`user:*`) per replica, reconnected with backoff. After a
//! reconnection every socket gets [`RESYNC`](crate::hub::RESYNC): signals sent while Redis was unreachable are lost,
//! the browsers catch up through the API (their data is in PostgreSQL, never only in Redis).

use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::Duration;

use futures_util::StreamExt;

use crate::hub::{CHANNEL_PATTERN, Hub, RESYNC};
use crate::metrics;

/// Runs forever: subscribe, forward, and on any error wait (1 s doubling to 30 s) and subscribe again.
pub async fn run(url: String, hub: Arc<Hub>, connected: Arc<AtomicBool>) {
    let mut backoff = Duration::from_secs(1);
    let mut first = true;
    loop {
        match subscribe(&url).await {
            Ok(mut pubsub) => {
                connected.store(true, Ordering::Relaxed);
                backoff = Duration::from_secs(1);
                if !first {
                    let told = hub.broadcast(RESYNC);
                    tracing::info!(
                        connections = told,
                        "redis subscription restored; clients told to resync"
                    );
                } else {
                    tracing::info!(pattern = CHANNEL_PATTERN, "subscribed to redis");
                }
                first = false;
                let mut messages = pubsub.on_message();
                while let Some(message) = messages.next().await {
                    let Ok(payload) = message.get_payload::<String>() else {
                        continue;
                    };
                    metrics::inc(&metrics::SIGNALS);
                    hub.deliver(message.get_channel_name(), &payload);
                }
                connected.store(false, Ordering::Relaxed);
                tracing::warn!("redis subscription lost");
            }
            Err(error) => {
                connected.store(false, Ordering::Relaxed);
                tracing::warn!(%error, retry_in = ?backoff, "cannot subscribe to redis");
            }
        }
        tokio::time::sleep(backoff).await;
        backoff = (backoff * 2).min(Duration::from_secs(30));
    }
}

async fn subscribe(url: &str) -> redis::RedisResult<redis::aio::PubSub> {
    let client = redis::Client::open(url)?;
    let mut pubsub = client.get_async_pubsub().await?;
    pubsub.psubscribe(CHANNEL_PATTERN).await?;
    Ok(pubsub)
}
