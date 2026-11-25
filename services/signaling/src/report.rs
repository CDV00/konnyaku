//! Presence reports to the API (`POST /api/workspace/internal/rooms/events`): joined, left, ended. They become the
//! participant log of the session (attendance, timesheets). Sent one by one, in order, retried until the API answers.

use std::time::{Duration, UNIX_EPOCH};

use serde::Serialize;
use tokio::sync::mpsc::{UnboundedReceiver, UnboundedSender, unbounded_channel};
use uuid::Uuid;

use crate::ticket::{Role, service_token, unix_now};

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct RoomEvent {
    pub tenant_id: Uuid,
    pub session_id: Uuid,
    pub kind: &'static str,
    /// Unix seconds.
    pub at: i64,
    pub connection_id: Option<Uuid>,
    pub user_id: Option<Uuid>,
    pub display_name: Option<String>,
    pub role: Option<Role>,
}

/// Body of the report (`RoomEventCommand` in the Workspace module).
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Body<'a> {
    tenant_id: Uuid,
    session_id: Uuid,
    kind: &'a str,
    at: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    connection_id: Option<Uuid>,
    #[serde(skip_serializing_if = "Option::is_none")]
    user_id: Option<Uuid>,
    #[serde(skip_serializing_if = "Option::is_none")]
    display_name: Option<&'a str>,
    #[serde(skip_serializing_if = "Option::is_none")]
    role: Option<Role>,
}

fn rfc3339(unix: i64) -> String {
    humantime::format_rfc3339_seconds(UNIX_EPOCH + Duration::from_secs(unix.max(0) as u64)).to_string()
}

#[derive(Clone)]
pub struct Reporter {
    tx: UnboundedSender<RoomEvent>,
}

impl Reporter {
    pub fn send(&self, event: RoomEvent) {
        let _ = self.tx.send(event);
    }

    /// Without an API (tests, local tools) events are handed to the caller instead.
    pub fn channel() -> (Self, UnboundedReceiver<RoomEvent>) {
        let (tx, rx) = unbounded_channel();
        (Self { tx }, rx)
    }

    pub fn start(api_url: Option<String>, secret: String) -> Self {
        let (reporter, mut rx) = Self::channel();
        tokio::spawn(async move {
            let Some(api_url) = api_url else {
                while let Some(event) = rx.recv().await {
                    tracing::info!(?event, "room event (API_URL not set, not reported)");
                }
                return;
            };
            let client = reqwest::Client::builder()
                .timeout(Duration::from_secs(10))
                .build()
                .expect("http client");
            let url = format!("{api_url}/api/workspace/internal/rooms/events");
            while let Some(event) = rx.recv().await {
                deliver(&client, &url, &secret, &event).await;
            }
        });
        reporter
    }
}

/// Retries network errors and 5xx with backoff (up to ~10 minutes); 4xx means the API refuses it for good.
async fn deliver(client: &reqwest::Client, url: &str, secret: &str, event: &RoomEvent) {
    let body = Body {
        tenant_id: event.tenant_id,
        session_id: event.session_id,
        kind: event.kind,
        at: rfc3339(event.at),
        connection_id: event.connection_id,
        user_id: event.user_id,
        display_name: event.display_name.as_deref(),
        role: event.role,
    };
    let mut delay = Duration::from_secs(1);
    for attempt in 1..=12 {
        let result = client
            .post(url)
            .header("X-Rtc-Token", service_token(secret, unix_now()))
            .json(&body)
            .send()
            .await;
        match result {
            Ok(response) if response.status().is_success() => return,
            Ok(response) if response.status().is_client_error() => {
                tracing::warn!(status = %response.status(), kind = event.kind, session = %event.session_id, "room event refused by the API");
                return;
            }
            Ok(response) => tracing::warn!(status = %response.status(), attempt, "room event not delivered, retrying"),
            Err(error) => tracing::warn!(%error, attempt, "room event not delivered, retrying"),
        }
        tokio::time::sleep(delay).await;
        delay = (delay * 2).min(Duration::from_secs(120));
    }
    tracing::error!(kind = event.kind, session = %event.session_id, "room event dropped after retries");
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn formats_times_and_body_for_the_api() {
        assert_eq!(rfc3339(0), "1970-01-01T00:00:00Z");
        let body = Body {
            tenant_id: Uuid::nil(),
            session_id: Uuid::nil(),
            kind: "ended",
            at: rfc3339(86_400),
            connection_id: None,
            user_id: None,
            display_name: None,
            role: Some(Role::Host),
        };
        let json = serde_json::to_string(&body).unwrap();
        assert!(json.contains(r#""kind":"ended","at":"1970-01-02T00:00:00Z""#), "{json}");
        assert!(
            json.contains(r#""role":"host""#) && !json.contains("connectionId"),
            "{json}"
        );
    }
}
