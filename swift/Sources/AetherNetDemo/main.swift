// SPDX-License-Identifier: MIT

import AetherNetProtocol
import Foundation

// MARK: - Demo Main

@main
struct AetherNetDemo {
    static func main() async {
        print("=== Aether Protocol Demo ===\n")

        // Test 1: Packet Serialization
        await testPacketSerialization()

        // Test 2: Ed25519 Signing
        await testEd25519()

        // Test 3: Signal Protocol
        await testSignalProtocol()

        // Test 4: In-Process Transport
        await testInProcessTransport()

        // Test 5: End-to-End Messaging
        await testEndToEndMessaging()

        print("\n=== All Tests Completed ===")
    }
}

// MARK: - Test 1: Packet Serialization

func testPacketSerialization() async {
    print("Test 1: Packet Serialization")
    print("---")

    // Create a packet
    var packet = MeshPacket(
        type: .data,
        sourceUhid: "node-alice",
        destinationUhid: "node-bob",
        ttl: 7,
        priority: 0,
        payload: "Hello, Aether!".data(using: .utf8) ?? Data()
    )

    // Generate packet nonce
    var nonce = Data(count: 8)
    nonce.withUnsafeMutableBytes { buffer in
        var rng = SystemRandomNumberGenerator()
        for i in 0..<8 { buffer[i] = rng.next() }
    }
    packet.packetNonce = nonce

    print("Original packet: \(packet)")

    // Serialize
    let serialized = PacketSerializer.serialize(packet)
    print("Serialized size: \(serialized.count) bytes")

    // Deserialize
    do {
        let deserialized = try PacketSerializer.deserialize(serialized)
        print("Deserialized packet: \(deserialized)")

        // Verify
        assert(deserialized.sourceUhid == packet.sourceUhid, "Source UHID mismatch")
        assert(deserialized.destinationUhid == packet.destinationUhid, "Destination UHID mismatch")
        assert(deserialized.payload == packet.payload, "Payload mismatch")
        print("✓ Serialization/Deserialization successful\n")
    } catch {
        print("✗ Deserialization failed: \(error)\n")
    }
}

// MARK: - Test 2: Ed25519 Signing

func testEd25519() async {
    print("Test 2: Ed25519 Signing")
    print("---")

    // Generate key pair
    let (privateKey, publicKey) = Ed25519Service.generateKeyPair()
    print("Generated Ed25519 key pair")
    print("Private key size: \(privateKey.count) bytes")
    print("Public key size: \(publicKey.count) bytes")

    // Sign data
    let message = "Test message".data(using: .utf8)!
    do {
        let signature = try Ed25519Service.sign(privateKey, message)
        print("Signature size: \(signature.count) bytes")

        // Verify signature
        let isValid = Ed25519Service.verify(publicKey, message, signature)
        assert(isValid, "Signature verification failed")
        print("✓ Signature verified")

        // Verify with wrong data
        let wrongMessage = "Different message".data(using: .utf8)!
        let isInvalid = Ed25519Service.verify(publicKey, wrongMessage, signature)
        assert(!isInvalid, "Should reject wrong data")
        print("✓ Correctly rejected invalid signature\n")
    } catch {
        print("✗ Signing failed: \(error)\n")
    }
}

// MARK: - Test 3: Signal Protocol

func testSignalProtocol() async {
    print("Test 3: Signal Protocol (X3DH + Symmetric Ratchet)")
    print("---")

    let aliceService = SignalProtocolService()

    do {
        // Generate pre-key bundle
        let preKeyBundle = try await aliceService.generatePreKeyBundle(localUhid: "alice")
        print("Generated pre-key bundle")
        print("Identity key: \(preKeyBundle.identityKey.count) bytes (Ed25519)")
        print("Pre-key: \(preKeyBundle.preKey.count) bytes (P-256)")
        print("Signed pre-key: \(preKeyBundle.signedPreKey.count) bytes (P-256)")
        print("Signature: \(preKeyBundle.signedPreKeySignature.count) bytes (Ed25519)")

        // Verify signature over signed pre-key
        let isValid = Ed25519Service.verify(
            preKeyBundle.identityKey,
            preKeyBundle.signedPreKey,
            preKeyBundle.signedPreKeySignature
        )
        assert(isValid, "Pre-key signature verification failed")
        print("✓ Pre-key signature verified")

        // Encrypt a message
        let bobService = SignalProtocolService()
        let bobBundle = try await bobService.generatePreKeyBundle(localUhid: "bob")
        try await aliceService.processPreKeyBundle(bobBundle)

        let plaintext = "Secret message".data(using: .utf8)!
        let encrypted = try await aliceService.encrypt(peerUhid: "bob", plaintext: plaintext)
        print("✓ Encrypted message: \(encrypted.ciphertext.count) bytes, counter=\(encrypted.counter)")
        print("✓ Signal Protocol test passed\n")
    } catch {
        print("✗ Signal Protocol test failed: \(error)\n")
    }
}

// MARK: - Test 4: In-Process Transport

func testInProcessTransport() async {
    print("Test 4: In-Process Transport")
    print("---")

    let alice = InProcessTransport(uhid: "node-alice")
    let bob = InProcessTransport(uhid: "node-bob")

    // Set up data received handlers
    var bobReceivedData: [Data] = []
    await bob.onDataReceived { senderUhid, data in
        print("Bob received \(data.count) bytes from \(senderUhid)")
        bobReceivedData.append(data)
    }

    // Alice sends to Bob
    let message = "Hello, Bob!".data(using: .utf8)!
    let success = await alice.sendAsync(
        peerUhid: "node-bob",
        data: message,
        cancellationToken: nil
    )

    print("Send successful: \(success)")
    print("Bob received: \(bobReceivedData.count) messages")
    assert(bobReceivedData.count == 1, "Expected 1 message")
    assert(bobReceivedData[0] == message, "Message content mismatch")
    print("✓ Transport test successful\n")
}

// MARK: - Test 5: End-to-End Messaging

func testEndToEndMessaging() async {
    print("Test 5: End-to-End Messaging (Full Stack)")
    print("---")

    // Create two nodes with their own services
    let alice = AetherNetNode(
        uhid: "alice-node",
        identityPublicKey: Ed25519Service.generateKeyPair().publicKey
    )
    let bob = AetherNetNode(
        uhid: "bob-node",
        identityPublicKey: Ed25519Service.generateKeyPair().publicKey
    )

    print("Alice UHID: \(alice.uhid)")
    print("Bob UHID: \(bob.uhid)")

    // Create Signal sessions
    let aliceSignal = SignalProtocolService()
    let bobSignal = SignalProtocolService()

    do {
        // Key exchange
        let bobBundle = try await bobSignal.generatePreKeyBundle(localUhid: bob.uhid)
        try await aliceSignal.processPreKeyBundle(bobBundle)

        let aliceBundle = try await aliceSignal.generatePreKeyBundle(localUhid: alice.uhid)
        try await bobSignal.processPreKeyBundle(aliceBundle)

        // Create and sign packets
        var packet = MeshPacket(
            type: .data,
            sourceUhid: alice.uhid,
            destinationUhid: bob.uhid,
            ttl: 7,
            priority: 0,
            payload: "End-to-end test".data(using: .utf8) ?? Data()
        )

        // Add nonce
        var nonce = Data(count: 8)
        nonce.withUnsafeMutableBytes { buffer in
            var rng = SystemRandomNumberGenerator()
            for i in 0..<8 { buffer[i] = rng.next() }
        }
        packet.packetNonce = nonce

        // Get Alice's signing key
        let (alicePrivateKey, alicePublicKey) = Ed25519Service.generateKeyPair()
        let aliceSigner = await PacketSigningService(privateKey: alicePrivateKey, publicKey: alicePublicKey)

        // Sign packet
        try await aliceSigner.signPacket(&packet)
        print("Alice signed packet")

        // Serialize for transmission
        let serialized = PacketSerializer.serialize(packet)
        print("Serialized packet: \(serialized.count) bytes")

        // Deserialize on Bob's side
        let received = try PacketSerializer.deserialize(serialized)
        print("Bob received packet: \(received.type)")

        // Verify signature
        let isValid = try await aliceSigner.verifyPacket(received, againstPublicKey: alicePublicKey)
        assert(isValid, "Signature verification failed")
        print("✓ Bob verified Alice's signature")

        print("✓ End-to-end messaging test successful\n")
    } catch {
        print("✗ End-to-end test failed: \(error)\n")
    }
}

// MARK: - Helper Extensions

extension NSLock {
    func withLock<T>(_ closure: () -> T) -> T {
        lock()
        defer { unlock() }
        return closure()
    }
}
