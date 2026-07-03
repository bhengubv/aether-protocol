// SPDX-License-Identifier: MIT

//! A signed device-membership record. A user links a new device by having their
//! long-term Ed25519 identity key sign the new device's own public key; every
//! other device verifies that signature to admit the newcomer into the "self"
//! device set — no central directory, no server. Because Ed25519 signatures are
//! deterministic, the serialized record is byte-identical across SDKs (verified
//! against `fixtures/sync/vectors.json`).
//!
//! Signed body layout (v1): `version(u8=1) · device_id(u16 len + utf8)
//! · device_public_key(32) · issued_at_ms(i64 LE)`. The serialized link is the
//! signed body followed by the 64-byte Ed25519 signature.

use std::fmt;

use crate::security::ed25519::Ed25519SigningService;

/// Wire format version; readers reject any other value.
pub const FORMAT_VERSION: u8 = 0x01;

/// A signed device-membership record.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DeviceLink {
    /// The linked device's identifier.
    pub device_id: String,
    /// The device's own 32-byte Ed25519 public key.
    pub device_public_key: [u8; 32],
    /// When the link was issued (Unix ms).
    pub issued_at_ms: i64,
    /// 64-byte Ed25519 signature by the user's identity key over the signed body.
    pub signature: [u8; 64],
}

/// A framing/validation error from [`create`], [`serialize`] or [`deserialize`].
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum DeviceLinkError {
    /// `device_id` was longer than `u16::MAX` bytes.
    DeviceIdTooLong,
    /// Signing failed (bad identity private key length).
    SignFailed,
    /// The buffer was shorter than the fixed minimum framing.
    TooShort,
    /// The version byte was not [`FORMAT_VERSION`].
    UnsupportedVersion(u8),
    /// A length prefix ran past the end of the buffer.
    Truncated,
    /// A UTF-8 field was not valid UTF-8.
    InvalidUtf8,
}

impl fmt::Display for DeviceLinkError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            DeviceLinkError::DeviceIdTooLong => write!(f, "DeviceId is too long (> u16::MAX bytes)"),
            DeviceLinkError::SignFailed => write!(f, "failed to sign DeviceLink body"),
            DeviceLinkError::TooShort => write!(f, "DeviceLink is too short"),
            DeviceLinkError::UnsupportedVersion(v) => {
                write!(f, "unsupported DeviceLink format version: {v}")
            }
            DeviceLinkError::Truncated => write!(f, "DeviceLink is truncated"),
            DeviceLinkError::InvalidUtf8 => write!(f, "DeviceLink device_id is not valid UTF-8"),
        }
    }
}

impl std::error::Error for DeviceLinkError {}

// Fixed minimum: version(1) + device_id len(2) + device_public_key(32)
// + issued_at_ms(8) + signature(64).
const MIN_LEN: usize = 1 + 2 + 32 + 8 + 64;

/// The canonical signed body (everything but the signature): `version ·
/// device_id(u16 len + utf8) · device_public_key(32) · issued_at_ms(i64 LE)`.
/// Signer and verifier operate over exactly these bytes.
pub fn signed_body(
    device_id: &str,
    device_public_key: &[u8; 32],
    issued_at_ms: i64,
) -> Result<Vec<u8>, DeviceLinkError> {
    let id = device_id.as_bytes();
    if id.len() > u16::MAX as usize {
        return Err(DeviceLinkError::DeviceIdTooLong);
    }

    let mut body = Vec::with_capacity(1 + 2 + id.len() + 32 + 8);
    body.push(FORMAT_VERSION);
    body.extend_from_slice(&(id.len() as u16).to_le_bytes());
    body.extend_from_slice(id);
    body.extend_from_slice(device_public_key);
    body.extend_from_slice(&issued_at_ms.to_le_bytes());
    Ok(body)
}

/// Creates a device-link signed by the user's 32-byte Ed25519 identity private key.
pub fn create(
    device_id: &str,
    device_public_key: [u8; 32],
    issued_at_ms: i64,
    identity_private_key: &[u8],
) -> Result<DeviceLink, DeviceLinkError> {
    let body = signed_body(device_id, &device_public_key, issued_at_ms)?;
    let sig_vec = Ed25519SigningService::sign(identity_private_key, &body)
        .map_err(|_| DeviceLinkError::SignFailed)?;
    let signature: [u8; 64] = sig_vec
        .as_slice()
        .try_into()
        .map_err(|_| DeviceLinkError::SignFailed)?;
    Ok(DeviceLink {
        device_id: device_id.to_string(),
        device_public_key,
        issued_at_ms,
        signature,
    })
}

/// True if `link` was signed by the identity behind `identity_public_key` —
/// i.e. this device belongs to that user.
pub fn verify(link: &DeviceLink, identity_public_key: &[u8]) -> bool {
    let Ok(body) = signed_body(&link.device_id, &link.device_public_key, link.issued_at_ms) else {
        return false;
    };
    Ed25519SigningService::verify(identity_public_key, &body, &link.signature)
}

/// Serializes a link as its signed body followed by the 64-byte signature.
pub fn serialize(link: &DeviceLink) -> Result<Vec<u8>, DeviceLinkError> {
    let mut body = signed_body(&link.device_id, &link.device_public_key, link.issued_at_ms)?;
    body.extend_from_slice(&link.signature);
    Ok(body)
}

/// Parses a serialized link, validating framing.
pub fn deserialize(data: &[u8]) -> Result<DeviceLink, DeviceLinkError> {
    if data.len() < MIN_LEN {
        return Err(DeviceLinkError::TooShort);
    }
    let mut o = 0usize;

    if data[o] != FORMAT_VERSION {
        return Err(DeviceLinkError::UnsupportedVersion(data[o]));
    }
    o += 1;

    let id_len = u16::from_le_bytes(data[o..o + 2].try_into().unwrap()) as usize;
    o += 2;
    // Everything after the id: public key(32) + issued_at(8) + signature(64).
    if o + id_len + 32 + 8 + 64 > data.len() {
        return Err(DeviceLinkError::Truncated);
    }
    let device_id =
        String::from_utf8(data[o..o + id_len].to_vec()).map_err(|_| DeviceLinkError::InvalidUtf8)?;
    o += id_len;

    let mut device_public_key = [0u8; 32];
    device_public_key.copy_from_slice(&data[o..o + 32]);
    o += 32;

    let issued_at_ms = i64::from_le_bytes(data[o..o + 8].try_into().unwrap());
    o += 8;

    let mut signature = [0u8; 64];
    signature.copy_from_slice(&data[o..o + 64]);

    Ok(DeviceLink {
        device_id,
        device_public_key,
        issued_at_ms,
        signature,
    })
}
