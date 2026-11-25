//! Tickets issued by the API when someone passes the "join class" permission check, and the tokens this service
//! attaches to its reports back to the API. Both are HS256 JWTs signed with the shared secret.

use jsonwebtoken::{Algorithm, DecodingKey, EncodingKey, Header, Validation, decode, encode};
use serde::{Deserialize, Serialize};
use uuid::Uuid;

pub const TICKET_ISSUER: &str = "konnyaku";
pub const TICKET_AUDIENCE: &str = "konnyaku-rtc";
pub const SERVICE_ISSUER: &str = "konnyaku-rtc";
pub const SERVICE_AUDIENCE: &str = "konnyaku-api";

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum Role {
    /// Teacher of the session or a coordinator: may end the room, always has a seat.
    Host,
    Learner,
}

/// Claims of a room ticket (see `RoomTickets.Issue` in the Workspace module).
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct Ticket {
    pub sub: Uuid,
    pub tid: Uuid,
    pub sid: Uuid,
    pub name: String,
    pub role: Role,
    /// Planned end of the session (unix seconds).
    pub ends: i64,
    pub exp: i64,
    #[serde(default)]
    pub iss: String,
    #[serde(default)]
    pub aud: String,
}

impl Ticket {
    /// One room per session of a tenant.
    pub fn room_key(&self) -> String {
        format!("{}:{}", self.tid, self.sid)
    }
}

#[derive(Debug, PartialEq, Eq)]
pub enum TicketError {
    Invalid,
    Expired,
}

pub fn verify(secret: &str, token: &str) -> Result<Ticket, TicketError> {
    let mut validation = Validation::new(Algorithm::HS256);
    validation.set_issuer(&[TICKET_ISSUER]);
    validation.set_audience(&[TICKET_AUDIENCE]);
    validation.set_required_spec_claims(&["exp", "iss", "aud", "sub"]);
    validation.leeway = 30;
    decode::<Ticket>(token, &DecodingKey::from_secret(secret.as_bytes()), &validation)
        .map(|data| data.claims)
        .map_err(|e| match e.kind() {
            jsonwebtoken::errors::ErrorKind::ExpiredSignature => TicketError::Expired,
            _ => TicketError::Invalid,
        })
}

#[derive(Serialize)]
struct ServiceClaims<'a> {
    iss: &'a str,
    aud: &'a str,
    iat: i64,
    exp: i64,
}

/// Token for the `X-Rtc-Token` header of reports to the API, valid one minute.
pub fn service_token(secret: &str, now: i64) -> String {
    let claims = ServiceClaims {
        iss: SERVICE_ISSUER,
        aud: SERVICE_AUDIENCE,
        iat: now,
        exp: now + 60,
    };
    encode(
        &Header::new(Algorithm::HS256),
        &claims,
        &EncodingKey::from_secret(secret.as_bytes()),
    )
    .expect("HS256 signing does not fail")
}

/// Signs a ticket like the API does. Used by tests and local tools.
pub fn issue(secret: &str, ticket: &Ticket) -> String {
    encode(
        &Header::new(Algorithm::HS256),
        ticket,
        &EncodingKey::from_secret(secret.as_bytes()),
    )
    .expect("HS256 signing does not fail")
}

pub fn unix_now() -> i64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_secs() as i64)
        .unwrap_or_default()
}

#[cfg(test)]
mod tests {
    use super::*;

    const SECRET: &str = "test-secret-0123456789abcdef-0123456789";

    fn ticket(exp: i64) -> Ticket {
        Ticket {
            sub: Uuid::new_v4(),
            tid: Uuid::new_v4(),
            sid: Uuid::new_v4(),
            name: "GV".into(),
            role: Role::Host,
            ends: unix_now() + 3600,
            exp,
            iss: TICKET_ISSUER.into(),
            aud: TICKET_AUDIENCE.into(),
        }
    }

    #[test]
    fn accepts_a_ticket_signed_with_the_shared_secret() {
        let t = ticket(unix_now() + 60);
        let verified = verify(SECRET, &issue(SECRET, &t)).unwrap();
        assert_eq!(verified.sub, t.sub);
        assert_eq!(verified.role, Role::Host);
        assert_eq!(verified.room_key(), format!("{}:{}", t.tid, t.sid));
    }

    #[test]
    fn rejects_other_secret_expired_and_wrong_audience() {
        let t = ticket(unix_now() + 60);
        assert_eq!(
            verify("another-secret-0123456789abcdef-012345", &issue(SECRET, &t)).unwrap_err(),
            TicketError::Invalid
        );
        assert_eq!(
            verify(SECRET, &issue(SECRET, &ticket(unix_now() - 120))).unwrap_err(),
            TicketError::Expired
        );
        let mut wrong = ticket(unix_now() + 60);
        wrong.aud = SERVICE_AUDIENCE.into();
        assert_eq!(
            verify(SECRET, &issue(SECRET, &wrong)).unwrap_err(),
            TicketError::Invalid
        );
        assert_eq!(verify(SECRET, "not-a-jwt").unwrap_err(), TicketError::Invalid);
    }

    #[test]
    fn service_tokens_are_not_tickets() {
        let token = service_token(SECRET, unix_now());
        assert_eq!(verify(SECRET, &token).unwrap_err(), TicketError::Invalid);
    }
}
