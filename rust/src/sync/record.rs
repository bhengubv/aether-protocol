// SPDX-License-Identifier: MIT

//! One state change to a synced item (a message, a read-marker, a deletion),
//! emitted by one of a user's devices and gossiped to that user's other devices
//! so they all converge on the same state — with no server.
//!
//! Binary wire format (v1) — byte-identical across every AetherNet SDK, verified
//! against `fixtures/sync/vectors.json`:
//!
//! ```text
//! version(u8=1) · record_id(16, RFC-4122 big-endian) · op(u8)
//! · logical_clock(i64 LE) · created_at_ms(i64 LE)
//! · device_id(u16 len + utf8) · item_id(u16 len + utf8)
//! · encrypted_payload(i32 len + bytes)
//! ```
//!
//! Every multi-byte integer is LITTLE-ENDIAN (`to_le_bytes`), except the 16-byte
//! record id which is stored/serialized big-endian (`Uuid::as_bytes`, mirroring
//! the DTN envelope and packet serializer). Strings are u16-length-prefixed
//! UTF-8; the opaque `encrypted_payload` is i32-length-prefixed and comes last.
//!
//! The `encrypted_payload` is already end-to-end encrypted to the user's device
//! set, so any node that relays the record (over the mesh or via DTN
//! store-and-forward) learns nothing about its content.

use std::fmt;

/// Wire format version; readers reject any other value.
pub const FORMAT_VERSION: u8 = 0x01;

/// The kind of state change a [`SyncRecord`] carries.
///
/// Wire values match the C# `SyncOp` enum exactly.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash)]
#[repr(u8)]
pub enum SyncOp {
    /// Create or update the item.
    Upsert = 0,
    /// Delete the item.
    Delete = 1,
    /// Mark the item read (read-state sync).
    Read = 2,
}

impl SyncOp {
    /// The wire byte for this op.
    pub fn as_u8(self) -> u8 {
        self as u8
    }

    /// Parses a wire byte into a [`SyncOp`], rejecting any value `> 2`.
    pub fn from_u8(v: u8) -> Result<Self, SyncRecordError> {
        match v {
            0 => Ok(SyncOp::Upsert),
            1 => Ok(SyncOp::Delete),
            2 => Ok(SyncOp::Read),
            other => Err(SyncRecordError::UnknownOp(other)),
        }
    }
}

/// One state change to a synced item.
///
/// `record_id` is the 16-byte RFC-4122 big-endian representation of the record's
/// globally-unique id (i.e. `Uuid::as_bytes`), matching the C# `Guid` written
/// with `bigEndian: true`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SyncRecord {
    /// Globally-unique id for this record (16 bytes, big-endian).
    pub record_id: [u8; 16],
    /// The device that produced the record.
    pub device_id: String,
    /// Create/update, delete, or read-marker.
    pub op: SyncOp,
    /// The item this record is about (the sync key).
    pub item_id: String,
    /// The device's monotonic counter at emit time.
    pub logical_clock: i64,
    /// Wall-clock time (Unix ms) the record was created.
    pub created_at_ms: i64,
    /// The E2E-encrypted item content (opaque; empty for a delete/read).
    pub encrypted_payload: Vec<u8>,
}

impl SyncRecord {
    /// Constructs a record from its parts.
    #[allow(clippy::too_many_arguments)]
    pub fn new(
        record_id: [u8; 16],
        device_id: impl Into<String>,
        op: SyncOp,
        item_id: impl Into<String>,
        logical_clock: i64,
        created_at_ms: i64,
        encrypted_payload: Vec<u8>,
    ) -> Self {
        Self {
            record_id,
            device_id: device_id.into(),
            op,
            item_id: item_id.into(),
            logical_clock,
            created_at_ms,
            encrypted_payload,
        }
    }
}

/// A framing/validation error from [`serialize`] or [`deserialize`].
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum SyncRecordError {
    /// A string was longer than `u16::MAX` bytes when serializing.
    StringTooLong,
    /// The buffer was shorter than the fixed minimum framing.
    TooShort,
    /// The version byte was not [`FORMAT_VERSION`].
    UnsupportedVersion(u8),
    /// The op byte was `> 2`.
    UnknownOp(u8),
    /// A length prefix ran past the end of the buffer.
    Truncated,
    /// The payload length prefix was negative or ran past the end of the buffer.
    InvalidPayloadLength,
}

impl fmt::Display for SyncRecordError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            SyncRecordError::StringTooLong => write!(f, "string is too long (> u16::MAX bytes)"),
            SyncRecordError::TooShort => write!(f, "SyncRecord is too short"),
            SyncRecordError::UnsupportedVersion(v) => {
                write!(f, "unsupported SyncRecord format version: {v}")
            }
            SyncRecordError::UnknownOp(v) => write!(f, "unknown SyncRecord op: {v}"),
            SyncRecordError::Truncated => write!(f, "SyncRecord string is truncated"),
            SyncRecordError::InvalidPayloadLength => {
                write!(f, "SyncRecord payload length is invalid")
            }
        }
    }
}

impl std::error::Error for SyncRecordError {}

// Fixed minimum: version(1) + record_id(16) + op(1) + logical_clock(8)
// + created_at_ms(8) + device_id len(2) + item_id len(2) + payload len(4).
const MIN_LEN: usize = 1 + 16 + 1 + 8 + 8 + 2 + 2 + 4;

/// Serializes a record to its canonical wire bytes.
pub fn serialize(record: &SyncRecord) -> Result<Vec<u8>, SyncRecordError> {
    let device = record.device_id.as_bytes();
    let item = record.item_id.as_bytes();
    if device.len() > u16::MAX as usize || item.len() > u16::MAX as usize {
        return Err(SyncRecordError::StringTooLong);
    }
    if record.encrypted_payload.len() > i32::MAX as usize {
        return Err(SyncRecordError::InvalidPayloadLength);
    }

    let mut out = Vec::with_capacity(
        1 + 16 + 1 + 8 + 8 + 2 + device.len() + 2 + item.len() + 4 + record.encrypted_payload.len(),
    );
    out.push(FORMAT_VERSION);
    out.extend_from_slice(&record.record_id); // 16 bytes, big-endian
    out.push(record.op.as_u8());
    out.extend_from_slice(&record.logical_clock.to_le_bytes());
    out.extend_from_slice(&record.created_at_ms.to_le_bytes());
    write_string(&mut out, device);
    write_string(&mut out, item);
    out.extend_from_slice(&(record.encrypted_payload.len() as i32).to_le_bytes());
    out.extend_from_slice(&record.encrypted_payload);
    Ok(out)
}

/// Parses canonical bytes back into a record, validating framing.
pub fn deserialize(data: &[u8]) -> Result<SyncRecord, SyncRecordError> {
    if data.len() < MIN_LEN {
        return Err(SyncRecordError::TooShort);
    }
    let mut o = 0usize;

    if data[o] != FORMAT_VERSION {
        return Err(SyncRecordError::UnsupportedVersion(data[o]));
    }
    o += 1;

    let mut record_id = [0u8; 16];
    record_id.copy_from_slice(&data[o..o + 16]);
    o += 16;

    let op = SyncOp::from_u8(data[o])?;
    o += 1;

    let logical_clock = i64::from_le_bytes(data[o..o + 8].try_into().unwrap());
    o += 8;
    let created_at_ms = i64::from_le_bytes(data[o..o + 8].try_into().unwrap());
    o += 8;

    let device_id = read_string(data, &mut o)?;
    let item_id = read_string(data, &mut o)?;

    if o + 4 > data.len() {
        return Err(SyncRecordError::InvalidPayloadLength);
    }
    let payload_len = i32::from_le_bytes(data[o..o + 4].try_into().unwrap());
    o += 4;
    if payload_len < 0 || o + payload_len as usize > data.len() {
        return Err(SyncRecordError::InvalidPayloadLength);
    }
    let encrypted_payload = data[o..o + payload_len as usize].to_vec();

    Ok(SyncRecord {
        record_id,
        device_id,
        op,
        item_id,
        logical_clock,
        created_at_ms,
        encrypted_payload,
    })
}

fn write_string(out: &mut Vec<u8>, utf8: &[u8]) {
    out.extend_from_slice(&(utf8.len() as u16).to_le_bytes());
    out.extend_from_slice(utf8);
}

fn read_string(data: &[u8], o: &mut usize) -> Result<String, SyncRecordError> {
    if *o + 2 > data.len() {
        return Err(SyncRecordError::Truncated);
    }
    let len = u16::from_le_bytes(data[*o..*o + 2].try_into().unwrap()) as usize;
    *o += 2;
    if *o + len > data.len() {
        return Err(SyncRecordError::Truncated);
    }
    let s = String::from_utf8(data[*o..*o + len].to_vec())
        .map_err(|_| SyncRecordError::Truncated)?;
    *o += len;
    Ok(s)
}
