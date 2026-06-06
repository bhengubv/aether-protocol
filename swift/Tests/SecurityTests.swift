// SPDX-License-Identifier: MIT

import XCTest
@testable import AetherNetProtocol

final class SecurityTests: XCTestCase {
    func testEd25519KeyGeneration() {
        let (privateKey, publicKey) = Ed25519Service.generateKeyPair()

        XCTAssertEqual(privateKey.count, 32, "Private key must be 32 bytes")
        XCTAssertEqual(publicKey.count, 32, "Public key must be 32 bytes")
        XCTAssertNotEqual(privateKey, publicKey, "Keys should be different")
    }

    func testEd25519SignAndVerify() throws {
        let (privateKey, publicKey) = Ed25519Service.generateKeyPair()
        let message = "Test message for signing".data(using: .utf8)!

        let signature = try Ed25519Service.sign(privateKey, message)
        XCTAssertEqual(signature.count, 64, "Signature must be 64 bytes")

        let isValid = Ed25519Service.verify(publicKey, message, signature)
        XCTAssertTrue(isValid, "Signature should be valid")
    }

    func testEd25519RejectInvalidSignature() throws {
        let (privateKey, publicKey) = Ed25519Service.generateKeyPair()
        let message = "Original message".data(using: .utf8)!
        let signature = try Ed25519Service.sign(privateKey, message)

        let tamperedMessage = "Tampered message".data(using: .utf8)!
        let isValid = Ed25519Service.verify(publicKey, tamperedMessage, signature)
        XCTAssertFalse(isValid, "Should reject signature for different message")
    }

    func testEd25519RejectWrongKey() throws {
        let (privateKey1, _) = Ed25519Service.generateKeyPair()
        let (_, publicKey2) = Ed25519Service.generateKeyPair()

        let message = "Test message".data(using: .utf8)!
        let signature = try Ed25519Service.sign(privateKey1, message)

        let isValid = Ed25519Service.verify(publicKey2, message, signature)
        XCTAssertFalse(isValid, "Should reject signature from wrong key")
    }

    func testEd25519InvalidKeySize() throws {
        let invalidKey = Data(repeating: 0x00, count: 16)
        let message = "Test".data(using: .utf8)!

        XCTAssertThrowsError(try Ed25519Service.sign(invalidKey, message)) { error in
            guard case .invalidKeySize = error as! Ed25519Error else {
                XCTFail("Expected invalidKeySize error")
                return
            }
        }
    }

    func testEd25519VerifyWrongSignatureSize() {
        let (_, publicKey) = Ed25519Service.generateKeyPair()
        let message = "Test".data(using: .utf8)!
        let wrongSignature = Data(repeating: 0x00, count: 32)

        let isValid = Ed25519Service.verify(publicKey, message, wrongSignature)
        XCTAssertFalse(isValid, "Should reject signature of wrong size")
    }

    func testSignalProtocolSessionEstablishment() async throws {
        let alice = SignalProtocolService()
        let bob = SignalProtocolService()

        // Bob publishes pre-key bundle
        let bobBundle = try await bob.generatePreKeyBundle(localUhid: "bob")

        // Alice processes bundle
        try await alice.processPreKeyBundle(bobBundle)
        let aliceHasBob = await alice.hasSession(peerUhid: "bob")
        XCTAssertTrue(aliceHasBob)

        // For bidirectional, Bob also establishes session with Alice
        let aliceBundle = try await alice.generatePreKeyBundle(localUhid: "alice")
        try await bob.processPreKeyBundle(aliceBundle)
        let bobHasAlice = await bob.hasSession(peerUhid: "alice")
        XCTAssertTrue(bobHasAlice)
    }

    func testSignalProtocolEncryptDecrypt() async throws {
        let alice = SignalProtocolService()
        let bob = SignalProtocolService()

        // Setup sessions
        let bobBundle = try await bob.generatePreKeyBundle(localUhid: "bob")
        try await alice.processPreKeyBundle(bobBundle)

        let aliceBundle = try await alice.generatePreKeyBundle(localUhid: "alice")
        try await bob.processPreKeyBundle(aliceBundle)

        // Alice encrypts
        let plaintext = "Secret message".data(using: .utf8)!
        let encrypted = try await alice.encrypt(peerUhid: "bob", plaintext: plaintext)

        XCTAssertNotEqual(encrypted.ciphertext, plaintext, "Should not be plaintext")
        XCTAssertEqual(encrypted.nonce.count, 12, "Nonce must be 12 bytes")

        // Bob decrypts
        let decrypted = try await bob.decrypt(peerUhid: "alice", payload: encrypted)
        XCTAssertEqual(decrypted, plaintext, "Decrypted should match original")
    }

    func testSignalProtocolRatcheting() async throws {
        let alice = SignalProtocolService()
        let bob = SignalProtocolService()

        // Setup
        let bobBundle = try await bob.generatePreKeyBundle(localUhid: "bob")
        try await alice.processPreKeyBundle(bobBundle)

        let aliceBundle = try await alice.generatePreKeyBundle(localUhid: "alice")
        try await bob.processPreKeyBundle(aliceBundle)

        // Send multiple messages
        let messages = ["msg1", "msg2", "msg3"]
        var encrypted: [EncryptedPayload] = []

        for msg in messages {
            let data = msg.data(using: .utf8)!
            let enc = try await alice.encrypt(peerUhid: "bob", plaintext: data)
            encrypted.append(enc)
        }

        // Verify counters increment
        XCTAssertEqual(encrypted[0].counter, 0)
        XCTAssertEqual(encrypted[1].counter, 1)
        XCTAssertEqual(encrypted[2].counter, 2)

        // Verify all decrypt correctly
        for (i, enc) in encrypted.enumerated() {
            let dec = try await bob.decrypt(peerUhid: "alice", payload: enc)
            let expected = messages[i].data(using: .utf8)!
            XCTAssertEqual(dec, expected)
        }
    }

    func testSignalProtocolNoSessionError() async throws {
        let alice = SignalProtocolService()
        let plaintext = "message".data(using: .utf8)!

        // XCTAssertThrowsError doesn't support async closures in Swift 6 — use do/catch.
        do {
            _ = try await alice.encrypt(peerUhid: "unknown", plaintext: plaintext)
            XCTFail("Expected noSessionEstablished error")
        } catch let error as SignalProtocolError {
            guard case .noSessionEstablished = error else {
                XCTFail("Expected noSessionEstablished error but got \(error)")
                return
            }
        }
    }

    func testSignalProtocolSignatureVerification() async throws {
        let alice = SignalProtocolService()
        let bob = SignalProtocolService()

        // Bob creates bundle
        let bobBundle = try await bob.generatePreKeyBundle(localUhid: "bob")

        // Verify signature over signed pre-key
        let isValid = Ed25519Service.verify(
            bobBundle.identityKey,
            bobBundle.signedPreKey,
            bobBundle.signedPreKeySignature
        )
        XCTAssertTrue(isValid, "Pre-key bundle signature should be valid")
    }

    func testPacketSigningService() async throws {
        let (privateKey, publicKey) = Ed25519Service.generateKeyPair()
        let signer = await PacketSigningService(privateKey: privateKey, publicKey: publicKey)

        var packet = MeshPacket(
            type: .data,
            sourceUhid: "alice",
            destinationUhid: "bob"
        )
        packet.payload = "test".data(using: .utf8)!

        // Generate nonce
        var nonce = Data(count: 8)
        nonce.withUnsafeMutableBytes { buffer in
            var rng = SystemRandomNumberGenerator()
            for i in 0..<8 { buffer[i] = rng.next() }
        }
        packet.packetNonce = nonce

        // Sign
        try await signer.signPacket(&packet)
        XCTAssertFalse(packet.signature.isEmpty, "Signature should be set")

        // Verify
        let isValid = try await signer.verifyPacket(packet, againstPublicKey: publicKey)
        XCTAssertTrue(isValid, "Signature should be valid")
    }

    func testPacketSigningReplayPrevention() async throws {
        let (privateKey, publicKey) = Ed25519Service.generateKeyPair()
        let signer = await PacketSigningService(privateKey: privateKey, publicKey: publicKey)

        var packet = MeshPacket(
            type: .data,
            sourceUhid: "alice"
        )

        var nonce = Data(count: 8)
        nonce.withUnsafeMutableBytes { buffer in
            var rng = SystemRandomNumberGenerator()
            for i in 0..<8 { buffer[i] = rng.next() }
        }
        packet.packetNonce = nonce

        try await signer.signPacket(&packet)

        // First verification should succeed
        let isValid1 = try await signer.verifyPacket(packet, againstPublicKey: publicKey)
        XCTAssertTrue(isValid1)

        // Replay should fail (same nonce) — XCTAssertThrowsError doesn't support async.
        do {
            _ = try await signer.verifyPacket(packet, againstPublicKey: publicKey)
            XCTFail("Expected duplicateNonce error on replay")
        } catch let error as PacketSigningError {
            guard case .duplicateNonce = error else {
                XCTFail("Expected duplicateNonce error but got \(error)")
                return
            }
        }
    }

    /// Regression test for the pre-2026-05-05 cache-key bug. Pre-fix, the
    /// dedup cache stored ONE nonce per source — every subsequent packet
    /// from the same source overwrote the prior cached nonce, so a replay
    /// of an earlier nonce was accepted. Post-fix, the cache is keyed by
    /// (source, nonce) so each (source, nonce) pair is independently
    /// remembered for the freshness window.
    func testReplayOfEarlierNonceFromSameSenderRejected() async throws {
        let (privateKey, publicKey) = Ed25519Service.generateKeyPair()
        let signer = await PacketSigningService(privateKey: privateKey, publicKey: publicKey)

        var pkt1 = MeshPacket(type: .data, sourceUhid: "alice", destinationUhid: "bob")
        pkt1.packetNonce = Data([0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08])
        try await signer.signPacket(&pkt1)
        let v1 = try await signer.verifyPacket(pkt1, againstPublicKey: publicKey)
        XCTAssertTrue(v1)

        // A different nonce from the same source — must succeed.
        var pkt2 = MeshPacket(type: .data, sourceUhid: "alice", destinationUhid: "bob")
        pkt2.packetNonce = Data([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x00, 0x11])
        try await signer.signPacket(&pkt2)
        let v2 = try await signer.verifyPacket(pkt2, againstPublicKey: publicKey)
        XCTAssertTrue(v2)

        // Now REPLAY the FIRST nonce. The pre-fix bug would let this
        // through because pkt2 overwrote pkt1's entry. Must be rejected.
        do {
            _ = try await signer.verifyPacket(pkt1, againstPublicKey: publicKey)
            XCTFail("Expected duplicateNonce error for replayed earlier nonce")
        } catch let error as PacketSigningError {
            guard case .duplicateNonce = error else {
                XCTFail("Expected duplicateNonce error but got \(error)")
                return
            }
        }
    }

    /// Two distinct senders may legitimately use the same nonce — they
    /// share no replay risk because each (source, nonce) pair is tracked
    /// separately. Pre-fix, the cache keyed by source alone would still
    /// allow this (different keys), but the fix makes the design intent
    /// explicit and ensures it stays correct after future refactors.
    func testSameNonceFromDifferentSendersAccepted() async throws {
        let (privateKey, publicKey) = Ed25519Service.generateKeyPair()
        let signer = await PacketSigningService(privateKey: privateKey, publicKey: publicKey)

        let sharedNonce = Data([0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08])

        var aPkt = MeshPacket(type: .data, sourceUhid: "alice", destinationUhid: "carol")
        aPkt.packetNonce = sharedNonce
        try await signer.signPacket(&aPkt)
        let vA = try await signer.verifyPacket(aPkt, againstPublicKey: publicKey)
        XCTAssertTrue(vA)

        var bPkt = MeshPacket(type: .data, sourceUhid: "bob", destinationUhid: "carol")
        bPkt.packetNonce = sharedNonce
        try await signer.signPacket(&bPkt)
        // Different source, same nonce — must be accepted.
        let vB = try await signer.verifyPacket(bPkt, againstPublicKey: publicKey)
        XCTAssertTrue(vB, "Different source, same nonce — must be accepted.")
    }

    // MARK: - Reputation hook tests

    /// A replay (duplicate nonce) must call `recordReplayAttempt(uhid:)` on
    /// the reputation service, reducing the source's score by 0.15.
    func testReplayFiresReputationHook() async throws {
        let (privateKey, publicKey) = Ed25519Service.generateKeyPair()
        let signer = await PacketSigningService(privateKey: privateKey, publicKey: publicKey)
        let rep = NodeReputationService()
        await signer.setReputation(rep)

        var packet = MeshPacket(type: .data, sourceUhid: "attacker", destinationUhid: "victim")
        packet.packetNonce = Data([0xDE, 0xAD, 0xBE, 0xEF, 0xCA, 0xFE, 0xBA, 0xBE])
        try await signer.signPacket(&packet)

        // First verify — legitimate; no penalty.
        let firstResult = try await signer.verifyPacket(packet, againstPublicKey: publicKey)
        XCTAssertTrue(firstResult)
        let scoreAfterFirst = await rep.reputationScore(for: "attacker")
        XCTAssertEqual(scoreAfterFirst, 1.0, "No penalty on a fresh (source, nonce) pair")

        // Replay attempt — must throw and apply the −0.15 replay penalty.
        do {
            _ = try await signer.verifyPacket(packet, againstPublicKey: publicKey)
            XCTFail("Expected duplicateNonce error on replay")
        } catch let error as PacketSigningError {
            guard case .duplicateNonce = error else {
                XCTFail("Expected duplicateNonce error but got \(error)")
                return
            }
        }

        let scoreAfterReplay = await rep.reputationScore(for: "attacker")
        XCTAssertEqual(
            scoreAfterReplay, 0.85, accuracy: 1e-10,
            "Replay penalty (−0.15) must reduce score from 1.0 to 0.85"
        )
    }

    /// A fresh (not-yet-seen) nonce must NOT fire any reputation hook —
    /// the score for the source stays at 1.0 (default).
    func testFreshNonceDoesNotFireReputationHook() async throws {
        let (privateKey, publicKey) = Ed25519Service.generateKeyPair()
        let signer = await PacketSigningService(privateKey: privateKey, publicKey: publicKey)
        let rep = NodeReputationService()
        await signer.setReputation(rep)

        var packet = MeshPacket(type: .data, sourceUhid: "alice", destinationUhid: "bob")
        packet.packetNonce = Data([0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88])
        try await signer.signPacket(&packet)

        let isValid = try await signer.verifyPacket(packet, againstPublicKey: publicKey)
        XCTAssertTrue(isValid)

        let score = await rep.reputationScore(for: "alice")
        XCTAssertEqual(score, 1.0, "A legitimately fresh nonce must not penalise the source")
    }

    /// An invalid signature (tampered payload) must call
    /// `recordSignatureFailure(uhid:)` on the reputation service,
    /// reducing the source's score by 0.20.
    func testSignatureFailureFiresReputationHook() async throws {
        let (privateKey, publicKey) = Ed25519Service.generateKeyPair()
        let (_, wrongPublicKey) = Ed25519Service.generateKeyPair()
        let signer = await PacketSigningService(privateKey: privateKey, publicKey: publicKey)
        let rep = NodeReputationService()
        await signer.setReputation(rep)

        var packet = MeshPacket(type: .data, sourceUhid: "impostor", destinationUhid: "victim")
        packet.packetNonce = Data([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x01, 0x02])
        try await signer.signPacket(&packet)

        // Verify with the wrong public key — signature check must return false.
        let isValid = try await signer.verifyPacket(packet, againstPublicKey: wrongPublicKey)
        XCTAssertFalse(isValid, "Wrong public key must cause signature verification to fail")

        let score = await rep.reputationScore(for: "impostor")
        XCTAssertEqual(
            score, 0.80, accuracy: 1e-10,
            "Signature-failure penalty (−0.20) must reduce score from 1.0 to 0.80"
        )
    }
}
