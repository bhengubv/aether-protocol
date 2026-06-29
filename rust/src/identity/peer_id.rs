// SPDX-License-Identifier: MIT

//! PeerId — derives a libp2p **PeerID** from a node's Ed25519 public key, the bridge
//! between an AetherNet identity and the global libp2p relay / DHT used by the
//! decentralised relay layer.
//!
//! Because AetherNet and libp2p both key identity off the same Ed25519 public key, the
//! PeerID is a *pure, deterministic* function of that key — no lookup table, no network.
//! A node can compute its own PeerID (to announce on the libp2p DHT) and any peer's PeerID
//! (to dial it) from the public key alone.
//!
//! # Encoding (byte-identical across every SDK language)
//! 1. **protobuf PublicKey** = `08 01` (field 1 Type = Ed25519) `12 20` (field 2 Data,
//!    length 32) followed by the 32-byte key — 36 bytes total.
//! 2. **identity multihash** = `00` (identity hash code) `24` (length 36) followed by the
//!    protobuf — 38 bytes. libp2p uses the identity multihash for keys whose serialized
//!    form is ≤ 42 bytes, which Ed25519 always is.
//! 3. **PeerID string** = base58btc (Bitcoin alphabet) of the 38-byte multihash, WITHOUT a
//!    multibase prefix. Always renders as `12D3Koo…` for Ed25519.
//!
//! Verified byte-for-byte against real `js-libp2p` output; see `fixtures/peerid/`.

/// Bitcoin base58 alphabet (no 0, O, I, l).
const BASE58_ALPHABET: &[u8; 58] = b"123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

/// `ed25519_prefix` = identity-multihash(code 0x00, len 0x24 = 36) || protobuf PublicKey
/// (type Ed25519: `08 01`; data len 32: `12 20`).
const ED25519_PREFIX: [u8; 6] = [0x00, 0x24, 0x08, 0x01, 0x12, 0x20];

/// Byte length of a raw Ed25519 public key.
pub const ED25519_PUBLIC_KEY_LENGTH: usize = 32;

/// Errors produced by the PeerId primitive.
#[derive(Debug, Clone, PartialEq, Eq, thiserror::Error)]
pub enum PeerIdError {
    /// The supplied public key was not exactly [`ED25519_PUBLIC_KEY_LENGTH`] bytes.
    #[error("ed25519 public key must be {ED25519_PUBLIC_KEY_LENGTH} bytes, got {0}")]
    InvalidKeyLength(usize),
}

/// Returns the libp2p PeerID string (e.g. `12D3Koo…`) for a 32-byte Ed25519 public key.
///
/// # Errors
/// Returns [`PeerIdError::InvalidKeyLength`] if `public_key` is not exactly
/// [`ED25519_PUBLIC_KEY_LENGTH`] bytes.
pub fn from_ed25519_public_key(public_key: &[u8]) -> Result<String, PeerIdError> {
    if public_key.len() != ED25519_PUBLIC_KEY_LENGTH {
        return Err(PeerIdError::InvalidKeyLength(public_key.len()));
    }

    let mut multihash = Vec::with_capacity(ED25519_PREFIX.len() + ED25519_PUBLIC_KEY_LENGTH);
    multihash.extend_from_slice(&ED25519_PREFIX);
    multihash.extend_from_slice(public_key);

    Ok(base58_encode(&multihash))
}

/// Standard base58 (bitcoinj algorithm) — preserves leading zero bytes as leading '1's.
fn base58_encode(input: &[u8]) -> String {
    if input.is_empty() {
        return String::new();
    }

    // Count leading zero bytes — each becomes one leading '1'.
    let mut zeros = 0usize;
    while zeros < input.len() && input[zeros] == 0 {
        zeros += 1;
    }

    let mut buffer = input.to_vec(); // divmod mutates in place
    let mut encoded = vec![0u8; input.len() * 2]; // safe upper bound
    let mut output_start = encoded.len();

    let mut input_start = zeros;
    while input_start < buffer.len() {
        output_start -= 1;
        encoded[output_start] = BASE58_ALPHABET[divmod58(&mut buffer, input_start) as usize];
        if buffer[input_start] == 0 {
            input_start += 1; // a digit fully consumed
        }
    }

    // Drop extra leading '1's the loop may have produced.
    while output_start < encoded.len() && encoded[output_start] == BASE58_ALPHABET[0] {
        output_start += 1;
    }
    // Re-add one '1' per leading zero byte of the input.
    for _ in 0..zeros {
        output_start -= 1;
        encoded[output_start] = BASE58_ALPHABET[0];
    }

    // All bytes are ASCII from BASE58_ALPHABET, so this is valid UTF-8.
    String::from_utf8(encoded[output_start..].to_vec()).expect("base58 output is ASCII")
}

/// Divides the big-endian base-256 number in `number[first_digit..]` by 58, in place,
/// returning the remainder.
fn divmod58(number: &mut [u8], first_digit: usize) -> u8 {
    let mut remainder: u32 = 0;
    for digit in number.iter_mut().skip(first_digit) {
        let temp = remainder * 256 + *digit as u32;
        *digit = (temp / 58) as u8;
        remainder = temp % 58;
    }
    remainder as u8
}

#[cfg(test)]
mod tests {
    use super::*;

    /// The well-known PeerID for the all-zero Ed25519 seed's public key, verified against
    /// real js-libp2p (and the shared `fixtures/peerid/ed25519_1.txt` corpus).
    const ZERO_SEED_PUBKEY_HEX: &str =
        "3b6a27bcceb6a42d62a3a8d02a6f0d73653215771de243a63ac048a18b59da29";
    const ZERO_SEED_PEER_ID: &str = "12D3KooWDpJ7As7BWAwRMfu1VU2WCqNjvq387JEYKDBj4kx6nXTN";

    fn hex_decode(s: &str) -> Vec<u8> {
        (0..s.len())
            .step_by(2)
            .map(|i| u8::from_str_radix(&s[i..i + 2], 16).unwrap())
            .collect()
    }

    #[test]
    fn from_ed25519_public_key_matches_known_libp2p_peer_id() {
        let pubkey = hex_decode(ZERO_SEED_PUBKEY_HEX);
        let peer_id = from_ed25519_public_key(&pubkey).unwrap();
        assert_eq!(peer_id, ZERO_SEED_PEER_ID);
        assert!(peer_id.starts_with("12D3Koo"));
    }

    #[test]
    fn from_ed25519_public_key_rejects_31_byte_key() {
        assert_eq!(
            from_ed25519_public_key(&[0u8; 31]),
            Err(PeerIdError::InvalidKeyLength(31))
        );
    }

    #[test]
    fn from_ed25519_public_key_rejects_33_byte_key() {
        assert_eq!(
            from_ed25519_public_key(&[0u8; 33]),
            Err(PeerIdError::InvalidKeyLength(33))
        );
    }

    #[test]
    fn base58_encode_preserves_leading_zero_as_one() {
        // A single zero byte encodes to a single '1'.
        assert_eq!(base58_encode(&[0x00]), "1");
        // Leading zeros are preserved as leading '1's.
        assert_eq!(base58_encode(&[0x00, 0x00, 0x01]), "112");
    }
}
