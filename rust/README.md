# Aether Protocol — Rust Implementation

[English](README.md) · [Français](../docs/i18n/fr/rust/README.md) · [Español](../docs/i18n/es/rust/README.md) · [العربية](../docs/i18n/ar/rust/README.md) · [中文简体](../docs/i18n/zh-CN/rust/README.md) · [日本語](../docs/i18n/ja/rust/README.md) · [Deutsch](../docs/i18n/de/rust/README.md) · [Português (BR)](../docs/i18n/pt-BR/rust/README.md) · [Русский](../docs/i18n/ru/rust/README.md) · [فارسی](../docs/i18n/fa/rust/README.md) · [한국어](../docs/i18n/ko/rust/README.md)

Rust implementation of the Aether mesh networking protocol's protocol/crypto/serialization layer, featuring wire-format compatibility with the C# reference implementation (verified against the shared fixture corpus). Note: the mesh *transport* in this port is an in-process simulator (plus a written-but-unverified WebRTC internet transport) — there is no real BLE/Wi-Fi Direct radio.

## Overview

This crate provides:

- **MeshPacket serialization/deserialization** — Binary wire format matching C# PacketSerializer exactly
- **Ed25519 signing** — Identity key generation, signing, and verification
- **Signal Protocol** — X3DH-based key agreement with symmetric ratchet for forward secrecy
- **Packet signing service** — Nonce deduplication and freshness checks
- **In-process transport** — Simulated mesh network for testing and demos (an in-process simulator; there is no real BLE/Wi-Fi Direct radio in the Rust port)
- **WebRTC transport** — Real internet peer-to-peer data-channel transport in `src/transport/webrtc.rs`. **Status: written, but NOT yet verified (built/tested) on the dev box.** Treat as unproven until the Rust WebRTC tests are run green

## Project Structure

```
rust/
├── Cargo.toml                          # Crate manifest
├── src/
│   ├── lib.rs                          # Module declarations
│   ├── main.rs                         # Demo application
│   ├── constants.rs                    # Protocol constants
│   ├── models.rs                       # Core data structures
│   ├── protocol/
│   │   ├── mod.rs                      # MeshPacket, PacketType enum
│   │   └── serializer.rs               # Binary serialization (wire-compatible)
│   ├── security/
│   │   ├── mod.rs                      # Module declarations
│   │   ├── ed25519.rs                  # Ed25519 signing service
│   │   ├── signal_protocol.rs          # Signal Protocol implementation
│   │   └── packet_signing.rs           # Packet signing + nonce dedup
│   └── transport/
│       ├── mod.rs                      # TransportService trait
│       └── in_process.rs               # In-memory transport implementation
```

## Key Features

### 1. Wire Format Compatibility

The `PacketSerializer` produces byte-for-byte identical output to the C# implementation:

```
[1 byte]  Protocol version
[1 byte]  Packet type
[16 bytes] Packet ID (GUID)
[1 byte]  Priority
[4 bytes] TTL (int32, LE)
[8 bytes] TimestampMs (int64, LE)
[2 bytes] SourceUhid length (u16, LE)
[N bytes] SourceUhid (UTF-8)
[2 bytes] DestinationUhid length (u16, LE)
[N bytes] DestinationUhid (UTF-8)
[2 bytes] PacketNonce length (u16, LE)
[N bytes] PacketNonce
[4 bytes] Payload length (i32, LE)
[N bytes] Payload
[2 bytes] Signature length (u16, LE)
[N bytes] Signature
```

All multi-byte integers use little-endian byte order. String lengths are prefixed with u16 (SourceUhid, DestinationUhid) or i32 (Payload, Signature) as specified in the protocol spec.

### 2. Packet Types

All 26 packet types from the protocol specification are defined:

- RouteRequest (1), RouteReply (2), Data (3), Ack (4)
- SosBroadcast (5), SosAck (6)
- ChannelMessage (7)
- ChunkRequest (8), ChunkData (9)
- Heartbeat (10)
- StreamAnnounce (11), StreamSegment (12), StreamSubscribe (13), StreamUnsubscribe (14)
- VoicePtt (15), VoiceCall (16), VoiceSignaling (17)
- DtnBundle (18), DtnCustodyAck (19), DtnDeliveryReceipt (20)
- PresenceBeacon (21), PresenceQuery (22), ProfileSync (23)
- TipPacket (24), PreKeyRequest (25), PreKeyResponse (26)

### 3. Ed25519 Signing

- 32-byte private keys (seed), 32-byte public keys, 64-byte signatures
- Uses `ed25519-dalek` for cryptographic operations
- Secure key zeroing after use

### 4. Signal Protocol

X3DH-based key agreement with symmetric ratchet:

- **Key agreement:** ECDH P-256 using ephemeral + signed pre-keys
- **Key derivation:** HKDF-SHA256 with unique info strings
  - `aether-root-v1` — Root key
  - `aether-chain-send-v1` — Sending chain key
  - `aether-chain-recv-v1` — Receiving chain key
- **Encryption:** AES-256-GCM (12-byte nonce, 16-byte tag)
- **Ratchet:** Symmetric chain key advancement with counter-based message keys
- **Out-of-order handling:** Up to 1,000 skipped message keys cached

### 5. Packet Signing Service

- Random 8-byte nonce generation
- Millisecond-precision timestamps
- Freshness validation (5-minute window)
- Nonce deduplication per sender (prevents replays)
- Automatic cleanup of expired entries

### 6. In-Process Transport

Simulated mesh network for testing:

- Static registry of nodes using concurrent HashMap
- Fire-and-forget message delivery
- Bidirectional peer connectivity checks
- Suitable for demos and unit tests

## Usage

### Basic Key Generation and Signing

```rust
use aethernet_protocol::security::Ed25519SigningService;

let (private_key, public_key) = Ed25519SigningService::generate_keypair();

let message = b"test";
let signature = Ed25519SigningService::sign(&private_key, message)?;

assert!(Ed25519SigningService::verify(&public_key, message, &signature));
```

### Signal Protocol Session

```rust
use aethernet_protocol::security::SignalProtocolService;

let mut alice = SignalProtocolService::new();
let mut bob = SignalProtocolService::new();

// Bob publishes pre-key bundle
let bob_bundle = bob.generate_pre_key_bundle("bob-node")?;

// Alice processes bundle and establishes session
alice.process_pre_key_bundle(&bob_bundle)?;

// Alice encrypts message
let plaintext = b"Hello!";
let encrypted = alice.encrypt("bob-node", plaintext)?;

// Bob decrypts
let alice_bundle = alice.generate_pre_key_bundle("alice-node")?;
bob.process_pre_key_bundle(&alice_bundle)?;
let decrypted = bob.decrypt("alice-node", &encrypted)?;

assert_eq!(decrypted, plaintext);
```

### Packet Serialization

```rust
use aethernet_protocol::protocol::{MeshPacket, PacketType};
use aethernet_protocol::protocol::serializer::PacketSerializer;

let mut packet = MeshPacket::new(PacketType::Data, "alice".to_string());
packet.destination_uhid = "bob".to_string();
packet.payload = b"test".to_vec();

let serialized = PacketSerializer::serialize(&packet)?;
let deserialized = PacketSerializer::deserialize(&serialized)?;

assert_eq!(deserialized.source_uhid, "alice");
```

### Packet Signing

```rust
use aethernet_protocol::security::PacketSigningService;
use aethernet_protocol::protocol::MeshPacket;

let mut signer = PacketSigningService::new();
let (private_key, public_key) = Ed25519SigningService::generate_keypair();

let mut packet = MeshPacket::new(PacketType::Data, "sender".to_string());
signer.sign_packet(&mut packet, &private_key)?;

let mut verifier = PacketSigningService::new();
let is_valid = verifier.verify_packet(&packet, &public_key)?;
assert!(is_valid);
```

### In-Process Transport

```rust
use aethernet_protocol::transport::InProcessTransport;

let mut node_a = InProcessTransport::new("node-a".to_string());
let mut node_b = InProcessTransport::new("node-b".to_string());

node_a.register()?;
node_b.register()?;

node_a.send_async("node-b", b"Hello").await?;
assert!(node_b.is_connected("node-a"));
```

## Running the Demo

```bash
cargo run --release
```

The demo performs the following steps:

1. Generates identity keys for Alice and Bob
2. Initializes Signal Protocol services
3. Generates and exchanges pre-key bundles
4. Establishes encrypted sessions
5. Exchanges encrypted messages
6. Creates and signs mesh packets
7. Verifies packet signatures
8. Serializes and deserializes packets
9. Demonstrates in-process transport

## Constants

All protocol constants are defined in `src/constants.rs`, matching the C# specification:

- Routing: DefaultTtl=7, SosTtl=15, RouteTimeoutMs=5000
- Security: MaxPacketAgeSeconds=300, MaxSkippedKeys=1000
- Transport: BleMaxPayloadBytes=1024, WifiDirectTimeoutMs=10000
- DTN: DtnBundleTtlHours=72, DtnMaxCopies=3
- Voice/Stream: Various bitrate and buffer configurations

## Dependencies

- `ed25519-dalek` — Ed25519 signing
- `x25519-dalek` — X25519 key agreement
- `aes-gcm` — AES-256-GCM encryption
- `hkdf` — HKDF key derivation
- `sha2` — SHA-256 hashing
- `hmac` — HMAC operations
- `rand` — Random number generation
- `uuid` — GUID generation and serialization
- `serde` + `serde_json` — Serialization
- `tokio` — Async runtime
- `async-trait` — Async trait methods

## Testing

Run all tests:

```bash
cargo test
```

Tests cover:

- Packet creation and TTL management
- Packet type conversion
- Serialization/deserialization roundtrips
- Ed25519 key generation and signature verification
- Signal Protocol session establishment and encryption
- Packet signing and freshness validation
- In-process transport connectivity

## Protocol Compliance

This implementation follows the Aether protocol specification (Version 2.0) with:

- ✅ Binary wire format (little-endian, length-prefixed)
- ✅ All 26 packet types
- ✅ Ed25519 signing with nonce deduplication
- ✅ X3DH key agreement with HKDF-SHA256
- ✅ AES-256-GCM encryption with 12-byte nonce
- ✅ Symmetric ratchet with out-of-order handling
- ✅ Pre-key bundle generation and processing
- ✅ Packet signable data construction (SHA-256 payload hash)
- ✅ Transport trait abstraction

## Notes

- The wire format uses little-endian byte order throughout (matching C# BinaryPrimitives.WriteInt32LittleEndian)
- String length prefixes use u16 for UHIDs, i32 for payload/signature (matching C# WriteUInt16/WriteInt32)
- All cryptographic key material is zeroed after use via `CryptographicOperations` equivalent
- The Signal Protocol implementation uses HKDF with salt bytes [0x01] and [0x02] for chain ratcheting (matching C# HKDF usage)
- Nonce deduplication uses a per-sender VecDeque with automatic cleanup of entries older than 5 minutes
