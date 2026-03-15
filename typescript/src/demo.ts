/**
 * Aether Protocol Demo
 * Creates 2 nodes, generates Ed25519 keys, establishes Signal session,
 * exchanges encrypted messages
 * SPDX-License-Identifier: MIT
 */

import {
  MeshPacket,
  PacketType,
  PacketSerializer,
  Ed25519Service,
  SignalProtocol,
  InProcessTransport,
  signPacket,
  verifyPacket,
} from "./index.js";

async function main() {
  console.log("=== Aether Mesh Protocol Demo ===\n");

  // Step 1: Create two nodes with in-process transport
  console.log("Step 1: Creating nodes...");
  const nodeA = new InProcessTransport("node-alpha-001");
  const nodeB = new InProcessTransport("node-beta-002");
  console.log(
    `Created ${InProcessTransport.activeNodeCount} nodes in simulated network\n`
  );

  // Step 2: Generate Ed25519 key pairs
  console.log("Step 2: Generating Ed25519 key pairs...");
  const keyPairA = Ed25519Service.generateKeyPair();
  const keyPairB = Ed25519Service.generateKeyPair();
  console.log(
    `Node A public key: ${Buffer.from(keyPairA.publicKey).toString("hex").substring(0, 16)}...`
  );
  console.log(
    `Node B public key: ${Buffer.from(keyPairB.publicKey).toString("hex").substring(0, 16)}...\n`
  );

  // Step 3: Establish Signal protocol sessions
  console.log("Step 3: Establishing Signal protocol sessions...");
  const signalA = new SignalProtocol();
  const signalB = new SignalProtocol();

  // Generate pre-key bundles
  const bundleA = await signalA.generatePreKeyBundle("node-alpha-001");
  const bundleB = await signalB.generatePreKeyBundle("node-beta-002");
  console.log(`Generated pre-key bundles for both nodes\n`);

  // Process bundles to establish sessions
  await signalA.processPreKeyBundle(bundleB);
  await signalB.processPreKeyBundle(bundleA);
  console.log(`Established Signal sessions between nodes\n`);

  // Step 4: Create and sign a packet
  console.log("Step 4: Creating and signing packet...");
  const packet = MeshPacket.create(PacketType.Data, "node-alpha-001");
  packet.destinationUhid = "node-beta-002";
  packet.ttl = 7;
  packet.priority = 0;
  packet.payload = new TextEncoder().encode("Hello from Node A!");

  signPacket(packet, keyPairA.privateKey);
  console.log(`Created packet: ${packet.toString()}`);
  console.log(
    `Signature: ${Buffer.from(packet.signature).toString("hex").substring(0, 32)}...\n`
  );

  // Step 5: Verify packet signature (immediately after signing)
  console.log("Step 5: Verifying packet signature...");
  console.log(`  Nonce length: ${packet.packetNonce.length} bytes`);
  console.log(`  Nonce: ${Buffer.from(packet.packetNonce).toString("hex")}`);
  console.log(`  Timestamp: ${packet.timestampMs}`);
  console.log(`  Signature length: ${packet.signature.length} bytes`);
  const isValid = verifyPacket(packet, keyPairA.publicKey);
  console.log(`  Signature valid: ${isValid}`);
  console.log(
    `  (Signature freshness: timestamp must be within 5 minutes)\n`
  );

  // Step 6: Serialize packet
  console.log("Step 6: Serializing packet to binary...");
  const serialized = PacketSerializer.serialize(packet);
  console.log(
    `Serialized size: ${serialized.length} bytes\n`
  );

  // Step 7: Deserialize packet
  console.log("Step 7: Deserializing packet from binary...");
  const deserialized = PacketSerializer.deserialize(serialized);
  console.log(`Deserialized: ${deserialized.toString()}`);
  console.log(
    `Payload: ${new TextDecoder().decode(deserialized.payload)}\n`
  );

  // Step 8: Encrypt a message
  console.log("Step 8: Encrypting message with Signal session...");
  const plaintext = new TextEncoder().encode(
    "Secret message: Meet me at coordinates -33.9249, 18.4241"
  );

  try {
    const encrypted = await signalA.encrypt("node-beta-002", plaintext);
    console.log(`Plaintext: ${new TextDecoder().decode(plaintext)}`);
    console.log(
      `Encrypted: ${Buffer.from(encrypted.ciphertext).toString("hex").substring(0, 32)}...`
    );
    console.log(`Nonce: ${Buffer.from(encrypted.nonce).toString("hex")}`);
    console.log(`Counter: ${encrypted.counter}\n`);

    // Step 9: Decrypt message
    console.log("Step 9: Decrypting message on Node B...");
    const decrypted = await signalB.decrypt("node-alpha-001", encrypted);
    const decryptedText = new TextDecoder().decode(decrypted);
    console.log(`Decrypted: ${decryptedText}\n`);
  } catch (error: any) {
    console.error(`Error in Signal operations: ${error.message}`);
    console.log(`\nNote: Signal Protocol in demo uses deterministic key derivation`);
    console.log(`In production, use proper X3DH with ECDH for key agreement\n`);
  }

  // Step 10: Send packet through transport
  console.log("Step 10: Sending packet through transport...");
  let receivedData: Uint8Array | null = null;
  nodeB.onDataReceived = (sender, data) => {
    console.log(
      `[Node B] Received ${data.length} bytes from ${sender}`
    );
    receivedData = data;
  };

  const success = await nodeA.sendAsync("node-beta-002", serialized);
  console.log(`Send result: ${success}\n`);

  // Step 11: Verify round-trip
  console.log("Step 11: Verifying round-trip...");
  if (receivedData) {
    const roundTrip = PacketSerializer.deserialize(receivedData);
    console.log(`Round-trip packet: ${roundTrip.toString()}`);
    console.log(
      `Payload preserved: ${new TextDecoder().decode(roundTrip.payload) === new TextDecoder().decode(packet.payload)}`
    );
    console.log(
      `Signature present: ${roundTrip.signature.length === 64 ? "✓ (64 bytes)" : "✗"}`
    );
  }

  console.log("\n=== Demo Complete ===\n");
  console.log("Features Demonstrated:");
  console.log("  ✓ MeshPacket creation and serialization");
  console.log("  ✓ Ed25519 key generation and signing");
  console.log("  ✓ Packet signing with 8-byte random nonce");
  console.log("  ✓ Wire-format serialization (C# compatible)");
  console.log("  ✓ In-process transport network simulation");
  console.log("  ✓ Pre-key bundle generation");
  console.log("  ✓ Signal protocol session establishment\n");

  // Cleanup
  nodeA.dispose();
  nodeB.dispose();
  console.log(`\nActive nodes: ${InProcessTransport.activeNodeCount}`);
}

main().catch(console.error);
