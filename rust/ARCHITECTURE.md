# Aether Protocol Rust Implementation — Architecture

## Design Overview

This Rust implementation provides complete protocol compatibility with the C# reference implementation while following Rust idioms and best practices.

## Module Organization

### `protocol/` — Wire Format & Packet Structure

**`mod.rs`** — Core packet definitions
- `PacketType` enum: All 26 protocol-defined packet types
- `MeshPacket` struct: The fundamental unit of communication
- Helper methods for TTL management, freshness checks, and signable data construction

**`serializer.rs`** — Binary serialization
- Serialize: MeshPacket → Vec<u8>
- Deserialize: Vec<u8> → MeshPacket
- **Critical detail:** Maintains exact wire format compatibility with C# PacketSerializer
  - Little-endian integers throughout
  - u16 length prefixes for string fields
  - i32 length prefixes for payload/signature
  - UUID serialization as raw bytes
  - Proper error handling for malformed input

### `security/` — Cryptographic Operations

**`ed25519.rs`** — Identity key operations
- Key generation (32-byte private seed, 32-byte public key)
- Signing (Ed25519, produces 64-byte signature)
- Verification (standard + fallback for legacy P-256 during migration window)

**`signal_protocol.rs`** — End-to-end encryption
- X3DH key agreement using ECDH P-256
- Pre-key bundle generation and verification
- HKDF-SHA256 key derivation with protocol-specific info strings
- AES-256-GCM encryption/decryption
- Symmetric ratchet with chain key advancement
- Out-of-order message handling (up to 1,000 skipped keys)
- Key zeroing after cryptographic operations

**`packet_signing.rs`** — Packet authentication
- Deterministic signable data construction (matching C# spec exactly)
- Random nonce generation (8 bytes)
- Timestamp attachment (milliseconds since epoch)
- Nonce deduplication per sender (prevents replay attacks)
- Freshness validation (5-minute window)
- Automatic cleanup of expired entries

### `transport/` — Network Layer Abstraction

**`mod.rs`** — Transport trait
- Abstract interface for different physical layers (BLE, Wi-Fi Direct, etc.)
- Methods: `send_async()`, `send_stream_async()`, `is_connected()`
- Properties: bandwidth, range, power cost, peer capacity
- Event handler for data reception

**`in_process.rs`** — Testing/demo transport
- Simulated mesh network using static ConcurrentHashMap
- Fire-and-forget message delivery
- Bidirectional connectivity tracking
- Suitable for unit tests, integration tests, and demonstrations

### `models.rs` — Core Data Structures

- `AetherNetNode` — Represents a mesh node with peers and routes
- `PeerInfo` — Metadata about known peers
- `RouteEntry` — Routing table entries with expiry
- `Capabilities` — Bitfield of node features (BLE, gateway, relay, etc.)
- `PreKeyBundle` — Published public key material for async session establishment
- `SignalSession` — In-memory session state (keys, counters, skipped keys)
- `EncryptedPayload` — Encrypted message with nonce and counter
- `DtnBundle` — Store-and-forward bundle for offline delivery

### `constants.rs` — Protocol Parameters

All constants from the protocol spec:
- Routing timings (TTL, timeouts, expiry)
- Security parameters (nonce size, max packet age, max skipped keys)
- Transport limits (BLE max payload, Wi-Fi Direct peers)
- DTN parameters (bundle TTL, max copies, scan interval)
- Streaming/voice/presence intervals and buffers
- HKDF info strings and salt values

### `lib.rs` — Public API

Re-exports for convenient crate usage:
- Protocol types
- Security services
- Transport abstractions
- Model structures

### `main.rs` — Demonstration

End-to-end demo showing:
1. Key generation
2. Signal Protocol session establishment
3. Message encryption/decryption
4. Packet signing
5. Serialization/deserialization
6. In-process transport usage

## Design Decisions

### 1. Wire Format Fidelity

**Decision:** Maintain exact binary compatibility with C# implementation.

**Implementation:**
- Little-endian integers via `to_le_bytes()` and `from_le_bytes()`
- String lengths as u16 (UHIDs) and i32 (payload/signature) matching C# serializer
- UUID serialized as raw 16-byte array
- Proper span slicing for zero-copy deserialization where possible

**Rationale:** Enables seamless interoperability between Rust and C# nodes in production.

### 2. Async-First Transport

**Decision:** Use `async-trait` for transport abstraction.

**Implementation:**
- `TransportService` trait with async methods
- `#[async_trait]` for async trait methods in stable Rust
- Tokio runtime for async execution

**Rationale:** Modern Rust practice; allows efficient I/O multiplexing and scalability.

### 3. Cryptographic Key Zeroing

**Decision:** Zero sensitive key material immediately after use.

**Implementation:**
- Manual byte-by-byte zeroing of intermediate secrets
- Ownership transfer patterns to prevent accidental reuse
- Drop implementations for automatic cleanup (future enhancement)

**Rationale:** Defense against side-channel attacks; protects against memory disclosure.

### 4. Signal Protocol Ratcheting

**Decision:** Implement symmetric ratchet matching C# exactly.

**Implementation:**
- HKDF-SHA256 with salt [0x01] for message key derivation
- HKDF-SHA256 with salt [0x02] for chain key advancement
- Counter-based skipped key tracking
- MaxSkippedKeys limit to prevent memory exhaustion

**Rationale:** Provides forward secrecy; matches Signal Protocol design.

### 5. Error Handling

**Decision:** Use `Result<T, Box<dyn std::error::Error>>` for broad compatibility.

**Implementation:**
- Custom error messages for domain-specific failures
- Conversion from standard library errors
- Propagation via `?` operator

**Rationale:** Balances simplicity with flexibility; future improvement to custom error enum possible.

### 6. Nonce Deduplication

**Decision:** Per-sender tracking with automatic cleanup.

**Implementation:**
- HashMap of sender UHID → VecDeque of (nonce, timestamp)
- Automatic expiry of entries older than 5 minutes
- Efficient O(1) lookup via HashMap

**Rationale:** Prevents replay attacks while bounding memory usage.

## Protocol Compliance Matrix

| Feature | Status | Notes |
|---------|--------|-------|
| Binary wire format | ✅ | Exact match with C# serializer |
| All 26 packet types | ✅ | Defined as enum variants |
| Ed25519 identity keys | ✅ | 32-byte private, 32-byte public, 64-byte signatures |
| X3DH key agreement | ✅ | Using x25519-dalek and ECDH P-256 |
| HKDF-SHA256 derivation | ✅ | Unique info strings per context |
| AES-256-GCM encryption | ✅ | 12-byte nonce, 16-byte tag |
| Symmetric ratchet | ✅ | Chain key advancement, message key derivation |
| Out-of-order handling | ✅ | Skipped key caching (max 1,000) |
| Packet signing | ✅ | Deterministic signable data + Ed25519 |
| Nonce deduplication | ✅ | Per-sender, 5-minute TTL |
| Pre-key bundles | ✅ | Generation, verification, processing |
| Transport abstraction | ✅ | Trait-based with in-process implementation |
| SOS broadcast structure | ✅ | PacketType enum supports SosBroadcast |
| DTN bundle format | ✅ | DtnBundle struct with all fields |
| Route entries | ✅ | RouteEntry with quality score and expiry |
| Peer reliability scoring | ✅ | PeerInfo includes reliability_score |
| Capabilities bitfield | ✅ | Capabilities struct with bit operations |

## Testing Strategy

### Unit Tests
- Packet creation and manipulation
- Serialization/deserialization roundtrips
- Ed25519 signing and verification
- Signal Protocol encryption/decryption
- Packet signing with freshness validation
- Transport connectivity

### Integration Tests
- Multi-node communication flows
- Session establishment between Alice and Bob
- Message exchange with counter verification
- Packet signing verification across nodes

### Demo Application
- End-to-end workflow
- Real protocol usage patterns
- Output validation

## Performance Considerations

- **Serialization:** Stack-allocated buffer with known size; zero-copy deserialization where possible
- **Cryptography:** Native implementations via dalek libraries; hardware acceleration where available
- **Transport:** Async I/O with Tokio; non-blocking message delivery
- **Memory:** Bounded skipped key cache (1,000 max); automatic cleanup of old entries

## Future Enhancements

1. **Custom Error Type**
   ```rust
   enum ProtocolError {
       InvalidPacket(String),
       CryptographicFailure(String),
       TransportError(String),
   }
   ```

2. **Persistence Layer**
   - SQLite backing for routes, peers, sessions
   - Automatic session serialization

3. **DTN Implementation**
   - Bundle storage and delivery scanning
   - Epidemic routing logic
   - Custody transfer tracking

4. **Route Discovery (AODV)**
   - RREQ/RREP handling
   - Route table management
   - Quality-of-service weighting

5. **Advanced Transports**
   - BLE transport implementation
   - Wi-Fi Direct support
   - NearLink protocol

6. **Performance Optimizations**
   - SIMD for bulk operations
   - Shared state with Arc<RwLock<T>>
   - Streaming decompression

## Interoperability

This implementation is designed to interoperate with:
- C# AetherNet.Core, AetherNet.Security, AetherNet.Transport libraries
- iOS/Android MAUI implementations
- Web-based gateway nodes

Binary compatibility is maintained through:
- Identical wire format
- Deterministic signable data construction
- Standard cryptographic algorithms (Ed25519, ECDH P-256, AES-GCM)
- Matching protocol constants and parameter values

## Dependencies

| Crate | Version | Purpose |
|-------|---------|---------|
| `ed25519-dalek` | 2.1 | Ed25519 signing and verification |
| `x25519-dalek` | 2.0 | X25519 key agreement |
| `aes-gcm` | 0.10 | AES-256-GCM encryption |
| `hkdf` | 0.12 | HKDF key derivation |
| `sha2` | 0.10 | SHA-256 hashing |
| `hmac` | 0.12 | HMAC operations |
| `rand` | 0.8 | Random number generation |
| `uuid` | 1.6 | GUID generation |
| `serde` | 1.0 | Serialization framework |
| `serde_json` | 1.0 | JSON support |
| `tokio` | 1.35 | Async runtime |
| `async-trait` | 0.1 | Async trait definitions |

All dependencies are well-maintained, production-ready crates with active security monitoring.
