// SPDX-License-Identifier: MIT

import Crypto
import Foundation

/// Panic-wipe: the identity-erasure core of an AetherNet node's duress defence.
/// A duress PIN (or panic button) irreversibly destroys the node's key material,
/// so a seized device reveals nothing and looks like a fresh install.
///
/// This is the protocol-level core — deterministic and portable across every
/// AetherNet SDK:
/// - ``duressPinHash(_:)`` / ``verifyDuressPin(_:storedHash:)`` — recognise the
///   duress PIN (SHA-256, constant-time compare); the PIN itself is never stored.
/// - ``secureErase(_:)`` — best-effort in-memory erase of key material (overwrite
///   with random, then zero).
/// - ``identityKeyNames`` + ``preKeyName(_:)`` / ``signedPreKeyName(_:)`` — the
///   canonical set of key-store entries a wipe must destroy.
///
/// Destroying the hosting app's local database, platform keychain entries and any
/// decoy store is the app's job — it owns that storage. This type gives the app
/// the crypto trigger, the secure-erase primitive, and the manifest of what to
/// remove, so every app wipes the same identity material the same way.
///
/// The deterministic parts (the duress-PIN hash and the key-store name manifest)
/// are byte-identical across every AetherNet SDK, verified against
/// `fixtures/panicwipe/vectors.json`.
///
/// Mirrors `AetherNet.Security.Privacy.PanicWipe` (C#) byte-for-byte.
public enum PanicWipe {

    /// Number of one-time / signed pre-key slots a wipe sweeps (0..N-1).
    public static let maxPreKeys: Int = 200

    /// The key-store entry names that together constitute an AetherNet identity —
    /// everything a panic-wipe must destroy, besides the numbered pre-keys.
    public static let identityKeyNames: [String] = [
        "aether_identity_pub",
        "aether_identity_priv",
        "aether_identity_generated",
        "aether_device_salt",
        "aether_drk",
        "aether_ble_rotation_key",
        "aether_ble_irk",
    ]

    /// Key-store name of the i-th one-time pre-key.
    public static func preKeyName(_ index: Int) -> String { "prekey_\(index)" }

    /// Key-store name of the i-th signed pre-key.
    public static func signedPreKeyName(_ index: Int) -> String { "signed_prekey_\(index)" }

    /// The duress-PIN hash: SHA-256 of the UTF-8 PIN. Stored at setup and compared
    /// on unlock — the PIN is only ever kept as this hash (32 bytes).
    public static func duressPinHash(_ pin: String) -> Data {
        Data(SHA256.hash(data: Data(pin.utf8)))
    }

    /// Constant-time check of whether `pin` matches a stored ``duressPinHash(_:)``
    /// — i.e. whether unlocking should trigger a wipe. Returns false if
    /// `storedHash` is not exactly 32 bytes.
    public static func verifyDuressPin(_ pin: String, storedHash: Data) -> Bool {
        guard storedHash.count == 32 else { return false }
        return constantTimeEquals(duressPinHash(pin), storedHash)
    }

    /// Best-effort secure erase of in-memory key material: overwrite with random
    /// bytes, then zero. Call on every buffer holding a secret before releasing
    /// it. Defence in depth — the runtime or OS may still hold copies, but this
    /// removes the obvious one and leaves no plaintext secret in the buffer.
    public static func secureErase(_ buffer: inout Data) {
        if buffer.isEmpty { return }

        // Overwrite with random bytes first, then zero. Matches the C#
        // RandomNumberGenerator.Fill + CryptographicOperations.ZeroMemory.
        var rng = SystemRandomNumberGenerator()
        buffer.withUnsafeMutableBytes { raw in
            guard let base = raw.baseAddress else { return }
            let bytes = base.assumingMemoryBound(to: UInt8.self)
            for i in 0 ..< raw.count {
                bytes[i] = UInt8.random(in: UInt8.min ... UInt8.max, using: &rng)
            }
            memset(base, 0, raw.count)
        }
    }

    // MARK: - Private

    /// Constant-time byte comparison (XOR-accumulate), matching the C#
    /// `CryptographicOperations.FixedTimeEquals`. Runs in time proportional to the
    /// length regardless of where the first mismatch is, so it leaks no timing
    /// signal about the secret. Callers must gate on equal length beforehand.
    private static func constantTimeEquals(_ a: Data, _ b: Data) -> Bool {
        if a.count != b.count { return false }
        var diff: UInt8 = 0
        for i in 0 ..< a.count {
            diff |= a[a.startIndex + i] ^ b[b.startIndex + i]
        }
        return diff == 0
    }
}
