// SPDX-License-Identifier: MIT
//! Bluetooth-LE tracking protection: a rotating Service UUID and IRK-based
//! Resolvable Private Addresses (RPA), so a mesh node is discoverable by its
//! peers without exposing a stable, trackable Bluetooth fingerprint on the air.
//!
//! - The Service UUID rotates every 15 minutes, HMAC-SHA256-derived from a
//!   shared rotation key and the current time window. Every node in the same
//!   window derives the same UUID, so peers still find each other — but a
//!   passive scanner sees an identifier that changes and cannot be linked over
//!   time.
//! - The node's stable id is removed from the advertisement; a peer that holds
//!   the node's 128-bit Identity Resolving Key (IRK) resolves its rotating
//!   6-byte RPA instead (the BLE "ah" function).
//!
//! The window-based operations are deterministic and byte-identical across every
//! AetherNet SDK (verified against `fixtures/bleprivacy/vectors.json`). The time
//! window is encoded as a little-endian `i64`, matching the C# reference
//! `BlePrivacy` (`BinaryPrimitives.WriteInt64LittleEndian`).

use aes::Aes128;
use cipher::{BlockCipherEncrypt, KeyInit};
use hmac::{Hmac, Mac};
use sha2::Sha256;

type HmacSha256 = Hmac<Sha256>;

/// Rotation period in seconds (15 minutes).
pub const ROTATION_SECONDS: i64 = 900;

/// The rotation window index for a Unix-seconds timestamp.
pub fn window_for(unix_seconds: i64) -> i64 {
    unix_seconds / ROTATION_SECONDS
}

/// The window encoded as the 8-byte little-endian `i64` HMAC input — identical
/// across every port.
fn window_bytes(window: i64) -> [u8; 8] {
    window.to_le_bytes()
}

/// HMAC-SHA256(key, le64(window)) -> 32-byte tag.
fn hmac_window(key: &[u8], window: i64) -> [u8; 32] {
    // hmac 0.13 moved `new_from_slice` to the `KeyInit` trait (from `digest`).
    use hmac::digest::KeyInit;
    let mut mac = <HmacSha256 as KeyInit>::new_from_slice(key)
        .expect("HMAC accepts a key of any length");
    mac.update(&window_bytes(window));
    mac.finalize().into_bytes().into()
}

/// The rotating BLE Service UUID for a rotation key and time window. Every node
/// sharing the rotation key derives the same UUID within the window, enabling
/// mutual discovery with no static identifier on the air.
///
/// `mac = HMAC-SHA256(rotation_key, le64(window))`; the first 16 bytes are
/// formatted as a lowercase canonical UUID
/// `xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx` (bytes 0-3, 4-5, 6-7, 8-9, 10-15).
pub fn service_uuid(rotation_key: &[u8], window: i64) -> String {
    let mac = hmac_window(rotation_key, window);
    format_uuid(&mac[0..16])
}

/// A 6-byte Resolvable Private Address for a 16-byte IRK and time window:
/// `hash(3) || prand(3)`, where `prand` is HMAC-derived (with the RPA
/// address-type bits set) and `hash = AES-128(IRK, prand-block)`. Rotates every
/// window; only a peer holding the IRK can link successive addresses.
///
/// Returns `None` if `irk` is not exactly 16 bytes (mirrors the C# reference,
/// which throws on a bad IRK length).
pub fn resolvable_address(irk: &[u8], window: i64) -> Option<[u8; 6]> {
    if irk.len() != 16 {
        return None;
    }

    let tag = hmac_window(irk, window);
    let mut prand = [tag[0], tag[1], tag[2]];
    prand[0] = (prand[0] & 0x3F) | 0x40; // RPA address-type bits (0b01)

    let hash = ah(irk, &prand);

    let mut rpa = [0u8; 6];
    rpa[0..3].copy_from_slice(&hash);
    rpa[3..6].copy_from_slice(&prand);
    Some(rpa)
}

/// `true` if `rpa` was generated from `irk` — i.e. this node recognises the peer
/// behind the rotating address. Returns `false` for any wrong length
/// (`irk != 16` or `rpa != 6`) or a hash mismatch, never panicking.
pub fn resolve_address(irk: &[u8], rpa: &[u8]) -> bool {
    if irk.len() != 16 || rpa.len() != 6 {
        return false;
    }

    let prand = [rpa[3], rpa[4], rpa[5]];
    let hash = ah(irk, &prand);
    hash[..] == rpa[0..3]
}

/// BLE "ah" hash: `AES-128-ECB(irk, 0^13 || prand)`, keep the first 3 bytes.
///
/// A single 16-byte block, ECB, no padding. `irk` is assumed to be 16 bytes
/// (callers check); a wrong length would panic in `Aes128::new_from_slice`, but
/// every public entry point validates the IRK length before reaching here.
fn ah(irk: &[u8], prand: &[u8; 3]) -> [u8; 3] {
    let mut block = [0u8; 16];
    block[13..16].copy_from_slice(prand);

    let cipher = Aes128::new_from_slice(irk).expect("IRK must be 16 bytes for AES-128");
    cipher.encrypt_block((&mut block).into());

    [block[0], block[1], block[2]]
}

/// Lowercase canonical UUID from 16 bytes: `xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx`
/// grouping bytes 0-3, 4-5, 6-7, 8-9, 10-15.
fn format_uuid(b: &[u8]) -> String {
    debug_assert_eq!(b.len(), 16);
    let mut s = String::with_capacity(36);
    for (i, byte) in b.iter().enumerate() {
        if i == 4 || i == 6 || i == 8 || i == 10 {
            s.push('-');
        }
        s.push_str(&format!("{:02x}", byte));
    }
    s
}
