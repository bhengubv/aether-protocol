// SPDX-License-Identifier: MIT
//
// Forward secrecy / deletion-proof integration tests for the Signal Protocol
// implementation in aether-protocol.
//
// These tests exercise three properties that must hold after the ratchet
// has advanced:
//
//  1. A ciphertext whose message key was consumed cannot be decrypted again,
//     even after the ratchet has moved forward several DH steps.
//  2. A failed replay attempt does not corrupt session state; subsequent
//     legitimate messages still decrypt.
//  3. Encrypting the same plaintext twice always produces distinct ciphertexts
//     (AES-256-GCM with a fresh random nonce each call), and both ciphertexts
//     decrypt to the original plaintext.

use aethermesh_protocol::security::signal_protocol::SignalProtocolService;

// ─── helpers ────────────────────────────────────────────────────────────────

/// Set up a freshly-established Alice-Bob session.
///
/// Both `alice` and `bob` are mutated in place:
///   * Bob generates + holds a pre-key bundle.
///   * Alice processes that bundle.
///   * No messages are exchanged yet; the caller decides what to send first.
fn establish_session(alice: &mut SignalProtocolService, bob: &mut SignalProtocolService) {
    let bob_bundle = bob.generate_pre_key_bundle("bob").unwrap();
    alice.generate_pre_key_bundle("alice").unwrap();
    alice.process_pre_key_bundle(&bob_bundle).unwrap();
}

// ─── test 1 ─────────────────────────────────────────────────────────────────

/// **Replay of a consumed message must fail after ratchet advancement.**
///
/// Security property: forward secrecy means that once a message key has been
/// used (and should be deleted), re-presenting the matching ciphertext must
/// not produce the plaintext. The implementation enforces this because:
///   * The symmetric ratchet advances the chain key one step per message —
///     the old message key is never held.
///   * AES-256-GCM authentication will fail with an incorrect key.
///
/// Advancement here means 5 full bidirectional round-trips so both sides
/// have DH-ratcheted multiple times; any in-memory key material belonging
/// to the first message's epoch is unreachable.
#[test]
fn forward_secrecy_replay_of_consumed_message_fails() {
    let mut alice = SignalProtocolService::new();
    let mut bob = SignalProtocolService::new();
    establish_session(&mut alice, &mut bob);

    // Alice sends M1 (PreKey message — establishes Bob's session).
    let m1 = alice.encrypt("bob", b"forward secret payload").unwrap();

    // Bob decrypts M1 and establishes a responder-side session.
    let plaintext = bob.decrypt("alice", &m1).unwrap();
    assert_eq!(plaintext, b"forward secret payload", "initial decrypt must succeed");

    // Advance the ratchet: 5 bidirectional pairs.
    for i in 0u8..5 {
        let a = alice.encrypt("bob", &[i, b'a']).unwrap();
        bob.decrypt("alice", &a).unwrap();

        let b = bob.encrypt("alice", &[i, b'b']).unwrap();
        alice.decrypt("bob", &b).unwrap();
    }

    // Attempt to replay M1 now that the ratchet has advanced well past it.
    // Bob's session is at a completely different chain/counter state, so the
    // AES-GCM tag produced with the consumed (gone) message key must fail
    // authentication.
    let replay_result = bob.decrypt("alice", &m1);
    assert!(
        replay_result.is_err(),
        "Replaying a consumed message after ratchet advancement must fail; got Ok instead"
    );
}

// ─── test 2 ─────────────────────────────────────────────────────────────────

/// **Session remains healthy after a failed replay attempt.**
///
/// A replay attempt must be an inert, self-contained error: it must not
/// corrupt the receive chain key, increment the receive counter, or leave
/// any other state that would prevent legitimate subsequent messages from
/// decrypting correctly.
#[test]
fn forward_secrecy_session_healthy_after_replay_attempt() {
    let mut alice = SignalProtocolService::new();
    let mut bob = SignalProtocolService::new();
    establish_session(&mut alice, &mut bob);

    // M1: initial message, Bob establishes his session.
    let m1 = alice.encrypt("bob", b"ephemeral secret").unwrap();
    bob.decrypt("alice", &m1).unwrap();

    // Advance ratchet (3 bidirectional pairs) to move past M1's key epoch.
    for i in 0u8..3 {
        let a = alice.encrypt("bob", &[i]).unwrap();
        bob.decrypt("alice", &a).unwrap();
        let b = bob.encrypt("alice", &[i]).unwrap();
        alice.decrypt("bob", &b).unwrap();
    }

    // Replay M1 — must fail; ignore the error (testing that the session is
    // not damaged, not the precise error variant).
    let _ = bob.decrypt("alice", &m1);

    // Alice encrypts a fresh message after the replay attempt.
    let fresh = alice.encrypt("bob", b"still alive").unwrap();
    let result = bob.decrypt("alice", &fresh);
    assert!(
        result.is_ok(),
        "Bob must be able to decrypt a fresh message after a failed replay attempt"
    );
    assert_eq!(
        result.unwrap(),
        b"still alive",
        "Decrypted plaintext must match what Alice sent"
    );
}

// ─── test 3 ─────────────────────────────────────────────────────────────────

/// **Same plaintext encrypted twice must produce distinct ciphertexts.**
///
/// AES-256-GCM uses a random 12-byte nonce per encryption call. Even with an
/// identical plaintext and the same message key in scope (two consecutive
/// encrypts before any ratchet step), the nonces will differ so the resulting
/// ciphertexts are distinct. Both ciphertexts must decrypt back to the
/// original plaintext, confirming neither was corrupted by the uniqueness
/// check.
///
/// Note: consecutive encrypts here land on *different* message keys because
/// the symmetric ratchet advances the chain key after each call (HMAC step),
/// so in practice both the nonce AND the key differ. The assertion is still
/// meaningful: it confirms that no accidental determinism slipped in (e.g.
/// a seeded RNG or a reused nonce).
#[test]
fn forward_secrecy_same_plaintext_yields_different_ciphertexts() {
    let mut alice = SignalProtocolService::new();
    let mut bob = SignalProtocolService::new();
    establish_session(&mut alice, &mut bob);

    // Encrypt the same plaintext twice on the same session.
    let enc1 = alice.encrypt("bob", b"hello").unwrap();
    let enc2 = alice.encrypt("bob", b"hello").unwrap();

    // The raw ciphertext bytes (which include the GCM authentication tag and
    // embed the nonce in the payload envelope) must differ.
    assert_ne!(
        enc1.ciphertext, enc2.ciphertext,
        "Two encryptions of the same plaintext must produce distinct ciphertexts"
    );

    // Establish Bob's responder session by delivering enc1 first (PreKey), then enc2.
    let pt1 = bob.decrypt("alice", &enc1).unwrap();
    let pt2 = bob.decrypt("alice", &enc2).unwrap();

    assert_eq!(pt1, b"hello", "First ciphertext must decrypt to original plaintext");
    assert_eq!(pt2, b"hello", "Second ciphertext must decrypt to original plaintext");
}

// ─── Network partition recovery tests ───────────────────────────────────────

/// **Within-limit out-of-order delivery must decrypt from cache.**
///
/// Alice sends N+1 messages while Bob is offline. Bob reconnects and receives
/// the LAST message first (N keys skipped and cached); then decrypts the rest
/// from the skipped-key cache. All N+1 messages must round-trip.
#[test]
fn skipped_key_cache_within_limit_out_of_order_delivery_decrypts() {
    const N: usize = 5;
    let mut alice = SignalProtocolService::new();
    let mut bob = SignalProtocolService::new();
    establish_session(&mut alice, &mut bob);

    // Encrypt N+1 messages (msg[0] is the PreKey message that establishes Bob's session).
    let mut msgs = Vec::new();
    for i in 0..=N {
        let enc = alice
            .encrypt("bob", format!("partition-{i}").as_bytes())
            .unwrap_or_else(|e| panic!("alice.encrypt[{i}]: {e}"));
        msgs.push(enc);
    }

    // Bob receives msg[0] (PreKey — establishes his responder session).
    let dec0 = bob.decrypt("alice", &msgs[0]).expect("Decrypt[0] must succeed");
    assert_eq!(dec0, b"partition-0");

    // Bob receives msg[N] first (gap = N-1 keys to skip, within the 1000-entry limit).
    let decN = bob
        .decrypt("alice", &msgs[N])
        .unwrap_or_else(|e| panic!("Decrypt[{N}]: {e}"));
    assert_eq!(decN, format!("partition-{N}").as_bytes());

    // Bob decrypts msgs N-1 down to 1 from the skipped-key cache.
    for i in (1..N).rev() {
        let dec = bob
            .decrypt("alice", &msgs[i])
            .unwrap_or_else(|e| panic!("Decrypt[{i}] from cache: {e}"));
        assert_eq!(dec, format!("partition-{i}").as_bytes());
    }
}

/// **Limit exceeded must return Err, not crash, not silent corruption.**
///
/// A counter gap strictly greater than MAX_SKIPPED_KEYS must return `Err(...)`.
/// The check guards against unbounded memory exhaustion when the partition
/// duration exceeds the cache capacity.
#[test]
fn skipped_key_cache_limit_exceeded_rejects_cleanly() {
    use aethermesh_protocol::security::signal_protocol::MAX_SKIPPED_KEYS;

    let mut alice = SignalProtocolService::new();
    let mut bob = SignalProtocolService::new();
    establish_session(&mut alice, &mut bob);

    // Establish session: Bob decrypts the first (PreKey) message.
    let first = alice.encrypt("bob", b"session-open").unwrap();
    bob.decrypt("alice", &first).unwrap();

    // Alice sends MAX_SKIPPED_KEYS+2 more messages; none delivered to Bob.
    // Last message has counter = MAX_SKIPPED_KEYS+2; Bob's recv_counter = 1,
    // so gap = MAX_SKIPPED_KEYS+1 which is strictly greater than the limit.
    let mut over_limit = first.clone();
    for i in 0..(MAX_SKIPPED_KEYS + 2) {
        over_limit = alice
            .encrypt("bob", format!("offline-{i}").as_bytes())
            .unwrap_or_else(|e| panic!("alice.encrypt offline[{i}]: {e}"));
    }

    let result = bob.decrypt("alice", &over_limit);
    assert!(
        result.is_err(),
        "Expected Err for counter gap > MAX_SKIPPED_KEYS, got Ok"
    );
}
