// SPDX-License-Identifier: MIT

use aether_protocol::{
    protocol::{MeshPacket, PacketType},
    security::{Ed25519SigningService, PacketSigningService, SignalProtocolService},
    transport::InProcessTransport,
};

#[tokio::main]
async fn main() -> Result<(), Box<dyn std::error::Error>> {
    println!("=== Aether Protocol Demo ===\n");

    // Step 1: Create two nodes with identity keys
    println!("Step 1: Generating identity keys for Alice and Bob");
    let (alice_private, alice_public) = Ed25519SigningService::generate_keypair();
    let (bob_private, bob_public) = Ed25519SigningService::generate_keypair();
    println!("  Alice public key: {}", hex::encode(&alice_public[..8]));
    println!("  Bob public key:   {}\n", hex::encode(&bob_public[..8]));

    // Step 2: Create Signal Protocol services
    println!("Step 2: Initializing Signal Protocol services");
    let mut alice_signal = SignalProtocolService::new();
    let mut bob_signal = SignalProtocolService::new();
    println!("  Alice Signal service initialized");
    println!("  Bob Signal service initialized\n");

    // Step 3: Generate pre-key bundles
    println!("Step 3: Generating pre-key bundles");
    let alice_bundle = alice_signal.generate_pre_key_bundle("alice-node")?;
    let bob_bundle = bob_signal.generate_pre_key_bundle("bob-node")?;
    println!("  Alice bundle generated (signed pre-key ID: {})", bob_bundle.signed_pre_key_id);
    println!("  Bob bundle generated (signed pre-key ID: {})\n", bob_bundle.signed_pre_key_id);

    // Step 4: Establish sessions
    println!("Step 4: Establishing encrypted sessions");
    alice_signal.process_pre_key_bundle(&bob_bundle)?;
    bob_signal.process_pre_key_bundle(&alice_bundle)?;
    println!("  Alice established session with Bob");
    println!("  Bob established session with Alice\n");

    // Step 5: Exchange encrypted messages
    println!("Step 5: Exchanging encrypted messages");
    let message_1 = b"Hello from Alice!";
    let encrypted_1 = alice_signal.encrypt("bob-node", message_1)?;
    println!("  Alice encrypted message (counter: {})", encrypted_1.counter);

    let decrypted_1 = bob_signal.decrypt("alice-node", &encrypted_1)?;
    println!("  Bob decrypted: {}", String::from_utf8_lossy(&decrypted_1));

    let message_2 = b"Hello from Bob!";
    let encrypted_2 = bob_signal.encrypt("alice-node", message_2)?;
    println!("  Bob encrypted message (counter: {})", encrypted_2.counter);

    let decrypted_2 = alice_signal.decrypt("bob-node", &encrypted_2)?;
    println!("  Alice decrypted: {}\n", String::from_utf8_lossy(&decrypted_2));

    // Step 6: Create and sign packets
    println!("Step 6: Creating and signing mesh packets");
    let mut packet_signer = PacketSigningService::new();
    let mut packet = MeshPacket::new(PacketType::Data, "alice-node".to_string());
    packet.destination_uhid = "bob-node".to_string();
    packet.payload = b"Signed mesh packet".to_vec();

    packet_signer.sign_packet(&mut packet, &alice_private)?;
    println!("  Packet signed");
    println!("  Packet ID: {}", packet.id);
    println!("  Nonce: {}", hex::encode(&packet.packet_nonce));
    println!("  Signature: {}\n", hex::encode(&packet.signature[..8]));

    // Step 7: Verify packet
    println!("Step 7: Verifying packet signature");
    let mut packet_verifier = PacketSigningService::new();
    let is_valid = packet_verifier.verify_packet(&packet, &alice_public)?;
    println!("  Signature valid: {}\n", is_valid);

    // Step 8: Serialize and deserialize packet
    println!("Step 8: Serializing and deserializing packet");
    let serialized = aether_protocol::protocol::serializer::PacketSerializer::serialize(&packet)?;
    println!("  Serialized size: {} bytes", serialized.len());

    let deserialized = aether_protocol::protocol::serializer::PacketSerializer::deserialize(&serialized)?;
    println!("  Deserialized successfully");
    println!("  Payload matches: {}\n", deserialized.payload == packet.payload);

    // Step 9: In-process transport demo
    println!("Step 9: In-process transport demo");
    let mut alice_transport = InProcessTransport::new("alice-node".to_string());
    let mut bob_transport = InProcessTransport::new("bob-node".to_string());

    alice_transport.register()?;
    bob_transport.register()?;
    println!("  Alice and Bob registered in simulated network");

    let test_data = b"Transport test message";
    let sent = alice_transport.send_async("bob-node", test_data).await?;
    println!("  Alice sent to Bob: {}", sent);
    println!("  Bob is connected to Alice: {}\n", bob_transport.is_connected("alice-node"));

    println!("=== Demo Complete ===");

    Ok(())
}

mod hex {
    pub fn encode(data: &[u8]) -> String {
        data.iter()
            .map(|b| format!("{:02x}", b))
            .collect::<String>()
    }
}
