// SPDX-License-Identifier: MIT

//! Native circuit-relay-v2 wire frame — the decentralised any-node relay that lets a
//! node reach a peer it cannot contact directly by routing through a third node
//! reachable to both. This is the Rust side of the cross-language protocol; conventions
//! mirror [`crate::dtn::envelope`] exactly: a format-version byte first, little-endian
//! integers, uint16-LE length-prefixed UTF-8 strings, the 16-byte connection id as a
//! UUID in RFC-4122 big-endian order ([`Uuid::as_bytes`], never the legacy .NET
//! mixed-endian layout), and an int32-LE length-prefixed payload last.
//!
//! Byte-identical to the C# reference (`AetherNet.CircuitRelay.RelayFrameSerializer`) and
//! the Go oracle (`go/circuitrelay`), pinned by `fixtures/circuit-relay`.
//!
//! Layout — fixed, every field always present, in order:
//! ```text
//! version u8 | type u8 | status u8
//! srcUhid u16+utf8 | dstUhid u16+utf8 | relayUhid u16+utf8
//! connId 16B(BE) | reservationExpiresAtMs i64 | limitDurationSeconds i32 | limitDataBytes i64
//! payload i32+bytes
//! ```
//! Minimum size (all strings empty, no payload): 49 bytes.

use std::fmt;

use uuid::Uuid;

mod transport;
pub use transport::*;

mod mesh_link;
pub use mesh_link::*;

mod transport_service;
pub use transport_service::*;

/// Format-version byte at offset 0 of every relay frame.
pub const VERSION: u8 = 0x01;

const MAX_PAYLOAD: usize = 16 * 1024 * 1024; // AETHERNET_MAX_PAYLOAD_LEN

/// The circuit-relay-v2 verb a [`RelayFrame`] carries.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(u8)]
pub enum MessageType {
    /// Client → relay: request a reservation (permission to be relayed to).
    Reserve = 1,
    /// Relay → client: reservation grant/refusal + limits.
    ReserveResponse = 2,
    /// Client → relay: bridge me to `destination_uhid`.
    Connect = 3,
    /// Relay → target: client `source_uhid` wants to reach you.
    Stop = 4,
    /// Target → relay: accept/reject the inbound bridge.
    StopResponse = 5,
    /// Relay → client: bridge established/refused.
    ConnectResponse = 6,
    /// Either endpoint → relay → other endpoint: opaque tunnelled payload.
    Data = 7,
}

impl MessageType {
    /// The wire byte for this verb.
    pub fn as_u8(self) -> u8 {
        self as u8
    }

    /// Parses a wire byte into a verb, rejecting `0` and anything above [`MessageType::Data`].
    pub fn from_u8(v: u8) -> Option<Self> {
        match v {
            1 => Some(MessageType::Reserve),
            2 => Some(MessageType::ReserveResponse),
            3 => Some(MessageType::Connect),
            4 => Some(MessageType::Stop),
            5 => Some(MessageType::StopResponse),
            6 => Some(MessageType::ConnectResponse),
            7 => Some(MessageType::Data),
            _ => None,
        }
    }
}

/// Result code carried by a relay response frame.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(u8)]
pub enum Status {
    /// Success (reservation granted / bridge established / no error).
    Ok = 0,
    /// Relay declined to reserve capacity for the client.
    ReservationRefused = 1,
    /// Connect attempted without a valid reservation.
    NoReservation = 2,
    /// The bridge's data or duration budget was exhausted.
    ResourceLimitExceeded = 3,
    /// Policy denied the reservation or connection.
    PermissionDenied = 4,
    /// Relay could not reach / was refused by the target.
    ConnectionFailed = 5,
    /// A received frame was malformed.
    MalformedMessage = 6,
}

impl Status {
    /// The wire byte for this status.
    pub fn as_u8(self) -> u8 {
        self as u8
    }

    /// Parses a wire byte into a status, rejecting anything above [`Status::MalformedMessage`].
    pub fn from_u8(v: u8) -> Option<Self> {
        match v {
            0 => Some(Status::Ok),
            1 => Some(Status::ReservationRefused),
            2 => Some(Status::NoReservation),
            3 => Some(Status::ResourceLimitExceeded),
            4 => Some(Status::PermissionDenied),
            5 => Some(Status::ConnectionFailed),
            6 => Some(Status::MalformedMessage),
            _ => None,
        }
    }
}

/// A single circuit-relay-v2 wire frame. One fixed layout carries every verb
/// (type-discriminated) so the format stays trivially byte-identical across every
/// language SDK. It rides in `MeshPacket.payload` the same way the DTN envelope does.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RelayFrame {
    /// Which verb this frame carries.
    pub message_type: MessageType,
    /// Result code (meaningful on the `*Response` frames; [`Status::Ok`] otherwise).
    pub status: Status,
    /// UHID of the originating client (A).
    pub source_uhid: String,
    /// UHID of the final target (B).
    pub destination_uhid: String,
    /// UHID of the relay node (R). May be empty on client→relay requests.
    pub relay_uhid: String,
    /// Correlation id for a bridge session, shared by all frames of that session.
    /// [`Uuid::nil`] when not applicable.
    pub connection_id: Uuid,
    /// Reservation expiry as Unix ms. 0 when not applicable.
    pub reservation_expires_at_ms: i64,
    /// Bridge duration budget in seconds. 0 = unlimited.
    pub limit_duration_seconds: i32,
    /// Bridge data budget in bytes. 0 = unlimited.
    pub limit_data_bytes: i64,
    /// Tunnelled payload ([`MessageType::Data`] only; empty otherwise).
    pub payload: Vec<u8>,
}

/// Error returned when a byte buffer cannot be decoded into a [`RelayFrame`],
/// or a frame cannot be encoded.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum RelayError {
    /// Buffer ended before a required field was fully read.
    UnexpectedEof,
    /// The format-version byte was not [`VERSION`].
    UnsupportedVersion(u8),
    /// The message-type byte was `0` or greater than [`MessageType::Data`].
    InvalidMessageType(u8),
    /// The status byte was greater than [`Status::MalformedMessage`].
    InvalidStatus(u8),
    /// A string field's bytes were not valid UTF-8.
    InvalidUtf8,
    /// The payload length prefix was negative or exceeded [`MAX_PAYLOAD`].
    InvalidPayloadLength(i32),
    /// A string field's UTF-8 length exceeded 65535 bytes (serialize only).
    StringTooLong(usize),
    /// The payload exceeded [`MAX_PAYLOAD`] bytes (serialize only).
    PayloadTooLarge(usize),
}

impl fmt::Display for RelayError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            RelayError::UnexpectedEof => write!(f, "relay: unexpected end of buffer"),
            RelayError::UnsupportedVersion(v) => {
                write!(f, "relay: unsupported frame version 0x{v:02x}")
            }
            RelayError::InvalidMessageType(t) => write!(f, "relay: invalid message type {t}"),
            RelayError::InvalidStatus(s) => write!(f, "relay: invalid status {s}"),
            RelayError::InvalidUtf8 => write!(f, "relay: invalid utf-8 in string field"),
            RelayError::InvalidPayloadLength(n) => write!(f, "relay: invalid payload length {n}"),
            RelayError::StringTooLong(n) => {
                write!(f, "relay: string too long ({n} bytes exceeds 65535)")
            }
            RelayError::PayloadTooLarge(n) => {
                write!(f, "relay: payload too large ({n} bytes exceeds {MAX_PAYLOAD})")
            }
        }
    }
}

impl std::error::Error for RelayError {}

// ─────────────────────────────── serialize ───────────────────────────────

/// Encodes a [`RelayFrame`] to its binary wire form.
///
/// Errors only when a string field exceeds 65535 UTF-8 bytes
/// ([`RelayError::StringTooLong`]) or the payload exceeds 16 MiB
/// ([`RelayError::PayloadTooLarge`]).
pub fn serialize(f: &RelayFrame) -> Result<Vec<u8>, RelayError> {
    let mut out = Vec::with_capacity(48 + f.payload.len());
    out.push(VERSION);
    out.push(f.message_type.as_u8());
    out.push(f.status.as_u8());
    write_str(&mut out, &f.source_uhid)?;
    write_str(&mut out, &f.destination_uhid)?;
    write_str(&mut out, &f.relay_uhid)?;
    out.extend_from_slice(f.connection_id.as_bytes()); // 16 bytes, RFC-4122 big-endian
    write_i64(&mut out, f.reservation_expires_at_ms);
    write_i32(&mut out, f.limit_duration_seconds);
    write_i64(&mut out, f.limit_data_bytes);
    write_bytes32(&mut out, &f.payload)?;
    Ok(out)
}

/// Decodes a [`RelayFrame`] from its binary wire form.
///
/// Rejects (returns `Err`): a version byte other than [`VERSION`]; a message type
/// `< 1` or `> 7`; a status `> 6`; a negative payload length; and a payload length
/// greater than 16 MiB. Also errors on a truncated buffer or invalid UTF-8.
pub fn deserialize(data: &[u8]) -> Result<RelayFrame, RelayError> {
    let mut r = Reader::new(data);
    r.version()?;

    let type_byte = r.u8()?;
    let message_type = MessageType::from_u8(type_byte).ok_or(RelayError::InvalidMessageType(type_byte))?;

    let status_byte = r.u8()?;
    let status = Status::from_u8(status_byte).ok_or(RelayError::InvalidStatus(status_byte))?;

    let source_uhid = r.string()?;
    let destination_uhid = r.string()?;
    let relay_uhid = r.string()?;
    let connection_id = r.uuid()?;
    let reservation_expires_at_ms = r.i64()?;
    let limit_duration_seconds = r.i32()?;
    let limit_data_bytes = r.i64()?;
    let payload = r.bytes32()?;

    Ok(RelayFrame {
        message_type,
        status,
        source_uhid,
        destination_uhid,
        relay_uhid,
        connection_id,
        reservation_expires_at_ms,
        limit_duration_seconds,
        limit_data_bytes,
        payload,
    })
}

// ─────────────────────────────── primitives ───────────────────────────────
// (identical idiom to crate::dtn::envelope)

fn write_i32(out: &mut Vec<u8>, v: i32) {
    out.extend_from_slice(&v.to_le_bytes());
}

fn write_i64(out: &mut Vec<u8>, v: i64) {
    out.extend_from_slice(&v.to_le_bytes());
}

fn write_str(out: &mut Vec<u8>, s: &str) -> Result<(), RelayError> {
    let bytes = s.as_bytes();
    if bytes.len() > u16::MAX as usize {
        return Err(RelayError::StringTooLong(bytes.len()));
    }
    out.extend_from_slice(&(bytes.len() as u16).to_le_bytes());
    out.extend_from_slice(bytes);
    Ok(())
}

fn write_bytes32(out: &mut Vec<u8>, b: &[u8]) -> Result<(), RelayError> {
    if b.len() > MAX_PAYLOAD {
        return Err(RelayError::PayloadTooLarge(b.len()));
    }
    out.extend_from_slice(&(b.len() as i32).to_le_bytes());
    out.extend_from_slice(b);
    Ok(())
}

struct Reader<'a> {
    data: &'a [u8],
    pos: usize,
}

impl<'a> Reader<'a> {
    fn new(data: &'a [u8]) -> Self {
        Self { data, pos: 0 }
    }

    fn version(&mut self) -> Result<(), RelayError> {
        let v = self.u8()?;
        if v == VERSION {
            Ok(())
        } else {
            Err(RelayError::UnsupportedVersion(v))
        }
    }

    fn u8(&mut self) -> Result<u8, RelayError> {
        let b = *self.data.get(self.pos).ok_or(RelayError::UnexpectedEof)?;
        self.pos += 1;
        Ok(b)
    }

    fn take(&mut self, n: usize) -> Result<&'a [u8], RelayError> {
        let end = self.pos.checked_add(n).ok_or(RelayError::UnexpectedEof)?;
        let slice = self.data.get(self.pos..end).ok_or(RelayError::UnexpectedEof)?;
        self.pos = end;
        Ok(slice)
    }

    fn uuid(&mut self) -> Result<Uuid, RelayError> {
        let slice = self.take(16)?;
        let mut arr = [0u8; 16];
        arr.copy_from_slice(slice);
        Ok(Uuid::from_bytes(arr))
    }

    fn i32(&mut self) -> Result<i32, RelayError> {
        let slice = self.take(4)?;
        Ok(i32::from_le_bytes(slice.try_into().map_err(|_| RelayError::UnexpectedEof)?))
    }

    fn i64(&mut self) -> Result<i64, RelayError> {
        let slice = self.take(8)?;
        Ok(i64::from_le_bytes(slice.try_into().map_err(|_| RelayError::UnexpectedEof)?))
    }

    fn u16(&mut self) -> Result<u16, RelayError> {
        let slice = self.take(2)?;
        Ok(u16::from_le_bytes(slice.try_into().map_err(|_| RelayError::UnexpectedEof)?))
    }

    fn string(&mut self) -> Result<String, RelayError> {
        let n = self.u16()? as usize;
        if n == 0 {
            return Ok(String::new());
        }
        let slice = self.take(n)?;
        String::from_utf8(slice.to_vec()).map_err(|_| RelayError::InvalidUtf8)
    }

    fn bytes32(&mut self) -> Result<Vec<u8>, RelayError> {
        let n = self.i32()?;
        if n < 0 || n as usize > MAX_PAYLOAD {
            return Err(RelayError::InvalidPayloadLength(n));
        }
        let slice = self.take(n as usize)?;
        Ok(slice.to_vec())
    }
}
