// SPDX-License-Identifier: MIT

package aethermesh

import aethermesh.models.AetherMeshNode
import aethermesh.models.NodeCapabilities
import aethermesh.protocol.MeshPacket
import aethermesh.protocol.PacketSerializer
import aethermesh.protocol.PacketType
import aethermesh.security.Ed25519Service
import aethermesh.security.PacketSigning
import aethermesh.security.SignalProtocol
import aethermesh.transport.InProcessTransport
import java.security.SecureRandom
import java.util.*

/**
 * Demo application showing the Aether protocol in action.
 *
 * Demonstrates:
 * 1. Key generation
 * 2. Packet serialization and deserialization
 * 3. Packet signing and verification
 * 4. Signal Protocol session establishment
 * 5. End-to-end encryption and decryption
 */

suspend fun main() {
    println("=== Aether Protocol Kotlin Implementation Demo ===\n")

    // Step 1: Generate Ed25519 keys for two nodes
    println("Step 1: Generating Ed25519 identity keys...")
    val (alicePrivateKey, alicePublicKey) = Ed25519Service.generateKeyPair()
    val (_, bobPublicKey) = Ed25519Service.generateKeyPair()
    println("Alice's public key: ${toHex(alicePublicKey.take(8).toByteArray())}...")
    println("Bob's public key: ${toHex(bobPublicKey.take(8).toByteArray())}...\n")

    // Step 2: Create Aether nodes
    println("Step 2: Creating Aether nodes...")
    val aliceUhid = "node-alice-001"
    val bobUhid = "node-bob-001"

    val alice = AetherMeshNode(
        uhid = aliceUhid,
        identityPublicKey = alicePublicKey,
        capabilities = NodeCapabilities(ble = true, wifiDirect = true, relay = true)
    )

    val bob = AetherMeshNode(
        uhid = bobUhid,
        identityPublicKey = bobPublicKey,
        capabilities = NodeCapabilities(ble = true, wifiDirect = true, gateway = true)
    )

    println("Alice UHID: $aliceUhid (relay=${alice.capabilities.relay}, ble=${alice.capabilities.ble})")
    println("Bob UHID: $bobUhid (gateway=${bob.capabilities.gateway}, ble=${bob.capabilities.ble})\n")

    // Step 3: Create Signal Protocol instances
    println("Step 3: Initializing Signal Protocol...")
    val aliceSignal = SignalProtocol()
    val bobSignal = SignalProtocol()
    println("Signal Protocol instances created\n")

    // Step 4: Generate and exchange pre-key bundles
    println("Step 4: Pre-key bundle exchange...")
    val aliceBundle = aliceSignal.generatePreKeyBundle(aliceUhid)
    val bobBundle = bobSignal.generatePreKeyBundle(bobUhid)
    println("Alice generated pre-key bundle (PreKeyId=${aliceBundle.preKeyId})")
    println("Bob generated pre-key bundle (PreKeyId=${bobBundle.preKeyId})\n")

    // Step 5: Process bundles and establish sessions
    println("Step 5: Establishing encrypted sessions...")
    aliceSignal.processPreKeyBundle(bobBundle)
    bobSignal.processPreKeyBundle(aliceBundle)
    println("Alice -> Bob session established")
    println("Bob -> Alice session established\n")

    // Step 6: Create and serialize a packet
    println("Step 6: Creating and signing a packet...")
    val message = "Hello Bob, this is Alice!"
    val packet = MeshPacket(
        type = PacketType.Data,
        sourceUhid = aliceUhid,
        destinationUhid = bobUhid,
        ttl = 7,
        priority = 0,
        payload = message.toByteArray(Charsets.UTF_8),
        packetNonce = ByteArray(8).apply { SecureRandom().nextBytes(this) },
        timestampMs = System.currentTimeMillis()
    )

    // Sign the packet
    val signature = PacketSigning.signPacket(packet, alicePrivateKey)
    val signedPacket = packet.copy(signature = signature)
    println("Packet signed with Ed25519 (${signature.size} bytes)")
    println("Message: \"$message\"\n")

    // Step 7: Serialize the packet
    println("Step 7: Serializing packet to wire format...")
    val wireData = PacketSerializer.serialize(signedPacket)
    println("Serialized size: ${wireData.size} bytes")
    println("Wire format: ${toHex(wireData.take(32).toByteArray())}...\n")

    // Step 8: Deserialize and verify
    println("Step 8: Deserializing and verifying packet...")
    val deserializedPacket = PacketSerializer.deserialize(wireData)
    println("Deserialized packet: $deserializedPacket")

    val isValid = PacketSigning.verifyPacket(deserializedPacket, alicePublicKey)
    println("Signature verification: ${if (isValid) "VALID" else "INVALID"}\n")

    // Step 9: Encrypt and decrypt a message
    println("Step 9: End-to-end message encryption...")
    val plaintext = "Secret message from Alice to Bob".toByteArray()
    println("Plaintext: ${String(plaintext)}")

    val encryptedPayload = aliceSignal.encrypt(bobUhid, plaintext)
    println("Encrypted (ciphertext: ${encryptedPayload.ciphertext.size} bytes, nonce: ${encryptedPayload.nonce.size} bytes)")

    val decryptedPayload = bobSignal.decrypt(aliceUhid, encryptedPayload)
    println("Decrypted: ${String(decryptedPayload)}\n")

    // Step 10: Demonstrate replay protection
    println("Step 10: Testing replay protection...")
    val testPacket = MeshPacket(
        type = PacketType.Heartbeat,
        sourceUhid = aliceUhid,
        destinationUhid = bobUhid,
        packetNonce = ByteArray(8).apply { SecureRandom().nextBytes(this) }
    )

    val isNew1 = PacketSigning.isNewPacket(testPacket)
    val isNew2 = PacketSigning.isNewPacket(testPacket)
    println("First reception: isNew=$isNew1 (expected: true)")
    println("Replay attempt: isNew=$isNew2 (expected: false)\n")

    // Step 11: Demonstrate in-process transport
    println("Step 11: Testing in-process transport...")
    val aliceTransport = InProcessTransport("alice-transport")
    val bobTransport = InProcessTransport("bob-transport")

    InProcessTransport.register(aliceUhid, aliceTransport)
    InProcessTransport.register(bobUhid, bobTransport)

    val sendResult = aliceTransport.sendAsync(bobUhid, "Test message".toByteArray())
    println("Message sent from Alice to Bob: $sendResult\n")

    println("=== Demo Complete ===")
}

/**
 * Converts a byte array to a hex string.
 */
private fun toHex(bytes: ByteArray): String {
    return bytes.joinToString("") { "%02x".format(it) }
}
