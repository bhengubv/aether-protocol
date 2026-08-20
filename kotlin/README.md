# Aether Protocol - Kotlin Implementation

[English](README.md) · [Français](../docs/i18n/fr/kotlin/README.md) · [Español](../docs/i18n/es/kotlin/README.md) · [العربية](../docs/i18n/ar/kotlin/README.md) · [中文简体](../docs/i18n/zh-CN/kotlin/README.md) · [日本語](../docs/i18n/ja/kotlin/README.md) · [Deutsch](../docs/i18n/de/kotlin/README.md) · [Português (BR)](../docs/i18n/pt-BR/kotlin/README.md) · [Русский](../docs/i18n/ru/kotlin/README.md) · [فارسی](../docs/i18n/fa/kotlin/README.md) · [한국어](../docs/i18n/ko/kotlin/README.md)

A Kotlin implementation of the Aether mesh networking protocol, with cross-language wire-format compatibility with the C# reference implementation (verified against the shared fixture corpus).

## Overview

Aether is a decentralised mesh networking protocol for environments with intermittent or absent internet connectivity. This Kotlin implementation provides:

- **Wire-format compatibility** with C# (binary packet serialization matches exactly)
- **Ed25519 signing** for packet authentication and integrity
- **Signal Protocol** for end-to-end encryption (X3DH key agreement, symmetric ratchet, AES-256-GCM)
- **ECDH P-256** key agreement for session establishment
- **Packet serialization/deserialization** with little-endian multi-byte integers
- **Replay protection** using nonce deduplication
- **Transport abstraction** with an in-process simulator (BLE and Wi-Fi Direct are *interface slots* here — real BLE/Wi-Fi Direct adapters exist only in the C#/Windows + Android stacks)
- **WebRTC transport** — real internet peer-to-peer data-channel transport in `src/main/kotlin/aethernet/transport/webrtc/`. **Built and tested green on this Kotlin port** — it is the one real (non-simulated) transport here

## Project Structure

```
.
├── build.gradle.kts                          # Gradle build configuration (JDK 17, BouncyCastle)
├── settings.gradle.kts                       # Gradle settings
├── src/main/kotlin/
│   └── aether/
│       ├── Constants.kt                      # Protocol constants (TTL, timeouts, HKDF info strings)
│       ├── Demo.kt                           # Demo application (key generation, encryption, signing)
│       ├── models/
│       │   └── Models.kt                     # Domain models (AetherNetNode, PeerInfo, DtnBundle, etc.)
│       ├── protocol/
│       │   ├── MeshPacket.kt                 # Packet data class (wire-compatible with C#)
│       │   ├── PacketType.kt                 # Packet type enum (23 types, matching C# values)
│       │   └── PacketSerializer.kt           # Binary serializer (little-endian wire format)
│       ├── security/
│       │   ├── Ed25519Service.kt             # Ed25519 key generation, signing, verification
│       │   ├── SignalProtocol.kt             # X3DH + symmetric ratchet + AES-256-GCM
│       │   └── PacketSigning.kt              # Packet signing with replay protection
│       └── transport/
│           ├── TransportService.kt           # Transport interface (abstraction)
│           └── InProcessTransport.kt         # In-memory reference transport
└── README.md                                 # This file
```

## Building

### Prerequisites

- JDK 17 or higher
- Gradle 8.0 or higher

### Compile

```bash
cd /Users/admin/Code/Dev/aether-protocol/kotlin
./gradlew build
```

### Run Demo

```bash
./gradlew run
```

The demo demonstrates:
1. Ed25519 key pair generation
2. Pre-key bundle creation and exchange
3. Signal Protocol session establishment
4. Packet signing with Ed25519
5. Packet serialization/deserialization
6. Message encryption and decryption
7. Replay protection
8. In-process transport messaging

## Key Components

### 1. Packet Serialization (`PacketSerializer`)

Wire format (little-endian):
- Protocol version (1 byte)
- Packet type (1 byte)
- Packet ID / UUID (16 bytes)
- Priority (1 byte)
- TTL (4 bytes, int32)
- TimestampMs (8 bytes, int64)
- SourceUhid (2-byte length prefix + UTF-8 bytes)
- DestinationUhid (2-byte length prefix + UTF-8 bytes)
- PacketNonce (2-byte length prefix + bytes)
- Payload (4-byte length prefix + bytes)
- Signature (2-byte length prefix + bytes)

Fully compatible with C# `PacketSerializer`.

### 2. Ed25519 Signing (`Ed25519Service`, `PacketSigning`)

- **Key generation**: 32-byte private key seed, 32-byte public key
- **Signing**: 64-byte signatures over deterministic signable data
- **Verification**: Replaces P-256 ECDSA during migration period
- **Signable data format**: Matches C# spec exactly (packet nonce, timestamp, type, UHIDs, payload hash, TTL, priority)
- **Replay protection**: Nonce deduplication with 5-minute TTL

### 3. Signal Protocol (`SignalProtocol`)

Implements X3DH key agreement with symmetric ratchet:

**Session establishment:**
- Fetches peer's pre-key bundle
- Verifies bundle signature with Ed25519
- Performs X3DH: DH(local identity, remote signed pre-key) + DH(local identity, remote pre-key)
- Derives root key and chain keys using HKDF-SHA256

**Encryption/Decryption:**
- Symmetric ratchet with HMAC-SHA256
- AES-256-GCM with 12-byte random nonce
- Per-message keys with forward secrecy
- Out-of-order message handling (skipped key cache, max 1000 keys)

**Parameters:**
- Root key derivation info: `"aether-root-v1"`
- Send chain derivation info: `"aether-chain-send-v1"`
- Recv chain derivation info: `"aether-chain-recv-v1"`
- Message key salt: `0x01`, chain key salt: `0x02`

### 4. Transport Abstraction (`TransportService`)

Interface for physical transports. The only transports in the Kotlin port are the
in-process simulator (`InProcessTransport`, for tests/demos) and the real WebRTC
internet transport (`transport/webrtc/`, built + tested). BLE / Wi-Fi Direct are
interface slots, not implemented here:

```kotlin
interface TransportService {
    val name: String
    val isAvailable: Boolean
    val maxBandwidthBps: Long
    val maxRangeMeters: Int
    val powerCostRelative: Int
    val maxConcurrentPeers: Int

    suspend fun sendAsync(peerUhid: String, data: ByteArray): Boolean
    suspend fun sendStreamAsync(peerUhid: String, data: ByteArray): Boolean
    fun isConnected(peerUhid: String): Boolean
    val dataReceived: Flow<Pair<String, ByteArray>>
}
```

**InProcessTransport:** Reference implementation using global `ConcurrentHashMap` for testing/demo.

### 5. Domain Models (`Models.kt`)

- **AetherNetNode**: Node identity with UHID, public key, capabilities, geohash
- **PeerInfo**: Known peer with reliability score and last-seen timestamp
- **RouteEntry**: Routing table entry with hop count and quality score
- **NodeCapabilities**: Bitfield (BLE, Wi-Fi Direct, Gateway, Relay, SOS, Streaming, Voice, DTN)
- **DtnBundle**: Store-and-forward bundle with expiry and copy counting

## Protocol Constants

Key constants (from `Constants.kt`):

| Category | Constant | Value |
|----------|----------|-------|
| Packet | DEFAULT_TTL | 7 |
| Packet | PACKET_NONCE_SIZE | 8 |
| Security | MAX_SKIPPED_KEYS | 1000 |
| Security | AES_GCM_NONCE_SIZE | 12 |
| Security | AES_GCM_TAG_SIZE | 16 |
| Routing | ROUTE_TIMEOUT_MS | 5000 |
| Routing | ROUTE_EXPIRY_SECONDS | 300 |
| SOS | SOS_TTL | 15 |
| DTN | DTN_BUNDLE_TTL_HOURS | 72 |

## Packet Types

All 23 packet types match C# enum values (1-23):

1. RouteRequest
2. RouteReply
3. Data
4. Ack
5. SosBroadcast
6. SosAck
7. ChannelMessage
8. ChunkRequest
9. ChunkData
10. Heartbeat
11. StreamAnnounce
12. StreamSegment
13. StreamSubscribe
14. StreamUnsubscribe
15. VoicePtt
16. VoiceCall
17. VoiceSignaling
18. DtnBundle
19. DtnCustodyAck
20. DtnDeliveryReceipt
21. PresenceBeacon
22. PresenceQuery
23. ProfileSync

## Dependencies

- **org.bouncycastle:bcprov-jdk18on:1.76** — Ed25519, ECDH P-256, AES-GCM
- **org.bouncycastle:bcpkix-jdk18on:1.76** — Key format support
- **org.jetbrains.kotlinx:kotlinx-coroutines-core:1.7.3** — Async/await, Flow
- **org.slf4j:slf4j-api:2.0.9** — Logging
- **kotlin-stdlib** — Kotlin standard library

## Usage Examples

### Key Generation

```kotlin
val (privateKey, publicKey) = Ed25519Service.generateKeyPair()
// privateKey: 32 bytes
// publicKey: 32 bytes
```

### Packet Signing

```kotlin
val packet = MeshPacket(
    type = PacketType.Data,
    sourceUhid = "alice",
    destinationUhid = "bob",
    payload = "Hello".toByteArray()
)

val signature = PacketSigning.signPacket(packet, privateKey)
val signedPacket = packet.copy(signature = signature)

// Verify
val isValid = PacketSigning.verifyPacket(signedPacket, publicKey)
```

### Packet Serialization

```kotlin
val bytes = PacketSerializer.serialize(packet)
val deserialized = PacketSerializer.deserialize(bytes)
```

### Signal Protocol Encryption

```kotlin
val signal = SignalProtocol()

// Exchange pre-key bundles
val aliceBundle = signal.generatePreKeyBundle("alice")
val bobBundle = bobSignal.generatePreKeyBundle("bob")

// Establish session
aliceSignal.processPreKeyBundle(bobBundle)

// Encrypt
val encrypted = aliceSignal.encrypt("bob", plaintext)

// Decrypt (on Bob's side)
val decrypted = bobSignal.decrypt("alice", encrypted)
```

## Cross-Language Compatibility

This implementation maintains **exact wire-format compatibility** with the C# reference implementation:

- Binary packet format: identical little-endian layout
- Packet type enum: values match C# enum exactly (1-23)
- Ed25519 signatures: compatible with NSec/libsodium
- ECDH P-256: standard curve, compatible across languages
- HKDF-SHA256: RFC 5869 standard implementation
- AES-256-GCM: NIST standard with 12-byte nonce, 16-byte tag

Packets serialized in Kotlin can be deserialized in C# and vice versa.

## Testing

The implementation includes a comprehensive demo (`Demo.kt`) that exercises:

1. Key generation and public key export
2. Pre-key bundle generation and exchange
3. Session establishment via Signal Protocol
4. Packet creation, signing, and serialization
5. Packet deserialization and signature verification
6. Message encryption and decryption
7. Replay attack prevention
8. In-process transport messaging

Run with:
```bash
./gradlew run
```

## Security Considerations

- **Key zeroing**: All intermediate cryptographic material is zeroed after use using `CryptographicOperations.ZeroMemory` (Kotlin equivalent: `fill(0)`)
- **Replay protection**: Nonce deduplication with 5-minute TTL prevents replay attacks
- **Forward secrecy**: Per-message keys derived from chain ratchet
- **Out-of-order handling**: Skipped key cache with max 1000 keys to prevent memory exhaustion
- **RREP authentication**: Route Reply packets signed by destination node
- **Package confidentiality**: Message content encrypted with AES-256-GCM

## Future Extensions

The implementation provides hooks for:

- **BLE transport** (`TransportService` interface)
- **Wi-Fi Direct transport** (same interface)
- **DTN epidemic routing** (`DtnBundle` model ready)
- **SOS broadcast** (packet type defined)
- **Presence beacons** (packet type defined)
- **Voice and streaming** (packet types defined)
- **Double Ratchet** (when always-on transports available)

## Protocol Documentation

Full protocol specification: `/Users/admin/Code/Dev/aether-protocol/docs/PROTOCOL_SPEC.md`

## License

SPDX-License-Identifier: MIT
