# Aether Protocol - Go Implementation

[English](README.md) · [Français](../docs/i18n/fr/go/README.md) · [Español](../docs/i18n/es/go/README.md) · [العربية](../docs/i18n/ar/go/README.md) · [中文简体](../docs/i18n/zh-CN/go/README.md) · [日本語](../docs/i18n/ja/go/README.md) · [Deutsch](../docs/i18n/de/go/README.md) · [Português (BR)](../docs/i18n/pt-BR/go/README.md) · [Русский](../docs/i18n/ru/go/README.md) · [فارسی](../docs/i18n/fa/go/README.md) · [한국어](../docs/i18n/ko/go/README.md)

A Go implementation of the Aether mesh networking protocol's protocol/crypto/serialization layer, wire-compatible with the C# reference implementation (verified against the shared fixture corpus). Note: the mesh *transport* in this port is an in-process simulator, plus a real WebRTC internet transport — there is no real BLE/Wi-Fi Direct radio (see Future Enhancements).

## Overview

This module implements the Aether decentralised mesh networking protocol for environments with intermittent or absent internet connectivity. It provides:

- **Packet Serialization**: Binary wire format compatible with C# reference implementation (little-endian encoding)
- **Ed25519 Signing**: Cryptographic packet authentication
- **Signal Protocol**: X3DH key agreement + symmetric ratchet for end-to-end encryption
- **Packet Signing Service**: Nonce deduplication with 5-minute TTL for replay prevention
- **In-Process Transport**: Memory-based transport for testing and inter-process communication (an in-process simulator — there is no real BLE/Wi-Fi Direct radio in the Go port)
- **WebRTC Transport**: Real internet peer-to-peer data-channel transport (over [pion/webrtc](https://github.com/pion/webrtc)) in `transport/webrtc/`. **Built and tested green on this Go port** — it is the one real (non-simulated) transport here
- **Models**: AetherNetNode, PeerInfo, RouteEntry, DtnBundle, SosAlert structures
- **Protocol Constants**: All routing, discovery, security, and transport constants

## Module Structure

```
aether-protocol/go/
├── go.mod                          # Module definition
├── go.sum                           # Dependency checksums
├── README.md                        # This file
│
├── protocol/
│   ├── packet.go                   # MeshPacket struct, PacketType constants
│   └── serializer.go               # Binary serialization (little-endian)
│
├── security/
│   ├── ed25519.go                  # Ed25519 signing/verification
│   ├── signal_protocol.go          # Signal Protocol (X3DH + ratchet)
│   ├── packet_signing.go           # Nonce deduplication service
│   └── models.go                   # PreKeyBundle, EncryptedPayload, SignalSession
│
├── transport/
│   ├── transport.go                # TransportService interface
│   └── in_process.go               # In-memory transport implementation
│
├── models/
│   └── models.go                   # Domain models (Node, Route, DtnBundle, etc.)
│
├── constants/
│   └── constants.go                # Protocol constants
│
└── cmd/demo/
    └── main.go                      # Comprehensive demo program
```

## Key Features

### 1. Packet Serialization (Little-Endian)

Wire format matches C# exactly using little-endian encoding for all multi-byte integers:

```
[1 byte]  Protocol version
[1 byte]  Packet type
[16 bytes] Packet ID (UUID)
[1 byte]  Priority
[4 bytes] TTL (int32, LE)
[8 bytes] TimestampMs (int64, LE)
[2 bytes] SourceUhid length (uint16, LE)
[N bytes] SourceUhid (UTF-8)
... (destination, nonce, payload, signature)
```

**Example:**
```go
serializer := &protocol.PacketSerializer{}
packet := protocol.NewMeshPacket()
packet.Type = protocol.Data
packet.SourceUhid = "node-alice"
packet.DestinationUhid = "node-bob"
packet.Payload = []byte("Hello!")

data, err := serializer.Serialize(packet)      // Binary format
recovered, err := serializer.Deserialize(data) // Round-trip
```

### 2. Ed25519 Signing & Verification

- **Key format**: 32-byte seed (private), 32-byte public key, 64-byte signature
- **Stdlib**: Uses `crypto/ed25519` (no external dependencies)

**Example:**
```go
ed25519Svc := security.NewEd25519Service()
privateKey, publicKey, err := ed25519Svc.GenerateKeyPair()

signature, err := ed25519Svc.Sign(privateKey, message)
isValid := ed25519Svc.Verify(publicKey, message, signature)
```

### 3. Signal Protocol (X3DH + Symmetric Ratchet)

Implements the Signal Protocol for end-to-end encryption:

- **Key Agreement**: ECDH P-256 using `crypto/ecdh`
- **Key Derivation**: HKDF-SHA256 using `golang.org/x/crypto/hkdf`
  - `aether-root-v1`
  - `aether-chain-send-v1`
  - `aether-chain-recv-v1`
- **Encryption**: AES-256-GCM with 12-byte nonce, 16-byte tag
- **Ratcheting**: HMAC-SHA256 chain advancement
- **Out-of-order**: Skipped message keys (max 1000)

**Example:**
```go
aliceService, _ := security.NewSignalProtocolService()
bobService, _ := security.NewSignalProtocolService()

// Alice generates pre-key bundle
aliceBundle, _ := aliceService.GeneratePreKeyBundle("alice")

// Bob establishes session with Alice
bobService.ProcessPreKeyBundle(aliceBundle)

// Alice establishes session with Bob
bobBundle, _ := bobService.GeneratePreKeyBundle("bob")
aliceService.ProcessPreKeyBundle(bobBundle)

// End-to-end encrypted messaging
plaintext := []byte("Secret message")
encrypted, _ := aliceService.Encrypt("bob", plaintext)
decrypted, _ := bobService.Decrypt("alice", encrypted)
```

### 4. Packet Signing & Nonce Deduplication

Prevents replay attacks with 5-minute TTL on nonce cache:

```go
signer := security.NewPacketSigningService(300) // 300 seconds TTL
defer signer.Close()

// Compute signable data (SHA256 of payload + header fields)
signableData := signer.ComputeSignableData(
    nonce, timestamp, packetType, sourceUhid, destUhid, payload, ttl, priority)

// Track nonces for deduplication
signer.RecordNonce(sourceUhid, nonce)
isDuplicate := signer.IsNonceSeen(sourceUhid, nonce)
```

### 5. In-Process Transport

Memory-based transport for testing and local node communication:

```go
inProcTransport := transport.NewInProcessTransport()

// Register peers
aliceRx, _ := inProcTransport.RegisterPeer("alice", 10) // buffered channel
bobRx, _ := inProcTransport.RegisterPeer("bob", 10)

// Send and receive
ctx := context.Background()
inProcTransport.SendAsync(ctx, "bob", []byte("Hello!"))
message := <-bobRx

// Properties
fmt.Println(inProcTransport.Name())                // "InProcess"
fmt.Println(inProcTransport.IsAvailable())         // true
fmt.Println(inProcTransport.MaxBandwidthBps())     // 1000000
fmt.Println(inProcTransport.IsConnected("bob"))    // true
```

### 6. Domain Models

Complete structures for mesh networking:

```go
// Node in the mesh
node := &models.AetherNetNode{
    UHID: "node-alice-001",
    IdentityKey: publicKey,
    Capabilities: models.CapabilityBLE | models.CapabilityRelay,
    IsLocal: true,
}

// Route to destination
route := &models.RouteEntry{
    DestinationUhid: "node-bob",
    NextHop: "node-bob",
    HopCount: 1,
    ExpiresAt: time.Now().Add(5 * time.Minute),
    QualityScore: 85,
}

// DTN bundle for store-and-forward
bundle := &models.DtnBundle{
    ID: uuid.New().String(),
    SenderUhid: "alice",
    RecipientUhid: "bob",
    Priority: models.DtnPriorityHigh,
    Status: models.DtnStatusPending,
}

// Emergency alert
alert := &models.SosAlert{
    SenderUhid: "alice",
    Message: "Emergency! Need help!",
    Latitude: -33.9249,
    Longitude: 18.4241,
}
```

## Protocol Constants

All constants from the protocol specification (Section Appendix A):

```go
// Routing
DefaultTtl = 7
SosTtl = 15
RouteTimeoutMs = 5000

// BLE Discovery
BleScanOnMs = 2000
BleScanOffMs = 8000
BleUuidRotationSeconds = 900

// Security
MaxPacketAgeSeconds = 300
MaxSkippedKeys = 1000
AesGcmNonceSize = 12
AesGcmTagSize = 16

// DTN
DtnBundleTtlHours = 72
DtnMaxCopies = 3
DtnMaxBundlesPerNode = 50

// Voice, Streaming, Presence constants...
```

## Running the Demo

The demo program illustrates all major features:

```bash
cd /Users/admin/Code/Dev/aether-protocol/go
go run ./cmd/demo/main.go
```

**Demo output:**
```
========================================
Aether Protocol - Go Implementation Demo
========================================

[ DEMO 1: Packet Serialization ]
  Original Packet: [Data] ... src=node-alice-001 dst=node-bob-001
  Payload: Hello, Aether!
  Serialized size: 95 bytes
  Deserialized Packet: [Data] ...
  Payload: Hello, Aether!
  ✓ Round-trip serialization successful!

[ DEMO 2: Ed25519 Signing ]
  Generated Ed25519 Key Pair:
    Private Key (seed): 32 bytes
    Public Key: 32 bytes
  Signed message: Important mesh packet signature
  Signature: 64 bytes
  Signature verification: true
  Verification with tampered data: false (should be false)
  ✓ Ed25519 signing verification successful!

[ DEMO 3: Signal Protocol - Session Establishment ]
  Creating Signal Protocol services for Alice and Bob...
  ✓ Alice generated pre-key bundle
  ✓ Bob established session with Alice
  ✓ Bob generated pre-key bundle
  ✓ Alice established session with Bob
  ✓ Alice encrypted message: Hello Bob, this is Alice!
    Ciphertext: 41 bytes
  ✓ Bob decrypted message: Hello Bob, this is Alice!
  ✓ Bob encrypted message: Hi Alice, I received your message!
  ✓ Alice decrypted message: Hi Alice, I received your message!
  ✓ Signal Protocol end-to-end encryption successful!

[ DEMO 4: In-Process Transport ]
  Transport: InProcess
  Available: true
  Max Bandwidth: 1000000 bps
  Max Range: 100 meters
  ✓ Registered peer: alice
  ✓ Registered peer: bob
  ✓ Alice sent: Hello Bob! (success: true)
  ✓ Bob received: Hello Bob!
  ✓ Bob sent: Hi Alice! (success: true)
  ✓ Alice received: Hi Alice!
  Alice connected to bob: true
  Bob connected to alice: true
  ✓ In-process transport successful!

[ DEMO 5: Packet Signing & Nonce Deduplication ]
  Computed signable data: 152 bytes
  ✓ Recorded nonce for replay prevention
  Nonce seen (should be true): true
  Different nonce seen (should be false): false
  ✓ Nonce deduplication working correctly!

========================================
All demos completed successfully!
========================================
```

## Wire Format Compatibility

All serialization uses **little-endian encoding** to match the C# reference implementation:

- **Integers**: `encoding/binary.LittleEndian`
- **UUIDs**: Standard 16-byte UUID format
- **Strings**: UTF-8 encoded with 2-byte (uint16) or 4-byte (uint32) length prefix
- **Bytes**: Length-prefixed (2-byte or 4-byte) followed by raw data

This ensures byte-for-byte compatibility when exchanging packets between Go and C# implementations.

## Dependencies

```
github.com/google/uuid v1.6.0     - UUID generation
golang.org/x/crypto v0.31.0       - HKDF, ECDH, Ed25519
```

All cryptographic primitives use Go's standard library (`crypto/*`) plus `golang.org/x/crypto` for HKDF and ECDH P-256.

## Security Features

1. **Key Zeroing**: All intermediate keys are securely zeroed with `ZeroMemory()`
2. **No Fallback Encryption**: Messages require established sessions; no UHID-derived fallback
3. **Replay Prevention**: 8-byte nonce + timestamp + 5-minute dedup cache
4. **Counter Gaps**: Out-of-order messages supported up to MaxSkippedKeys (1000)
5. **Signature Verification**: All route replies and pre-key bundles verified with Ed25519

## Performance Notes

- **Packet serialization**: ~1-2µs per packet (tested with 100-byte payloads)
- **Ed25519 signing**: ~50µs per signature
- **Signal Protocol encryption**: ~100µs per message
- **Nonce dedup cleanup**: Background goroutine runs every 60 seconds

## Testing

The demo program demonstrates:
- ✓ Packet round-trip serialization
- ✓ Ed25519 signature verification
- ✓ Signal Protocol session establishment
- ✓ End-to-end encryption/decryption
- ✓ In-process transport communication
- ✓ Nonce deduplication

All operations are goroutine-safe using `sync.RWMutex` and `sync.Map` where appropriate.

## Implementation Notes

1. **UUID Format**: Uses `github.com/google/uuid` for RFC 4122 compliance
2. **Key Management**: No external key storage; keys kept in memory for demo. Production should use secure storage.
3. **Transport Interface**: Extensible for BLE, Wi-Fi Direct, and other physical layers
4. **Signal Sessions**: Persisted per-peer with no database backing in this implementation
5. **Error Handling**: All crypto operations return errors; caller must handle failures

## Future Enhancements

Note: a real **WebRTC** internet transport is already implemented and tested in
`transport/webrtc/` (see above). The radio transports below are NOT implemented
in the Go port — the in-process transport is a simulator for tests/demos:

- [ ] SQLite persistence for routes and sessions
- [ ] BLE transport implementation (real BLE exists only in the C#/Windows + Android stacks)
- [ ] Wi-Fi Direct transport implementation (real Wi-Fi Direct exists only in the C#/Windows + Android stacks)
- [ ] AODV routing protocol implementation
- [ ] DTN epidemic routing
- [ ] Presence and discovery beacon service
- [ ] Voice and streaming support
- [ ] Double Ratchet algorithm for higher-assurance forward secrecy

## License

SPDX-License-Identifier: MIT
