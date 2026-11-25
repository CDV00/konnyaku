//! JSON messages on the WebSocket (one object per text frame, `type` tells which).

use serde::{Deserialize, Serialize};
use uuid::Uuid;

use crate::ticket::Role;

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct MediaState {
    #[serde(default)]
    pub audio: bool,
    #[serde(default)]
    pub video: bool,
    #[serde(default)]
    pub screen: bool,
}

#[derive(Clone, Debug, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct PeerInfo {
    pub id: Uuid,
    pub user_id: Uuid,
    pub name: String,
    pub role: Role,
    pub state: MediaState,
}

#[derive(Debug, Deserialize)]
#[serde(tag = "type", rename_all = "kebab-case")]
pub enum ClientMessage {
    /// First message of a connection.
    Join {
        ticket: String,
        #[serde(default)]
        state: MediaState,
    },
    /// SDP or ICE candidate for one peer; `data` is relayed untouched.
    Signal {
        to: Uuid,
        data: serde_json::Value,
    },
    /// Microphone / camera / screen sharing switched on or off.
    State(MediaState),
    /// Teacher closes the room for everyone.
    End,
    Leave,
}

/// Error codes; the room page translates them (`room.errors.<code>`).
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum ErrorCode {
    InvalidTicket,
    TicketExpired,
    RoomFull,
    RoomEnded,
    /// The same person opened the room again elsewhere (another tab, or a reconnect).
    Replaced,
    NotAllowed,
    BadMessage,
}

impl ErrorCode {
    /// Errors after which the server closes the connection.
    pub fn is_fatal(self) -> bool {
        !matches!(self, ErrorCode::NotAllowed | ErrorCode::BadMessage)
    }
}

#[derive(Clone, Debug, Serialize)]
#[serde(tag = "type", rename_all = "kebab-case")]
pub enum ServerMessage {
    #[serde(rename_all = "camelCase")]
    Welcome {
        #[serde(rename = "self")]
        self_peer: PeerInfo,
        peers: Vec<PeerInfo>,
        capacity: usize,
    },
    PeerJoined {
        peer: PeerInfo,
    },
    PeerLeft {
        id: Uuid,
    },
    Signal {
        from: Uuid,
        data: serde_json::Value,
    },
    State {
        id: Uuid,
        state: MediaState,
    },
    Ended,
    Error {
        code: ErrorCode,
    },
}

impl ServerMessage {
    pub fn to_json(&self) -> String {
        serde_json::to_string(self).expect("server messages serialize")
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_client_messages() {
        let join: ClientMessage = serde_json::from_str(r#"{"type":"join","ticket":"t"}"#).unwrap();
        assert!(
            matches!(join, ClientMessage::Join { ref ticket, state } if ticket == "t" && state == MediaState::default())
        );
        let id = Uuid::new_v4();
        let signal: ClientMessage =
            serde_json::from_str(&format!(r#"{{"type":"signal","to":"{id}","data":{{"sdp":"x"}}}}"#)).unwrap();
        assert!(matches!(signal, ClientMessage::Signal { to, .. } if to == id));
        let state: ClientMessage = serde_json::from_str(r#"{"type":"state","audio":true,"video":false}"#).unwrap();
        assert!(matches!(
            state,
            ClientMessage::State(MediaState {
                audio: true,
                video: false,
                screen: false
            })
        ));
        assert!(serde_json::from_str::<ClientMessage>(r#"{"type":"nope"}"#).is_err());
    }

    #[test]
    fn serializes_server_messages() {
        let json = ServerMessage::Error {
            code: ErrorCode::RoomFull,
        }
        .to_json();
        assert_eq!(json, r#"{"type":"error","code":"room_full"}"#);
        assert_eq!(ServerMessage::Ended.to_json(), r#"{"type":"ended"}"#);
    }
}
