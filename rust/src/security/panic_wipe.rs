// SPDX-License-Identifier: MIT
//! Panic-wipe: the identity-erasure core of an AetherNet node's duress defence.
//!
//! A duress PIN (or panic button) irreversibly destroys the node's key material,
//! so a seized device reveals nothing and looks like a fresh install.
//!
//! This module is the protocol-level core — deterministic and portable across
//! every AetherNet SDK, byte-identical to the C# `PanicWipe` reference and the
//! shared `fixtures/panicwipe/vectors.json`:
//!
//! - [`duress_pin_hash`] / [`verify_duress_pin`] — recognise the duress PIN
//!   (SHA-256, constant-time compare); the PIN itself is never stored.
//! - [`secure_erase`] — best-effort in-memory erase of key material (overwrite
//!   with random, then zero).
//! - [`IDENTITY_KEY_NAMES`] + [`pre_key_name`] / [`signed_pre_key_name`] — the
//!   canonical set of key-store entries a wipe must destroy.
//!
//! Destroying the hosting app's local database, platform keychain entries and any
//! decoy store is the app's job — it owns that storage. This module gives the app
//! the crypto trigger, the secure-erase primitive, and the manifest of what to
//! remove, so every app wipes the same identity material the same way.

use rand::RngCore;
use sha2::{Digest, Sha256};
use subtle::ConstantTimeEq;

/// Number of one-time / signed pre-key slots a wipe sweeps (0..N-1).
pub const MAX_PRE_KEYS: usize = 200;

/// The key-store entry names that together constitute an AetherNet identity —
/// everything a panic-wipe must destroy, besides the numbered pre-keys. Same
/// order as the C# reference and the shared fixture.
pub const IDENTITY_KEY_NAMES: &[&str] = &[
    "aether_identity_pub",
    "aether_identity_priv",
    "aether_identity_generated",
    "aether_device_salt",
    "aether_drk",
    "aether_ble_rotation_key",
    "aether_ble_irk",
];

/// Key-store name of the `index`-th one-time pre-key.
pub fn pre_key_name(index: usize) -> String {
    format!("prekey_{index}")
}

/// Key-store name of the `index`-th signed pre-key.
pub fn signed_pre_key_name(index: usize) -> String {
    format!("signed_prekey_{index}")
}

/// The duress-PIN hash: SHA-256 of the UTF-8 PIN. Stored at setup and compared on
/// unlock — the PIN is only ever kept as this hash.
pub fn duress_pin_hash(pin: &str) -> [u8; 32] {
    Sha256::digest(pin.as_bytes()).into()
}

/// Constant-time check of whether `pin` matches a stored [`duress_pin_hash`] —
/// i.e. whether unlocking should trigger a wipe.
///
/// Returns `false` for any `stored_hash` that is not exactly 32 bytes; otherwise
/// compares in constant time (no early-out on the first differing byte).
pub fn verify_duress_pin(pin: &str, stored_hash: &[u8]) -> bool {
    if stored_hash.len() != 32 {
        return false;
    }
    duress_pin_hash(pin).ct_eq(stored_hash).into()
}

/// Best-effort secure erase of in-memory key material: overwrite with random
/// bytes, then zero. Call on every buffer holding a secret before releasing it.
/// Defence in depth — the runtime or OS may still hold copies, but this removes
/// the obvious one and leaves no plaintext secret in the buffer.
pub fn secure_erase(buf: &mut [u8]) {
    if buf.is_empty() {
        return;
    }
    rand::thread_rng().fill_bytes(buf);
    buf.iter_mut().for_each(|b| *b = 0);
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn duress_pin_hash_is_sha256_of_utf8() {
        // SHA-256("") — the canonical empty digest.
        assert_eq!(
            duress_pin_hash(""),
            [
                0xe3, 0xb0, 0xc4, 0x42, 0x98, 0xfc, 0x1c, 0x14, 0x9a, 0xfb, 0xf4, 0xc8, 0x99, 0x6f,
                0xb9, 0x24, 0x27, 0xae, 0x41, 0xe4, 0x64, 0x9b, 0x93, 0x4c, 0xa4, 0x95, 0x99, 0x1b,
                0x78, 0x52, 0xb8, 0x55,
            ]
        );
    }

    #[test]
    fn verify_matches_only_the_right_pin() {
        let hash = duress_pin_hash("1234");
        assert!(verify_duress_pin("1234", &hash));
        assert!(!verify_duress_pin("12345", &hash));
        assert!(!verify_duress_pin("", &hash));
    }

    #[test]
    fn verify_rejects_wrong_length_hash() {
        assert!(!verify_duress_pin("1234", &[0u8; 16]));
        assert!(!verify_duress_pin("1234", &[0u8; 31]));
        assert!(!verify_duress_pin("1234", &[0u8; 33]));
        assert!(!verify_duress_pin("1234", &[]));
    }

    #[test]
    fn secure_erase_zeroes_buffer_and_tolerates_empty() {
        let mut buf = vec![0xAAu8; 64];
        secure_erase(&mut buf);
        assert!(buf.iter().all(|&b| b == 0));

        let mut empty: [u8; 0] = [];
        secure_erase(&mut empty); // must not panic
    }

    #[test]
    fn key_name_patterns() {
        assert_eq!(MAX_PRE_KEYS, 200);
        assert_eq!(IDENTITY_KEY_NAMES.len(), 7);
        assert_eq!(pre_key_name(7), "prekey_7");
        assert_eq!(signed_pre_key_name(3), "signed_prekey_3");
    }
}
