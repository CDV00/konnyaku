//! The realtime socket (`/rt`) over real WebSockets: tickets, routing of signals to the right user, limits, and (when
//! `REDIS_URL` is set, as in CI) the whole path from a Redis PUBLISH to the browser.

use std::sync::Arc;
use std::sync::atomic::Ordering;
use std::time::Duration;

use futures_util::{SinkExt, StreamExt};
use konnyaku_signaling::server::Shared;
use konnyaku_signaling::ticket::{self, REALTIME_AUDIENCE, TICKET_AUDIENCE, TICKET_ISSUER, UserTicket, unix_now};
use konnyaku_signaling::{Config, Reporter, app, bus};
use serde_json::{Value, json};
use tokio::net::TcpStream;
use tokio_tungstenite::tungstenite::Message;
use tokio_tungstenite::{MaybeTlsStream, WebSocketStream, connect_async};
use uuid::Uuid;

const SECRET: &str = "integration-secret-0123456789abcdef-0123";

type Ws = WebSocketStream<MaybeTlsStream<TcpStream>>;

async fn start(config: Config) -> (String, Shared) {
    let (reporter, _events) = Reporter::channel();
    let (router, state) = app(config, reporter);
    let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
    let addr = listener.local_addr().unwrap();
    tokio::spawn(async move { axum::serve(listener, router).await.unwrap() });
    (format!("ws://{addr}/rt"), state)
}

fn ticket_for(user: Uuid, audience: &str, exp: i64) -> String {
    ticket::issue(
        SECRET,
        &UserTicket {
            sub: user,
            tid: Uuid::new_v4(),
            exp,
            iss: TICKET_ISSUER.into(),
            aud: audience.into(),
        },
    )
}

async fn recv(ws: &mut Ws) -> Value {
    loop {
        let message = tokio::time::timeout(Duration::from_secs(5), ws.next())
            .await
            .expect("message in time")
            .expect("open")
            .unwrap();
        if let Message::Text(text) = message {
            return serde_json::from_str(&text).unwrap();
        }
    }
}

async fn connect(url: &str, token: &str) -> (Ws, Value) {
    let (mut ws, _) = connect_async(url).await.unwrap();
    ws.send(Message::Text(
        json!({"type": "auth", "ticket": token}).to_string().into(),
    ))
    .await
    .unwrap();
    let first = recv(&mut ws).await;
    (ws, first)
}

async fn wait_for(what: &str, check: impl Fn() -> bool) {
    for _ in 0..100 {
        if check() {
            return;
        }
        tokio::time::sleep(Duration::from_millis(50)).await;
    }
    panic!("timed out waiting for {what}");
}

#[tokio::test]
async fn signals_reach_every_tab_of_their_user_only() {
    let (url, state) = start(Config::for_tests(SECRET, 6)).await;
    let (alice, bob) = (Uuid::new_v4(), Uuid::new_v4());
    let (mut tab1, ready) = connect(&url, &ticket_for(alice, REALTIME_AUDIENCE, unix_now() + 60)).await;
    assert_eq!(ready["t"], "ready");
    let (mut tab2, _) = connect(&url, &ticket_for(alice, REALTIME_AUDIENCE, unix_now() + 60)).await;
    let (mut other, _) = connect(&url, &ticket_for(bob, REALTIME_AUDIENCE, unix_now() + 60)).await;
    wait_for("three sockets", || state.hub.connection_count() == 3).await;

    let signal = json!({"t": "inbox", "id": Uuid::new_v4(), "seq": 42}).to_string();
    assert_eq!(state.hub.deliver(&format!("user:{alice}"), &signal), 2);
    assert_eq!(recv(&mut tab1).await["seq"], 42);
    assert_eq!(recv(&mut tab2).await["seq"], 42);

    state.hub.deliver(&format!("user:{bob}"), r#"{"t":"inbox.read"}"#);
    assert_eq!(recv(&mut other).await["t"], "inbox.read");

    tab1.close(None).await.unwrap();
    wait_for("the closed tab to be forgotten", || state.hub.connection_count() == 2).await;
}

#[tokio::test]
async fn refuses_bad_expired_and_classroom_tickets_and_too_many_tabs() {
    let (url, _state) = start(Config::for_tests(SECRET, 6)).await;
    let user = Uuid::new_v4();

    let (_, error) = connect(&url, "not-a-ticket").await;
    assert_eq!(error, json!({"t": "error", "code": "invalid_ticket"}));
    let (_, error) = connect(&url, &ticket_for(user, REALTIME_AUDIENCE, unix_now() - 120)).await;
    assert_eq!(error["code"], "ticket_expired");
    let (_, error) = connect(&url, &ticket_for(user, TICKET_AUDIENCE, unix_now() + 60)).await;
    assert_eq!(error["code"], "invalid_ticket");

    let (mut ws, _) = connect_async(&url).await.unwrap();
    ws.send(Message::Text(r#"{"type":"join"}"#.into())).await.unwrap();
    assert_eq!(recv(&mut ws).await["code"], "bad_message");

    // Config::for_tests allows 3 sockets per user.
    let mut tabs = Vec::new();
    for _ in 0..3 {
        let (tab, ready) = connect(&url, &ticket_for(user, REALTIME_AUDIENCE, unix_now() + 60)).await;
        assert_eq!(ready["t"], "ready");
        tabs.push(tab);
    }
    let (_, error) = connect(&url, &ticket_for(user, REALTIME_AUDIENCE, unix_now() + 60)).await;
    assert_eq!(error["code"], "too_many_connections");
}

#[tokio::test]
async fn closes_with_reconnect_at_the_end_of_the_lifetime() {
    let mut config = Config::for_tests(SECRET, 6);
    config.connection_lifetime = Duration::from_millis(300);
    let (url, state) = start(config).await;
    let (mut ws, _) = connect(&url, &ticket_for(Uuid::new_v4(), REALTIME_AUDIENCE, unix_now() + 60)).await;
    assert_eq!(recv(&mut ws).await["t"], "reconnect");
    wait_for("the socket to be released", || state.hub.connection_count() == 0).await;
}

/// Needs a Redis server: `REDIS_URL=redis://127.0.0.1:6379 cargo test` (CI runs one). Skipped otherwise.
#[tokio::test]
async fn a_redis_publish_reaches_the_socket_of_the_user() {
    let Ok(redis_url) = std::env::var("REDIS_URL") else {
        eprintln!("REDIS_URL not set: skipping the Redis test");
        return;
    };
    let (url, state) = start(Config::for_tests(SECRET, 6)).await;
    tokio::spawn(bus::run(redis_url.clone(), state.hub.clone(), state.redis_up.clone()));
    let up = Arc::clone(&state.redis_up);
    wait_for("the redis subscription", move || up.load(Ordering::Relaxed)).await;

    let user = Uuid::new_v4();
    let (mut ws, _) = connect(&url, &ticket_for(user, REALTIME_AUDIENCE, unix_now() + 60)).await;
    wait_for("the socket", || state.hub.connection_count() == 1).await;

    let client = redis::Client::open(redis_url).unwrap();
    let mut connection = client.get_multiplexed_async_connection().await.unwrap();
    let signal = json!({"t": "inbox", "id": Uuid::new_v4(), "seq": 7}).to_string();
    let _: i64 = redis::cmd("PUBLISH")
        .arg(format!("user:{user}"))
        .arg(&signal)
        .query_async(&mut connection)
        .await
        .unwrap();
    assert_eq!(recv(&mut ws).await["seq"], 7);
}
