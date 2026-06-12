// SPDX-License-Identifier: MIT

//! Ephemeral Routing Id (ERID) — a rotating, key-derived wire address designed to
//! replace the stable, phone-derived UHID on the public wire.
//!
//! # The problem it solves
//! A node's UHID is `SHA-256(phone : deviceId : publicKey)` — stable for the life of
//! the install and carried in cleartext on every packet. A passive observer who never
//! breaks any encryption can therefore (a) follow any node indefinitely across time and
//! place, and (b) — because the value is phone-derived — attempt to confirm a suspected
//! phone number by recomputing the hash. That is a surveillance and targeting primitive,
//! independent of the fact that message contents are end-to-end encrypted.
//!
//! # The design
//! `ERID(epoch) = base32( HMAC-SHA256(routing_key, epoch) )[0..length]`
//! - `routing_key` is SECRET — derived from the node's identity secret via
//!   [`derive_routing_key`]. It is NEVER derived from the public key.
//! - `epoch = floor(unix_seconds / epoch_seconds)` — a 15-minute window by default.
//! - Two ERIDs from the same node in different epochs are cryptographically uncorrelated
//!   to an outside observer — no cross-time linkage, no phone recovery.
//!
//! The epoch is encoded big-endian (8-byte signed `i64`) so every language port produces
//! byte-identical input to the HMAC.

use hkdf::Hkdf;
use hmac::{Hmac, Mac};
use sha2::Sha256;

type HmacSha256 = Hmac<Sha256>;

/// Crockford base-32 alphabet (32 characters, no I/L/O/U) — same as [`super::AetherNetTag`].
const ALPHABET: &[u8; 32] = b"0123456789ABCDEFGHJKMNPQRSTVWXYZ";

/// HKDF domain-separation label. Must match the C# reference (and every other port) exactly.
const ROUTING_KEY_INFO: &[u8] = b"aether-erid-routing-key-v1";

/// Default rotation window: 15 minutes, expressed in seconds.
pub const DEFAULT_EPOCH_SECONDS: i64 = 900;

/// Default ERID length in base-32 characters (16 chars × 5 bits = 80 bits of entropy).
pub const DEFAULT_LENGTH: usize = 16;

/// Errors produced by the ERID primitive.
#[derive(Debug, Clone, PartialEq, Eq, thiserror::Error)]
pub enum EphemeralRoutingIdError {
    /// The identity secret supplied to [`derive_routing_key`] was empty.
    #[error("identity secret cannot be empty")]
    EmptySecret,

    /// The routing key supplied to [`derive_for_epoch`] was empty.
    #[error("routing key cannot be empty")]
    EmptyRoutingKey,

    /// `epoch_seconds` was not strictly positive.
    #[error("epoch_seconds must be positive")]
    InvalidEpochSeconds,

    /// `length` was outside the valid range `1..=51`.
    #[error("length must be 1..=51 (SHA-256 is 256 bits = 51 base-32 chars)")]
    InvalidLength,
}

/// Derive the 32-byte SECRET routing key from a node's identity secret (e.g. its
/// Ed25519 private-key bytes). Domain-separated via HKDF-SHA256 (RFC 5869, no salt).
/// MUST be fed a secret — never a public value, or the rotation schedule becomes
/// computable by anyone.
///
/// # Errors
/// Returns [`EphemeralRoutingIdError::EmptySecret`] if `identity_secret` is empty.
pub fn derive_routing_key(identity_secret: &[u8]) -> Result<[u8; 32], EphemeralRoutingIdError> {
    if identity_secret.is_empty() {
        return Err(EphemeralRoutingIdError::EmptySecret);
    }
    let hk = Hkdf::<Sha256>::new(None, identity_secret);
    let mut okm = [0u8; 32];
    // expand only fails if the requested length exceeds 255*HashLen; 32 never does.
    hk.expand(ROUTING_KEY_INFO, &mut okm)
        .expect("HKDF expand of 32 bytes never fails");
    Ok(okm)
}

/// The epoch (rotation-window index) that contains the given Unix time. Negative
/// `unix_seconds` clamp to 0.
///
/// # Errors
/// Returns [`EphemeralRoutingIdError::InvalidEpochSeconds`] if `epoch_seconds <= 0`.
pub fn epoch_for(unix_seconds: i64, epoch_seconds: i64) -> Result<i64, EphemeralRoutingIdError> {
    if epoch_seconds <= 0 {
        return Err(EphemeralRoutingIdError::InvalidEpochSeconds);
    }
    let u = if unix_seconds < 0 { 0 } else { unix_seconds };
    Ok(u / epoch_seconds)
}

/// Derive the ERID for the epoch that contains `unix_seconds`.
pub fn derive(
    routing_key: &[u8],
    unix_seconds: i64,
    epoch_seconds: i64,
    length: usize,
) -> Result<String, EphemeralRoutingIdError> {
    let epoch = epoch_for(unix_seconds, epoch_seconds)?;
    derive_for_epoch(routing_key, epoch, length)
}

/// Derive the ERID for an explicit epoch number. The epoch is encoded big-endian so
/// every language port produces byte-identical input to the HMAC.
///
/// # Errors
/// - [`EphemeralRoutingIdError::EmptyRoutingKey`] if `routing_key` is empty.
/// - [`EphemeralRoutingIdError::InvalidLength`] if `length` is outside `1..=51`.
pub fn derive_for_epoch(
    routing_key: &[u8],
    epoch: i64,
    length: usize,
) -> Result<String, EphemeralRoutingIdError> {
    if routing_key.is_empty() {
        return Err(EphemeralRoutingIdError::EmptyRoutingKey);
    }
    if !(1..=51).contains(&length) {
        return Err(EphemeralRoutingIdError::InvalidLength);
    }

    let epoch_bytes = epoch.to_be_bytes(); // 8-byte big-endian signed i64

    // hmac 0.13 moved `new_from_slice` to the `KeyInit` trait (from `digest`).
    use hmac::digest::KeyInit;
    let mut mac = <HmacSha256 as KeyInit>::new_from_slice(routing_key)
        .expect("HMAC accepts a key of any length");
    mac.update(&epoch_bytes);
    let tag = mac.finalize().into_bytes(); // 32 bytes

    Ok(base32(&tag, length))
}

/// Encode the first `length * 5` bits of `data` as Crockford base-32, most-significant
/// bit first.
fn base32(data: &[u8], length: usize) -> String {
    let mut out = Vec::with_capacity(length);
    let mut bit_pos = 0usize;
    for _ in 0..length {
        let byte_index = bit_pos >> 3;
        let bit_offset = bit_pos & 7;
        let hi = data[byte_index] as u32;
        let lo = if byte_index + 1 < data.len() {
            data[byte_index + 1] as u32
        } else {
            0
        };
        let window = (hi << 8) | lo;
        let val = ((window >> (11 - bit_offset)) & 0x1F) as usize;
        out.push(ALPHABET[val]);
        bit_pos += 5;
    }
    // Every byte pushed is an ASCII alnum from ALPHABET, so this is always valid UTF-8.
    String::from_utf8(out).expect("Crockford output is always ASCII")
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

#[cfg(test)]
mod tests {
    use super::*;

    /// GROUND TRUTH from the C# reference
    /// (src/AetherNet.Core/Identity/EphemeralRoutingId.cs). Every language port MUST
    /// reproduce these byte-for-byte. Do not edit without regenerating from C#.
    const ROUTING_KEY_VECTORS: &[(&str, &str)] = &[
        ("node-secret-A", "206f67e52afa8de0624fd3a2efc5bd68c65879ab623141811c996f0d416345e3"),
        ("node-B", "b071f5176536876b74a8927a242decea37aba390df06ec0019b711122c05384b"),
        ("n", "44874ed0e4e94dc12ea647a9460644feb1495f7dd348e583fcd3c5399388819a"),
    ];

    const ERID_VECTORS: &[(&str, i64, &str)] = &[
        ("node-secret-A", 0, "Q3AN7RWEGZBPZ5WM"),
        ("node-secret-A", 1, "N1HGBC2VC72W0A7E"),
        ("node-secret-A", 100, "KYF9JXYE3XJGFK26"),
        ("node-secret-A", 12345, "ZFM5AZMY6K0TGEK0"),
        ("node-secret-A", 1371, "N080TN3W537B27ZE"),
        ("node-B", 0, "61V5RVS7BVEBTV39"),
        ("node-B", 1, "6NQ731EA0HNGAN3C"),
        ("node-B", 100, "PDEMCT481QBWQN9P"),
        ("node-B", 12345, "H2D11G5JJY5EQ0PW"),
        ("node-B", 1371, "003WA1T3KDQVSDET"),
        ("n", 0, "GGY1T8FKNWCFXS71"),
        ("n", 1, "76AA5GEDFJ669RQS"),
        ("n", 100, "CFSM7DAP0Z1QT2KT"),
        ("n", 12345, "MJT2C0EYGYVRF4KN"),
        ("n", 1371, "39MYY8R0ZA292MPD"),
    ];

    fn hex(bytes: &[u8]) -> String {
        bytes.iter().map(|b| format!("{:02x}", b)).collect()
    }

    fn key(secret: &str) -> [u8; 32] {
        derive_routing_key(secret.as_bytes()).unwrap()
    }

    #[test]
    fn routing_key_matches_canonical_vectors() {
        for (secret, want) in ROUTING_KEY_VECTORS {
            assert_eq!(&hex(&key(secret)), want, "routing key for {secret}");
        }
    }

    #[test]
    fn erid_matches_canonical_vectors() {
        for (secret, epoch, want) in ERID_VECTORS {
            let got = derive_for_epoch(&key(secret), *epoch, DEFAULT_LENGTH).unwrap();
            assert_eq!(&got, want, "ERID for ({secret}, {epoch})");
        }
    }

    #[test]
    fn deterministic_for_same_key_and_epoch() {
        let k = key("node-secret-A");
        assert_eq!(
            derive_for_epoch(&k, 12345, DEFAULT_LENGTH).unwrap(),
            derive_for_epoch(&k, 12345, DEFAULT_LENGTH).unwrap()
        );
    }

    #[test]
    fn rotates_across_consecutive_epochs() {
        let k = key("node-secret-A");
        assert_ne!(
            derive_for_epoch(&k, 100, DEFAULT_LENGTH).unwrap(),
            derive_for_epoch(&k, 101, DEFAULT_LENGTH).unwrap()
        );
    }

    #[test]
    fn differs_by_node_in_same_epoch() {
        assert_ne!(
            derive_for_epoch(&key("node-A"), 7, DEFAULT_LENGTH).unwrap(),
            derive_for_epoch(&key("node-B"), 7, DEFAULT_LENGTH).unwrap()
        );
    }

    #[test]
    fn length_and_alphabet() {
        let id = derive_for_epoch(&key("n"), 1, DEFAULT_LENGTH).unwrap();
        assert_eq!(id.len(), DEFAULT_LENGTH);
        for b in id.bytes() {
            assert!(ALPHABET.contains(&b), "char {} not in alphabet", b as char);
        }
    }

    #[test]
    fn epoch_for_computes_window_index() {
        assert_eq!(epoch_for(0, 900).unwrap(), 0);
        assert_eq!(epoch_for(899, 900).unwrap(), 0);
        assert_eq!(epoch_for(900, 900).unwrap(), 1);
        assert_eq!(epoch_for(1800, 900).unwrap(), 2);
        assert_eq!(epoch_for(1234567, 900).unwrap(), 1371);
        assert_eq!(epoch_for(-50, 900).unwrap(), 0); // negative clamps to 0
        assert_eq!(epoch_for(1, 0), Err(EphemeralRoutingIdError::InvalidEpochSeconds));
    }

    #[test]
    fn stable_within_window_changes_at_boundary() {
        let k = key("n");
        assert_eq!(
            derive(&k, 1000, DEFAULT_EPOCH_SECONDS, DEFAULT_LENGTH).unwrap(),
            derive(&k, 1500, DEFAULT_EPOCH_SECONDS, DEFAULT_LENGTH).unwrap()
        );
        assert_ne!(
            derive(&k, 1000, DEFAULT_EPOCH_SECONDS, DEFAULT_LENGTH).unwrap(),
            derive(&k, 2000, DEFAULT_EPOCH_SECONDS, DEFAULT_LENGTH).unwrap()
        );
    }

    #[test]
    fn routing_key_is_deterministic_256bit_distinct_from_seed() {
        let seed = b"ed25519-private-key-material-seed";
        let k1 = derive_routing_key(seed).unwrap();
        let k2 = derive_routing_key(seed).unwrap();
        assert_eq!(k1, k2);
        assert_eq!(k1.len(), 32);
        assert_ne!(&k1[..], &seed[..]);
        assert_ne!(derive_routing_key(b"a-different-identity").unwrap(), k1);
    }

    #[test]
    fn rejects_empty_inputs() {
        assert_eq!(derive_routing_key(&[]), Err(EphemeralRoutingIdError::EmptySecret));
        assert_eq!(
            derive_for_epoch(&[], 1, DEFAULT_LENGTH),
            Err(EphemeralRoutingIdError::EmptyRoutingKey)
        );
    }
}
