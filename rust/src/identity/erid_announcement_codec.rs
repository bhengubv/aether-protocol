// SPDX-License-Identifier: MIT

//! Frames the in-session ERID announcement — the message a node sends a peer INSIDE an
//! established Signal session to share its secret routing key (plus the rotation
//! parameters it uses), so the peer can resolve its rotating wire address via
//! [`super::EridDirectory`].
//!
//! The bytes are carried *encrypted* by the Signal session, so this is framing only — no
//! encryption of its own. A 4-byte magic sentinel + version lets a receiver tell an ERID
//! announcement apart from other in-session application data before trying to parse it.
//!
//! Layout: magic `AERD` (4) + version (1) + `epoch_seconds` (i32 BE) + `erid_length`
//! (i32 BE) + `routing_key_len` (i32 BE) + `routing_key`. Integer fields are big-endian
//! so every language port frames byte-identically. Port of the C# reference
//! (`src/AetherNet.Core/Identity/EridAnnouncementCodec.cs`).

use super::ephemeral_routing_id::{DEFAULT_EPOCH_SECONDS, DEFAULT_LENGTH};

/// `'A' 'E' 'R' 'D'` — "AetherNet ERID Directory announcement".
const MAGIC: [u8; 4] = [0x41, 0x45, 0x52, 0x44];
const VERSION: u8 = 1;
/// magic(4) + version(1) + epoch_seconds(4) + erid_length(4) + routing_key_len(4).
const HEADER_LENGTH: usize = 17;

/// A decoded in-session ERID announcement.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct EridAnnouncement {
    /// The peer's secret routing key (used to derive its rotating ERID).
    pub routing_key: Vec<u8>,
    /// The rotation window the peer uses, in seconds.
    pub epoch_seconds: i32,
    /// The ERID length the peer uses, in base-32 characters.
    pub erid_length: i32,
}

/// Errors produced when framing an announcement with [`encode`].
#[derive(Debug, Clone, PartialEq, Eq, thiserror::Error)]
pub enum EridAnnouncementError {
    /// The routing key was empty.
    #[error("routing key cannot be empty")]
    EmptyRoutingKey,
    /// `epoch_seconds` was not strictly positive.
    #[error("epoch_seconds must be positive")]
    InvalidEpochSeconds,
    /// `erid_length` was outside `1..=51`.
    #[error("erid_length must be 1..=51")]
    InvalidLength,
}

/// Frame an announcement carrying `routing_key` and the rotation params.
///
/// # Errors
/// Returns an [`EridAnnouncementError`] if `routing_key` is empty, `epoch_seconds` is not
/// positive, or `erid_length` is outside `1..=51`.
pub fn encode(
    routing_key: &[u8],
    epoch_seconds: i32,
    erid_length: i32,
) -> Result<Vec<u8>, EridAnnouncementError> {
    if routing_key.is_empty() {
        return Err(EridAnnouncementError::EmptyRoutingKey);
    }
    if epoch_seconds <= 0 {
        return Err(EridAnnouncementError::InvalidEpochSeconds);
    }
    if !(1..=51).contains(&erid_length) {
        return Err(EridAnnouncementError::InvalidLength);
    }

    let mut buf = Vec::with_capacity(HEADER_LENGTH + routing_key.len());
    buf.extend_from_slice(&MAGIC);
    buf.push(VERSION);
    buf.extend_from_slice(&epoch_seconds.to_be_bytes());
    buf.extend_from_slice(&erid_length.to_be_bytes());
    buf.extend_from_slice(&(routing_key.len() as i32).to_be_bytes());
    buf.extend_from_slice(routing_key);
    Ok(buf)
}

/// Frame an announcement using the default rotation window and ERID length.
///
/// # Errors
/// Returns [`EridAnnouncementError::EmptyRoutingKey`] if `routing_key` is empty.
pub fn encode_default(routing_key: &[u8]) -> Result<Vec<u8>, EridAnnouncementError> {
    encode(routing_key, DEFAULT_EPOCH_SECONDS as i32, DEFAULT_LENGTH as i32)
}

/// Parse an announcement. Returns `None` (never an error) when the bytes are not a
/// well-formed ERID announcement, so a receiver can cheaply test an arbitrary decrypted
/// in-session payload against the magic.
#[must_use]
pub fn try_decode(data: &[u8]) -> Option<EridAnnouncement> {
    if data.len() < HEADER_LENGTH {
        return None;
    }
    if data[0..4] != MAGIC {
        return None;
    }
    if data[4] != VERSION {
        return None;
    }

    let epoch_seconds = i32::from_be_bytes([data[5], data[6], data[7], data[8]]);
    let erid_length = i32::from_be_bytes([data[9], data[10], data[11], data[12]]);
    let key_len = i32::from_be_bytes([data[13], data[14], data[15], data[16]]);

    if epoch_seconds <= 0 {
        return None;
    }
    if !(1..=51).contains(&erid_length) {
        return None;
    }
    if key_len <= 0 || HEADER_LENGTH + key_len as usize > data.len() {
        return None;
    }

    Some(EridAnnouncement {
        routing_key: data[HEADER_LENGTH..HEADER_LENGTH + key_len as usize].to_vec(),
        epoch_seconds,
        erid_length,
    })
}
