// SPDX-License-Identifier: MIT

import XCTest
@testable import AetherProtocol

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
        let (privateKey1, publicKey1) = Ed25519Service.generateKeyPair()
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
        XCTAssertTrue(await alice.hasSession(peerUhid: "bob"))

        // For bidirectional, Bob also establishes session with Alice
        let aliceBundle = try await alice.generatePreKeyBundle(localUhid: "alice")
        try await bob.processPreKeyBundle(aliceBundle)
        XCTAssertTrue(await bob.hasSession(peerUhid: "alice"))
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

        XCTAssertThrowsError(
            try await alice.encrypt(peerUhid: "unknown", plaintext: plaintext)
        ) { error in
            guard case .noSessionEstablished = error as! SignalProtocolError else {
                XCTFail("Expected noSessionEstablished error")
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
        _ = nonce.withUnsafeMutableBytes { buffer in
            SecRandomCopyBytes(kSecRandomDefault, 8, buffer.baseAddress!)
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
        _ = nonce.withUnsafeMutableBytes { buffer in
            SecRandomCopyBytes(kSecRandomDefault, 8, buffer.baseAddress!)
        }
        packet.packetNonce = nonce

        try await signer.signPacket(&packet)

        // First verification should succeed
        let isValid1 = try await signer.verifyPacket(packet, againstPublicKey: publicKey)
        XCTAssertTrue(isValid1)

        // Replay should fail (same nonce)
        XCTAssertThrowsError(
            try await signer.verifyPacket(packet, againstPublicKey: publicKey)
        ) { error in
            guard case .duplicateNonce = error as! PacketSigningError else {
                XCTFail("Expected duplicateNonce error")
                return
            }
        }
    }
}
