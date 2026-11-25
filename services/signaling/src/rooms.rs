//! Rooms in memory: who is connected, seats, ending. Pure state, no I/O: every change returns what must be sent
//! to whom and what must be reported to the API, so the rules can be tested without sockets.

use std::collections::HashMap;

use tokio::sync::mpsc::UnboundedSender;
use uuid::Uuid;

use crate::protocol::{ErrorCode, MediaState, PeerInfo, ServerMessage};
use crate::report::RoomEvent;
use crate::ticket::{Role, Ticket};

/// What the connection task forwards to its WebSocket.
#[derive(Debug)]
pub enum Outbound {
    Message(ServerMessage),
    /// Send this message, then close the connection.
    Close(ServerMessage),
}

struct Peer {
    info: PeerInfo,
    tx: UnboundedSender<Outbound>,
}

struct Room {
    tenant_id: Uuid,
    session_id: Uuid,
    /// Planned end of the session (unix seconds), from the tickets.
    ends_at: i64,
    peers: Vec<Peer>,
}

impl Room {
    fn broadcast(&self, except: Uuid, message: &ServerMessage) {
        for peer in self.peers.iter().filter(|p| p.info.id != except) {
            let _ = peer.tx.send(Outbound::Message(message.clone()));
        }
    }

    fn event(&self, kind: &'static str, at: i64, peer: Option<&PeerInfo>) -> RoomEvent {
        RoomEvent {
            tenant_id: self.tenant_id,
            session_id: self.session_id,
            kind,
            at,
            connection_id: peer.map(|p| p.id),
            user_id: peer.map(|p| p.user_id),
            display_name: peer.map(|p| p.name.clone()),
            role: peer.map(|p| p.role),
        }
    }
}

pub struct Joined {
    pub peer_id: Uuid,
    pub events: Vec<RoomEvent>,
}

#[derive(Default)]
pub struct Rooms {
    rooms: HashMap<String, Room>,
    /// Rooms closed by the teacher or by time, with when: nobody may enter them again.
    ended: HashMap<String, i64>,
}

/// How long an ended room is remembered (tickets are refused by the API long before).
const ENDED_MEMORY_SECS: i64 = 24 * 3600;

impl Rooms {
    /// Seats: `capacity` people in all, but a learner never takes the last free seat, which is kept for the teacher.
    pub fn join(
        &mut self,
        ticket: &Ticket,
        state: MediaState,
        capacity: usize,
        now: i64,
        tx: UnboundedSender<Outbound>,
    ) -> Result<Joined, ErrorCode> {
        let key = ticket.room_key();
        if self.ended.contains_key(&key) {
            return Err(ErrorCode::RoomEnded);
        }

        let room = self.rooms.entry(key).or_insert_with(|| Room {
            tenant_id: ticket.tid,
            session_id: ticket.sid,
            ends_at: ticket.ends,
            peers: Vec::new(),
        });
        room.ends_at = room.ends_at.max(ticket.ends);

        let others = room.peers.iter().filter(|p| p.info.user_id != ticket.sub);
        let (people, learners) = others.fold((0, 0), |(n, l), p| {
            (n + 1, l + usize::from(p.info.role == Role::Learner))
        });
        let seat = match ticket.role {
            Role::Host => people < capacity,
            Role::Learner => people < capacity && learners < capacity - 1,
        };
        if !seat {
            return Err(ErrorCode::RoomFull);
        }

        let mut events = Vec::new();
        // The same person again (second tab, or a reconnect before the old socket timed out): the newest wins.
        if let Some(index) = room.peers.iter().position(|p| p.info.user_id == ticket.sub) {
            let old = room.peers.remove(index);
            let _ = old.tx.send(Outbound::Close(ServerMessage::Error {
                code: ErrorCode::Replaced,
            }));
            room.broadcast(old.info.id, &ServerMessage::PeerLeft { id: old.info.id });
            events.push(room.event("left", now, Some(&old.info)));
        }

        let info = PeerInfo {
            id: Uuid::new_v4(),
            user_id: ticket.sub,
            name: ticket.name.clone(),
            role: ticket.role,
            state,
        };
        let welcome = ServerMessage::Welcome {
            self_peer: info.clone(),
            peers: room.peers.iter().map(|p| p.info.clone()).collect(),
            capacity,
        };
        let _ = tx.send(Outbound::Message(welcome));
        room.broadcast(info.id, &ServerMessage::PeerJoined { peer: info.clone() });
        events.push(room.event("joined", now, Some(&info)));
        let peer_id = info.id;
        room.peers.push(Peer { info, tx });
        Ok(Joined { peer_id, events })
    }

    /// Forwards SDP/ICE to one peer of the same room.
    pub fn relay(&self, key: &str, from: Uuid, to: Uuid, data: serde_json::Value) {
        if let Some(room) = self.rooms.get(key)
            && room.peers.iter().any(|p| p.info.id == from)
            && let Some(target) = room.peers.iter().find(|p| p.info.id == to)
        {
            let _ = target.tx.send(Outbound::Message(ServerMessage::Signal { from, data }));
        }
    }

    pub fn set_state(&mut self, key: &str, id: Uuid, state: MediaState) {
        if let Some(room) = self.rooms.get_mut(key)
            && let Some(peer) = room.peers.iter_mut().find(|p| p.info.id == id)
        {
            peer.info.state = state;
            room.broadcast(id, &ServerMessage::State { id, state });
        }
    }

    /// The connection closed. Nothing happens if it was already replaced or the room ended.
    pub fn leave(&mut self, key: &str, id: Uuid, now: i64) -> Option<RoomEvent> {
        let room = self.rooms.get_mut(key)?;
        let index = room.peers.iter().position(|p| p.info.id == id)?;
        let peer = room.peers.remove(index);
        room.broadcast(id, &ServerMessage::PeerLeft { id });
        // An empty room stays until the session's end, so it can still be closed (and reported) on time.
        Some(room.event("left", now, Some(&peer.info)))
    }

    /// The teacher ends the room: everyone is told and disconnected, nobody can enter again.
    pub fn end(&mut self, key: &str, by: Uuid, now: i64) -> Result<RoomEvent, ErrorCode> {
        let room = self.rooms.get(key).ok_or(ErrorCode::NotAllowed)?;
        let host = room.peers.iter().any(|p| p.info.id == by && p.info.role == Role::Host);
        if !host {
            return Err(ErrorCode::NotAllowed);
        }
        Ok(self.close(key, now).expect("room exists"))
    }

    fn close(&mut self, key: &str, now: i64) -> Option<RoomEvent> {
        let room = self.rooms.remove(key)?;
        for peer in &room.peers {
            let _ = peer.tx.send(Outbound::Close(ServerMessage::Ended));
        }
        self.ended.insert(key.to_string(), now);
        Some(room.event("ended", now, None))
    }

    /// Closes rooms nobody closed: empty after the planned end, or still open `grace` seconds after it.
    pub fn sweep(&mut self, now: i64, grace: i64) -> Vec<RoomEvent> {
        let due: Vec<String> = self
            .rooms
            .iter()
            .filter(|(_, r)| (r.peers.is_empty() && now >= r.ends_at) || now >= r.ends_at + grace)
            .map(|(k, _)| k.clone())
            .collect();
        self.ended.retain(|_, at| now - *at < ENDED_MEMORY_SECS);
        due.iter().filter_map(|key| self.close(key, now)).collect()
    }

    pub fn room_count(&self) -> usize {
        self.rooms.len()
    }

    pub fn peer_count(&self) -> usize {
        self.rooms.values().map(|r| r.peers.len()).sum()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use tokio::sync::mpsc::{UnboundedReceiver, unbounded_channel};

    #[derive(Debug)]
    struct Client {
        rx: UnboundedReceiver<Outbound>,
        id: Uuid,
    }

    impl Client {
        fn drain(&mut self) -> Vec<Outbound> {
            let mut out = Vec::new();
            while let Ok(m) = self.rx.try_recv() {
                out.push(m);
            }
            out
        }
    }

    fn ticket(tid: Uuid, sid: Uuid, role: Role) -> Ticket {
        Ticket {
            sub: Uuid::new_v4(),
            tid,
            sid,
            name: "x".into(),
            role,
            ends: 1_000,
            exp: 0,
            iss: String::new(),
            aud: String::new(),
        }
    }

    fn join(rooms: &mut Rooms, t: &Ticket, capacity: usize) -> Result<Client, ErrorCode> {
        let (tx, rx) = unbounded_channel();
        let joined = rooms.join(t, MediaState::default(), capacity, 100, tx)?;
        Ok(Client { rx, id: joined.peer_id })
    }

    #[test]
    fn learners_leave_the_last_seat_to_the_teacher() {
        let (tid, sid) = (Uuid::new_v4(), Uuid::new_v4());
        let mut rooms = Rooms::default();
        join(&mut rooms, &ticket(tid, sid, Role::Learner), 3).unwrap();
        join(&mut rooms, &ticket(tid, sid, Role::Learner), 3).unwrap();
        assert_eq!(
            join(&mut rooms, &ticket(tid, sid, Role::Learner), 3).unwrap_err(),
            ErrorCode::RoomFull
        );
        join(&mut rooms, &ticket(tid, sid, Role::Host), 3).unwrap();
        assert_eq!(
            join(&mut rooms, &ticket(tid, sid, Role::Host), 3).unwrap_err(),
            ErrorCode::RoomFull
        );
        // Another session, another room.
        join(&mut rooms, &ticket(tid, Uuid::new_v4(), Role::Learner), 3).unwrap();
        assert_eq!(rooms.room_count(), 2);
    }

    #[test]
    fn joining_again_replaces_the_old_connection() {
        let (tid, sid) = (Uuid::new_v4(), Uuid::new_v4());
        let mut rooms = Rooms::default();
        let mut teacher = join(&mut rooms, &ticket(tid, sid, Role::Host), 2).unwrap();
        let learner_ticket = ticket(tid, sid, Role::Learner);
        let mut first = join(&mut rooms, &learner_ticket, 2).unwrap();
        // Full for anyone else, but the same learner may come back.
        let (tx, mut rx) = unbounded_channel();
        let joined = rooms.join(&learner_ticket, MediaState::default(), 2, 200, tx).unwrap();
        assert_eq!(
            joined.events.iter().map(|e| e.kind).collect::<Vec<_>>(),
            ["left", "joined"]
        );
        assert!(matches!(
            first.drain().last(),
            Some(Outbound::Close(ServerMessage::Error {
                code: ErrorCode::Replaced
            }))
        ));
        let to_teacher = teacher.drain();
        assert!(
            to_teacher
                .iter()
                .any(|m| matches!(m, Outbound::Message(ServerMessage::PeerLeft { id }) if *id == first.id))
        );
        match rx.try_recv().unwrap() {
            Outbound::Message(ServerMessage::Welcome { peers, .. }) => assert_eq!(peers.len(), 1),
            other => panic!("unexpected {other:?}"),
        }
        assert_eq!(rooms.peer_count(), 2);
        // The replaced socket closing later changes nothing.
        assert!(rooms.leave(&learner_ticket.room_key(), first.id, 300).is_none());
    }

    #[test]
    fn only_a_host_ends_the_room_and_nobody_enters_again() {
        let (tid, sid) = (Uuid::new_v4(), Uuid::new_v4());
        let mut rooms = Rooms::default();
        let host_ticket = ticket(tid, sid, Role::Host);
        let mut host = join(&mut rooms, &host_ticket, 6).unwrap();
        let mut learner = join(&mut rooms, &ticket(tid, sid, Role::Learner), 6).unwrap();
        let key = host_ticket.room_key();
        assert_eq!(rooms.end(&key, learner.id, 500).unwrap_err(), ErrorCode::NotAllowed);
        let ended = rooms.end(&key, host.id, 500).unwrap();
        assert_eq!((ended.kind, ended.session_id, ended.at), ("ended", sid, 500));
        assert!(matches!(
            learner.drain().last(),
            Some(Outbound::Close(ServerMessage::Ended))
        ));
        assert!(matches!(
            host.drain().last(),
            Some(Outbound::Close(ServerMessage::Ended))
        ));
        assert_eq!(join(&mut rooms, &host_ticket, 6).unwrap_err(), ErrorCode::RoomEnded);
    }

    #[test]
    fn relays_only_inside_a_room() {
        let (tid, sid) = (Uuid::new_v4(), Uuid::new_v4());
        let mut rooms = Rooms::default();
        let t = ticket(tid, sid, Role::Host);
        let a = join(&mut rooms, &t, 6).unwrap();
        let mut b = join(&mut rooms, &ticket(tid, sid, Role::Learner), 6).unwrap();
        let mut stranger = join(&mut rooms, &ticket(tid, Uuid::new_v4(), Role::Learner), 6).unwrap();
        b.drain();
        rooms.relay(&t.room_key(), a.id, b.id, serde_json::json!({"sdp": "offer"}));
        rooms.relay(&t.room_key(), a.id, stranger.id, serde_json::json!({"sdp": "offer"}));
        assert!(matches!(&b.drain()[..], [Outbound::Message(ServerMessage::Signal { from, .. })] if *from == a.id));
        assert!(
            stranger
                .drain()
                .iter()
                .all(|m| !matches!(m, Outbound::Message(ServerMessage::Signal { .. })))
        );
    }

    #[test]
    fn sweep_closes_empty_rooms_after_the_end_and_full_ones_after_the_grace() {
        let mut rooms = Rooms::default();
        let empty = ticket(Uuid::new_v4(), Uuid::new_v4(), Role::Learner);
        let busy = ticket(Uuid::new_v4(), Uuid::new_v4(), Role::Host);
        let gone = join(&mut rooms, &empty, 6).unwrap();
        rooms.leave(&empty.room_key(), gone.id, 500).unwrap();
        let _stays = join(&mut rooms, &busy, 6).unwrap();
        assert!(rooms.sweep(999, 600).is_empty(), "before the end nothing closes");
        let closed = rooms.sweep(1_000, 600);
        assert_eq!(closed.iter().map(|e| e.session_id).collect::<Vec<_>>(), [empty.sid]);
        assert_eq!(
            rooms.sweep(1_600, 600).iter().map(|e| e.session_id).collect::<Vec<_>>(),
            [busy.sid]
        );
        assert_eq!(rooms.room_count(), 0);
    }
}
