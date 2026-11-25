//! End-to-end over real WebSockets: tickets, seats, relaying, ending, and what is reported to the API.

use std::time::Duration;

use futures_util::{SinkExt, StreamExt};
use konnyaku_signaling::report::RoomEvent;
use konnyaku_signaling::ticket::{self, Role, TICKET_AUDIENCE, TICKET_ISSUER, Ticket, unix_now};
use konnyaku_signaling::{Config, Reporter, app, server};
use serde_json::{Value, json};
use tokio::net::TcpStream;
use tokio::sync::mpsc::UnboundedReceiver;
use tokio_tungstenite::tungstenite::Message;
use tokio_tungstenite::{MaybeTlsStream, WebSocketStream, connect_async};
use uuid::Uuid;

const SECRET: &str = "integration-secret-0123456789abcdef-0123";

type Ws = WebSocketStream<MaybeTlsStream<TcpStream>>;

struct Server {
    url: String,
    events: UnboundedReceiver<RoomEvent>,
}

async fn start(capacity: usize) -> Server {
    let (reporter, events) = Reporter::channel();
    let (router, state) = app(Config::for_tests(SECRET, capacity), reporter);
    tokio::spawn(server::sweeper(state));
    let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
    let addr = listener.local_addr().unwrap();
    tokio::spawn(async move { axum::serve(listener, router).await.unwrap() });
    Server {
        url: format!("ws://{addr}/rtc/ws"),
        events,
    }
}

fn ticket_for(tenant: Uuid, session: Uuid, role: Role, ends: i64) -> Ticket {
    Ticket {
        sub: Uuid::new_v4(),
        tid: tenant,
        sid: session,
        name: format!("{role:?}"),
        role,
        ends,
        exp: unix_now() + 120,
        iss: TICKET_ISSUER.into(),
        aud: TICKET_AUDIENCE.into(),
    }
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

async fn connect(server: &Server, t: &Ticket) -> (Ws, Value) {
    let (mut ws, _) = connect_async(&server.url).await.unwrap();
    let token = ticket::issue(SECRET, t);
    ws.send(Message::Text(
        json!({"type": "join", "ticket": token, "state": {"audio": true, "video": true}})
            .to_string()
            .into(),
    ))
    .await
    .unwrap();
    let first = recv(&mut ws).await;
    (ws, first)
}

async fn send(ws: &mut Ws, value: Value) {
    ws.send(Message::Text(value.to_string().into())).await.unwrap();
}

async fn next_event(server: &mut Server) -> RoomEvent {
    tokio::time::timeout(Duration::from_secs(5), server.events.recv())
        .await
        .expect("event in time")
        .unwrap()
}

#[tokio::test]
async fn two_people_meet_exchange_signals_and_the_teacher_ends_the_room() {
    let mut server = start(6).await;
    let (tenant, session) = (Uuid::new_v4(), Uuid::new_v4());
    let teacher = ticket_for(tenant, session, Role::Host, unix_now() + 3600);
    let learner = ticket_for(tenant, session, Role::Learner, unix_now() + 3600);

    let (mut t, welcome) = connect(&server, &teacher).await;
    assert_eq!(welcome["type"], "welcome");
    assert_eq!(welcome["peers"].as_array().unwrap().len(), 0);
    assert_eq!(welcome["capacity"], 6);
    let teacher_id = welcome["self"]["id"].as_str().unwrap().to_string();

    let (mut l, welcome) = connect(&server, &learner).await;
    let learner_id = welcome["self"]["id"].as_str().unwrap().to_string();
    assert_eq!(welcome["peers"][0]["id"], teacher_id.as_str());
    assert_eq!(welcome["peers"][0]["role"], "host");
    assert_eq!(welcome["peers"][0]["state"]["video"], true);

    let joined = recv(&mut t).await;
    assert_eq!(
        (joined["type"].as_str(), joined["peer"]["id"].as_str()),
        (Some("peer-joined"), Some(learner_id.as_str()))
    );

    // The newcomer offers; the answer comes back.
    send(
        &mut l,
        json!({"type": "signal", "to": teacher_id, "data": {"description": {"type": "offer", "sdp": "v=0"}}}),
    )
    .await;
    let offer = recv(&mut t).await;
    assert_eq!(
        (offer["type"].as_str(), offer["from"].as_str()),
        (Some("signal"), Some(learner_id.as_str()))
    );
    assert_eq!(offer["data"]["description"]["type"], "offer");
    send(
        &mut t,
        json!({"type": "signal", "to": learner_id, "data": {"description": {"type": "answer", "sdp": "v=0"}}}),
    )
    .await;
    assert_eq!(recv(&mut l).await["data"]["description"]["type"], "answer");

    // Microphone off is seen by the others.
    send(&mut l, json!({"type": "state", "audio": false, "video": true})).await;
    let state = recv(&mut t).await;
    assert_eq!(
        (state["type"].as_str(), state["state"]["audio"].as_bool()),
        (Some("state"), Some(false))
    );

    // A learner cannot end the room; the teacher can.
    send(&mut l, json!({"type": "end"})).await;
    assert_eq!(recv(&mut l).await["code"], "not_allowed");
    send(&mut t, json!({"type": "end"})).await;
    assert_eq!(recv(&mut t).await["type"], "ended");
    assert_eq!(recv(&mut l).await["type"], "ended");

    let kinds: Vec<_> = [
        next_event(&mut server).await,
        next_event(&mut server).await,
        next_event(&mut server).await,
    ]
    .iter()
    .map(|e| (e.kind, e.session_id, e.user_id))
    .collect();
    assert_eq!(
        kinds,
        [
            ("joined", session, Some(teacher.sub)),
            ("joined", session, Some(learner.sub)),
            ("ended", session, None)
        ]
    );

    // Nobody enters an ended room.
    let (_, refused) = connect(&server, &ticket_for(tenant, session, Role::Host, unix_now() + 3600)).await;
    assert_eq!(refused["code"], "room_ended");
}

#[tokio::test]
async fn a_full_room_refuses_learners_but_keeps_a_seat_for_the_teacher() {
    let server = start(3).await;
    let (tenant, session) = (Uuid::new_v4(), Uuid::new_v4());
    let ends = unix_now() + 3600;
    let (_a, _) = connect(&server, &ticket_for(tenant, session, Role::Learner, ends)).await;
    let (_b, _) = connect(&server, &ticket_for(tenant, session, Role::Learner, ends)).await;
    let (_c, refused) = connect(&server, &ticket_for(tenant, session, Role::Learner, ends)).await;
    assert_eq!(
        (refused["type"].as_str(), refused["code"].as_str()),
        (Some("error"), Some("room_full"))
    );
    let (_t, welcome) = connect(&server, &ticket_for(tenant, session, Role::Host, ends)).await;
    assert_eq!(welcome["type"], "welcome");
}

#[tokio::test]
async fn bad_or_expired_tickets_are_refused() {
    let server = start(6).await;
    let mut expired = ticket_for(Uuid::new_v4(), Uuid::new_v4(), Role::Host, unix_now() + 3600);
    expired.exp = unix_now() - 300;
    assert_eq!(connect(&server, &expired).await.1["code"], "ticket_expired");

    let (mut ws, _) = connect_async(&server.url).await.unwrap();
    send(&mut ws, json!({"type": "join", "ticket": "forged.token.value"})).await;
    assert_eq!(recv(&mut ws).await["code"], "invalid_ticket");
}

#[tokio::test]
async fn reconnecting_replaces_the_old_connection_and_reports_both() {
    let mut server = start(6).await;
    let learner = ticket_for(Uuid::new_v4(), Uuid::new_v4(), Role::Learner, unix_now() + 3600);
    let (mut first, _) = connect(&server, &learner).await;
    let (_second, welcome) = connect(&server, &learner).await;
    assert_eq!(welcome["type"], "welcome");
    assert_eq!(recv(&mut first).await["code"], "replaced");
    let kinds: Vec<_> = [
        next_event(&mut server).await,
        next_event(&mut server).await,
        next_event(&mut server).await,
    ]
    .iter()
    .map(|e| e.kind)
    .collect();
    assert_eq!(kinds, ["joined", "left", "joined"]);
}

#[tokio::test]
async fn an_empty_room_closes_by_itself_after_the_session_end() {
    let mut server = start(6).await;
    let session = Uuid::new_v4();
    // The session is already over: once the last person leaves, the sweeper closes the room.
    let (mut ws, _) = connect(
        &server,
        &ticket_for(Uuid::new_v4(), session, Role::Learner, unix_now() - 1),
    )
    .await;
    send(&mut ws, json!({"type": "leave"})).await;
    assert_eq!(next_event(&mut server).await.kind, "joined");
    assert_eq!(next_event(&mut server).await.kind, "left");
    let ended = next_event(&mut server).await;
    assert_eq!((ended.kind, ended.session_id), ("ended", session));
}
