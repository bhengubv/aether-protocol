#!/usr/bin/env python3
"""
Demo: Aether Mesh Networking Protocol

Demonstrates:
1. Node creation and key generation
2. Pre-key bundle exchange
3. Session establishment via Signal Protocol
4. Message encryption and decryption
5. Packet serialization/deserialization
6. In-process transport communication
"""

import asyncio
import os
from typing import Optional

from aether.protocol.mesh_packet import MeshPacket, PacketType
from aether.protocol.serializer import PacketSerializer
from aether.security.ed25519_service import Ed25519SigningService
from aether.security.signal_protocol import SignalProtocolService
from aether.security.packet_signing import PacketSigningService
from aether.transport.in_process import InProcessTransport
from aether.models import AetherNode


# ANSI color codes for pretty output
class Colors:
    HEADER = "\033[95m"
    OKBLUE = "\033[94m"
    OKCYAN = "\033[96m"
    OKGREEN = "\033[92m"
    WARNING = "\033[93m"
    FAIL = "\033[91m"
    ENDC = "\033[0m"
    BOLD = "\033[1m"
    UNDERLINE = "\033[4m"


def print_header(text: str) -> None:
    """Print a styled header."""
    print(f"\n{Colors.HEADER}{Colors.BOLD}{'=' * 70}{Colors.ENDC}")
    print(f"{Colors.HEADER}{Colors.BOLD}{text:^70}{Colors.ENDC}")
    print(f"{Colors.HEADER}{Colors.BOLD}{'=' * 70}{Colors.ENDC}\n")


def print_section(text: str) -> None:
    """Print a styled section."""
    print(f"{Colors.OKBLUE}{Colors.BOLD}>>> {text}{Colors.ENDC}")


def print_success(text: str) -> None:
    """Print a success message."""
    print(f"{Colors.OKGREEN}✓ {text}{Colors.ENDC}")


def print_error(text: str) -> None:
    """Print an error message."""
    print(f"{Colors.FAIL}✗ {text}{Colors.ENDC}")


def print_info(text: str) -> None:
    """Print an info message."""
    print(f"{Colors.OKCYAN}• {text}{Colors.ENDC}")


def create_node(uhid: str) -> AetherNode:
    """
    Create an Aether node with generated Ed25519 keys.

    Args:
        uhid: The node's Universal Hardware Identifier.

    Returns:
        An initialized AetherNode.
    """
    private_key, public_key = Ed25519SigningService.generate_keypair()
    node = AetherNode(
        uhid=uhid,
        private_key=private_key,
        public_key=public_key,
        capabilities=0b00001111,  # Relay, Sos, WifiDirect, Ble
    )
    return node


def format_key(key: bytes, max_len: int = 16) -> str:
    """Format a key as hex string for display."""
    hex_str = key.hex()
    if len(hex_str) > max_len * 2:
        return hex_str[: max_len * 2] + "..."
    return hex_str


async def demo_basic_crypto() -> None:
    """Demo 1: Basic cryptography operations."""
    print_header("Demo 1: Basic Cryptography")

    print_section("1.1: Ed25519 Key Generation")
    private_key, public_key = Ed25519SigningService.generate_keypair()
    print_success(f"Generated Ed25519 key pair")
    print_info(f"Private key: {format_key(private_key)} ({len(private_key)} bytes)")
    print_info(f"Public key:  {format_key(public_key)} ({len(public_key)} bytes)")

    print_section("1.2: Signing and Verification")
    message = b"Hello, Aether Mesh!"
    signature = Ed25519SigningService.sign(private_key, message)
    print_success(f"Signed message: '{message.decode()}'")
    print_info(f"Signature: {format_key(signature)} ({len(signature)} bytes)")

    is_valid = Ed25519SigningService.verify(public_key, message, signature)
    if is_valid:
        print_success("Signature verification passed!")
    else:
        print_error("Signature verification failed!")

    # Try with wrong message
    wrong_message = b"Tampered message"
    is_valid = Ed25519SigningService.verify(public_key, wrong_message, signature)
    if not is_valid:
        print_success("Correctly rejected tampered message")
    else:
        print_error("Failed to reject tampered message!")


async def demo_node_creation() -> None:
    """Demo 2: Node creation and initialization."""
    print_header("Demo 2: Node Creation and Initialization")

    print_section("2.1: Creating Alice's node")
    alice = create_node("alice-device-001")
    print_success(f"Created node: {alice.uhid}")
    print_info(f"Public key: {format_key(alice.public_key)}")
    print_info(f"Capabilities: {bin(alice.capabilities)} (Ble, WifiDirect, Relay, Sos)")

    print_section("2.2: Creating Bob's node")
    bob = create_node("bob-device-002")
    print_success(f"Created node: {bob.uhid}")
    print_info(f"Public key: {format_key(bob.public_key)}")

    return alice, bob


async def demo_signal_protocol(alice: AetherNode, bob: AetherNode) -> None:
    """Demo 3: Signal Protocol key exchange and session establishment."""
    print_header("Demo 3: Signal Protocol - X3DH Key Exchange")

    print_section("3.1: Alice initializes Signal Protocol")
    alice_signal = SignalProtocolService()
    print_success("Alice's Signal Protocol instance created")

    print_section("3.2: Bob generates and publishes pre-key bundle")
    bob_signal = SignalProtocolService()
    bob_bundle = await bob_signal.generate_pre_key_bundle(bob.uhid)
    print_success(f"Bob's pre-key bundle generated")
    print_info(f"Identity key: {format_key(bob_bundle.identity_key)}")
    print_info(f"Signed pre-key: {format_key(bob_bundle.signed_pre_key)}")
    print_info(f"Pre-key ID: {bob_bundle.pre_key_id}")
    print_info(f"Signed pre-key ID: {bob_bundle.signed_pre_key_id}")

    print_section("3.3: Alice processes Bob's bundle and establishes session")
    try:
        await alice_signal.process_pre_key_bundle(bob_bundle)
        print_success("Alice established session with Bob via X3DH")
    except Exception as e:
        print_error(f"Failed to establish session: {e}")
        return

    print_info("Session established with:")
    print_info("  • 256-bit root key (via HKDF-SHA256)")
    print_info("  • Send chain key for forward secrecy")
    print_info("  • Receive chain key for forward secrecy")
    print_info("  • Max 1000 skipped message keys for out-of-order delivery")

    return alice_signal, bob_signal


async def demo_encryption() -> None:
    """Demo 4: Message encryption and decryption."""
    print_header("Demo 4: Message Encryption & Decryption")

    # Setup signal protocols and sessions
    alice_signal = SignalProtocolService()
    bob_signal = SignalProtocolService()

    # Exchange bundles
    alice_bundle = await alice_signal.generate_pre_key_bundle("alice-001")
    bob_bundle = await bob_signal.generate_pre_key_bundle("bob-001")

    # Establish sessions
    await alice_signal.process_pre_key_bundle(bob_bundle)
    await bob_signal.process_pre_key_bundle(alice_bundle)

    print_section("4.1: Alice encrypts a message to Bob")
    plaintext = b"This is a secret message from Alice to Bob!"
    encrypted = await alice_signal.encrypt("bob-001", plaintext)
    print_success(f"Message encrypted: '{plaintext.decode()}'")
    print_info(f"Ciphertext length: {len(encrypted.ciphertext)} bytes")
    print_info(f"Nonce: {format_key(encrypted.nonce)} ({len(encrypted.nonce)} bytes)")
    print_info(f"Counter: {encrypted.counter}")

    print_section("4.2: Bob decrypts the message")
    try:
        decrypted = await bob_signal.decrypt("alice-001", encrypted)
        print_success(f"Message decrypted: '{decrypted.decode()}'")

        if decrypted == plaintext:
            print_success("Decrypted message matches original!")
        else:
            print_error("Decrypted message does not match!")
    except Exception as e:
        print_error(f"Decryption failed: {e}")


async def demo_packet_serialization() -> None:
    """Demo 5: Packet serialization and deserialization."""
    print_header("Demo 5: Packet Serialization & Wire Format")

    print_section("5.1: Creating a MeshPacket")
    packet = MeshPacket(
        type=PacketType.Data,
        source_uhid="node-alice-001",
        destination_uhid="node-bob-002",
        ttl=7,
        priority=0,
        payload=b"Hello from Alice!",
    )
    print_success(f"Created packet: {packet}")
    print_info(f"Packet ID: {packet.id}")
    print_info(f"Payload: {packet.payload}")

    print_section("5.2: Serializing to binary wire format")
    serialized = PacketSerializer.serialize(packet)
    print_success(f"Packet serialized to {len(serialized)} bytes")
    print_info(f"Binary: {serialized[:32].hex()}... (first 32 bytes)")

    print_section("5.3: Deserializing from wire format")
    try:
        deserialized = PacketSerializer.deserialize(serialized)
        print_success("Packet deserialized successfully")
        print_info(f"Type: {deserialized.type.name}")
        print_info(f"Source: {deserialized.source_uhid}")
        print_info(f"Destination: {deserialized.destination_uhid}")
        print_info(f"Payload: {deserialized.payload}")

        if deserialized.id == packet.id and deserialized.payload == packet.payload:
            print_success("Deserialized packet matches original!")
        else:
            print_error("Deserialized packet mismatch!")
    except Exception as e:
        print_error(f"Deserialization failed: {e}")


async def demo_packet_signing() -> None:
    """Demo 6: Packet signing and verification."""
    print_header("Demo 6: Packet Signing & Verification")

    # Create a node
    alice = create_node("alice-001")

    print_section("6.1: Creating and signing a packet")
    packet = MeshPacket(
        type=PacketType.Data,
        source_uhid=alice.uhid,
        destination_uhid="bob-001",
        ttl=7,
        priority=0,
        payload=b"Signed message from Alice",
    )
    print_info(f"Packet ID: {packet.id}")

    # Sign the packet
    signing_service = PacketSigningService()
    signing_service.sign_packet(packet, alice.private_key)
    print_success(f"Packet signed with Ed25519")
    print_info(f"Signature: {format_key(packet.signature)}")

    print_section("6.2: Verifying the packet signature")
    is_valid = signing_service.verify_packet(packet, alice.public_key)
    if is_valid:
        print_success("Signature verification passed!")
    else:
        print_error("Signature verification failed!")

    print_section("6.3: Replay attack detection")
    # Try to verify the same packet again (should be detected as replay)
    is_valid_2 = signing_service.verify_packet(packet, alice.public_key)
    if not is_valid_2:
        print_success("Correctly detected replay attack (duplicate nonce)!")
    else:
        print_error("Failed to detect replay attack!")


async def demo_in_process_transport() -> None:
    """Demo 7: In-process transport communication."""
    print_header("Demo 7: In-Process Transport Communication")

    print_section("7.1: Creating transport instances for Alice and Bob")
    alice_transport = InProcessTransport("alice-001")
    bob_transport = InProcessTransport("bob-001")
    print_success(f"Created transports for Alice and Bob")
    print_info(f"Transport name: {alice_transport.name}")
    print_info(f"Max bandwidth: {alice_transport.max_bandwidth_bps / 1e9:.1f} Gbps")
    print_info(f"Max range: {alice_transport.max_range_meters} meters")

    print_section("7.2: Alice sends a message to Bob")
    message = b"Hello from Alice via in-process transport!"

    # Register callback on Bob's transport
    received_messages = []

    def on_message_received(sender: str, data: bytes) -> None:
        received_messages.append((sender, data))
        print_info(f"Bob received from {sender}: {data.decode()}")

    bob_transport.on_data_received(on_message_received)

    # Send message
    success = await alice_transport.send_async("bob-001", message)
    print_success(f"Message sent: {success}")

    # Give callback time to execute
    await asyncio.sleep(0.1)

    if received_messages:
        sender, received_data = received_messages[0]
        if received_data == message:
            print_success("Message received and verified!")
    else:
        print_error("Message was not received!")

    print_section("7.3: Cleaning up")
    alice_transport.shutdown()
    bob_transport.shutdown()
    print_success("Transports shut down")


async def demo_end_to_end() -> None:
    """Demo 8: Complete end-to-end message flow."""
    print_header("Demo 8: End-to-End Message Flow")

    # Create nodes
    alice = create_node("alice-e2e")
    bob = create_node("bob-e2e")

    # Create transports
    alice_transport = InProcessTransport(alice.uhid)
    bob_transport = InProcessTransport(bob.uhid)

    # Setup Signal Protocol
    alice_signal = SignalProtocolService()
    bob_signal = SignalProtocolService()

    # Exchange pre-key bundles
    print_section("8.1: Key exchange phase")
    alice_bundle = await alice_signal.generate_pre_key_bundle(alice.uhid)
    bob_bundle = await bob_signal.generate_pre_key_bundle(bob.uhid)

    await alice_signal.process_pre_key_bundle(bob_bundle)
    await bob_signal.process_pre_key_bundle(alice_bundle)
    print_success("Session established between Alice and Bob")

    # Create message
    print_section("8.2: Message composition and encryption")
    plaintext = b"Secret meeting at 10 AM tomorrow"
    encrypted_payload = await alice_signal.encrypt(bob.uhid, plaintext)
    print_success(f"Message encrypted: '{plaintext.decode()}'")

    # Create and sign packet
    packet = MeshPacket(
        type=PacketType.Data,
        source_uhid=alice.uhid,
        destination_uhid=bob.uhid,
        ttl=7,
        priority=0,
        payload=encrypted_payload.ciphertext,
    )

    signing_service = PacketSigningService()
    signing_service.sign_packet(packet, alice.private_key)
    print_success("Packet signed")

    # Serialize packet
    serialized = PacketSerializer.serialize(packet)
    print_success(f"Packet serialized: {len(serialized)} bytes")

    # Send via transport
    print_section("8.3: Transmission over transport")

    received_packets = []

    def on_data(sender: str, data: bytes) -> None:
        try:
            pkt = PacketSerializer.deserialize(data)
            received_packets.append(pkt)
        except Exception as e:
            print_error(f"Failed to deserialize: {e}")

    bob_transport.on_data_received(on_data)

    success = await alice_transport.send_async(bob.uhid, serialized)
    await asyncio.sleep(0.1)  # Let callback execute

    if success and received_packets:
        print_success("Packet transmitted and received by Bob")

        # Bob verifies and decrypts
        print_section("8.4: Reception and decryption")
        rcvd_packet = received_packets[0]

        # Verify signature
        is_valid = signing_service.verify_packet(rcvd_packet, alice.public_key)
        if is_valid:
            print_success("Packet signature verified")
        else:
            print_error("Signature verification failed")
            return

        # Decrypt
        new_encrypted = EncryptedPayload(
            ciphertext=rcvd_packet.payload,
            nonce=encrypted_payload.nonce,
            message_type=2,
            sender_uhid=alice.uhid,
            counter=encrypted_payload.counter,
        )

        try:
            decrypted = await bob_signal.decrypt(alice.uhid, new_encrypted)
            print_success(f"Message decrypted: '{decrypted.decode()}'")

            if decrypted == plaintext:
                print_success("End-to-end flow complete and verified!")
        except Exception as e:
            print_error(f"Decryption failed: {e}")
    else:
        print_error("Failed to transmit packet")

    # Cleanup
    alice_transport.shutdown()
    bob_transport.shutdown()


# Import after class definitions
from aether.security.signal_protocol import EncryptedPayload


async def main() -> None:
    """Run all demos."""
    print_header("Aether Mesh Networking Protocol - Python Implementation")
    print_info("Demonstrating wire-compatible X3DH key exchange, Signal Protocol,")
    print_info("and packet serialization matching the C# reference implementation")

    try:
        # Demo 1: Basic crypto
        await demo_basic_crypto()

        # Demo 2: Node creation
        alice, bob = await demo_node_creation()

        # Demo 3: Signal Protocol
        await demo_signal_protocol(alice, bob)

        # Demo 4: Encryption
        await demo_encryption()

        # Demo 5: Packet serialization
        await demo_packet_serialization()

        # Demo 6: Packet signing
        await demo_packet_signing()

        # Demo 7: Transport
        await demo_in_process_transport()

        # Demo 8: End-to-end
        await demo_end_to_end()

        # Final summary
        print_header("Demo Complete!")
        print_success("All Aether protocol demonstrations completed successfully")
        print_info("Features demonstrated:")
        print_info("  • Ed25519 key generation, signing, and verification")
        print_info("  • Signal Protocol X3DH key exchange")
        print_info("  • AES-256-GCM encryption with per-message keys")
        print_info("  • HKDF-SHA256 key derivation")
        print_info("  • Little-endian binary packet serialization")
        print_info("  • Packet signing and replay attack detection")
        print_info("  • In-memory mesh transport simulation")
        print_info("  • Complete end-to-end encryption workflow")

    except Exception as e:
        print_error(f"Demo failed: {e}")
        import traceback

        traceback.print_exc()


if __name__ == "__main__":
    asyncio.run(main())
