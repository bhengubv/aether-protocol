# Aether Mesh Protocol - TypeScript Implementation

A complete TypeScript/Node.js implementation of the Aether mesh networking protocol, fully wire-format compatible with the C# reference implementation.

## Features

- **MeshPacket Serialization**: Binary wire format matching C# exactly (little-endian integers, length-prefixed strings/arrays)
- **Ed25519 Signing**: Using TweetNaCl for signature generation and verification
- **Signal Protocol**: X3DH key exchange with HKDF-SHA256 key derivation and AES-256-GCM encryption
- **Packet Signing**: Full signable data construction per protocol spec (Section 2.3)
- **In-Process Transport**: Simulated network for testing and demos
- **Symmetric Ratchet**: HMAC-SHA256 chain key advancement with out-of-order message support
- **Protocol Constants**: All 60+ constants from PROTOCOL_SPEC Section A

## Installation

```bash
npm install
```

## Usage

### Build

```bash
npm run build
```

### Run Demo

```bash
npm run dev
```

The demo:
1. Creates 2 nodes in an in-process simulated network
2. Generates Ed25519 key pairs
3. Establishes Signal protocol sessions
4. Creates, signs, and verifies a packet
5. Serializes and deserializes packets
6. Encrypts and decrypts messages
7. Sends packets through the transport layer

### API Examples

#### Packet Creation & Signing

```typescript
import { MeshPacket, PacketType, signPacket, Ed25519Service } from '@bhengubv/aether-protocol';

// Create packet
const packet = MeshPacket.create(PacketType.Data, "node-a");
packet.destinationUhid = "node-b";
packet.payload = new TextEncoder().encode("Hello");

// Sign it
const keyPair = Ed25519Service.generateKeyPair();
signPacket(packet, keyPair.privateKey);

// Verify
const isValid = verifyPacket(packet, keyPair.publicKey);
```

#### Signal Protocol Encryption

```typescript
import { SignalProtocol } from '@bhengubv/aether-protocol';

const signal = new SignalProtocol();

// Generate pre-key bundle
const bundle = await signal.generatePreKeyBundle("my-uhid");

// Process peer's bundle to establish session
await signal.processPreKeyBundle(peerBundle);

// Encrypt message
const encrypted = await signal.encrypt("peer-uhid", plaintext);

// Decrypt message
const decrypted = await signal.decrypt("peer-uhid", encrypted);
```

#### Packet Serialization

```typescript
import { PacketSerializer } from '@bhengubv/aether-protocol';

// Serialize to binary
const binary = PacketSerializer.serialize(packet);

// Deserialize from binary
const restored = PacketSerializer.deserialize(binary);
```

#### In-Process Transport

```typescript
import { InProcessTransport } from '@bhengubv/aether-protocol';

const nodeA = new InProcessTransport("uhid-a");
const nodeB = new InProcessTransport("uhid-b");

// Listen for incoming data
nodeB.onDataReceived = (sender, data) => {
  console.log(`Received ${data.length} bytes from ${sender}`);
};

// Send data
await nodeA.sendAsync("uhid-b", payload);
```

## Protocol Compliance

### Wire Format

All multi-byte integers are **little-endian**:
- Packet ID: 16-byte UUID
- TTL, TimestampMs: int32/int64 LE
- String lengths: uint16 LE (not uint32)
- Payload length: int32 LE

### Packet Signing (Section 2.3)

Signable data format:
```
PacketNonce (8 bytes)
|| TimestampMs (8 bytes, LE int64)
|| Type (4 bytes, LE int32)
|| SourceUhidLength (4 bytes, LE int32)
|| SourceUhid (UTF-8)
|| DestinationUhidLength (4 bytes, LE int32)
|| DestinationUhid (UTF-8)
|| SHA-256(Payload) (32 bytes)
|| Ttl (4 bytes, LE int32)
|| Priority (4 bytes, LE int32)
```

### Signal Protocol (Section 4)

- **Key Exchange**: X3DH with ECDH P-256
- **HKDF**: SHA256 with salt="AetherSignal"
- **Info Strings**: "aether-root-v1", "aether-chain-send-v1", "aether-chain-recv-v1"
- **Encryption**: AES-256-GCM with 12-byte nonce, 16-byte tag
- **Chain Ratchet**: HMAC-SHA256 with counter advancement

## Packet Types

All 23 packet types defined:
- RouteRequest (1) - AODV Route Request
- RouteReply (2) - AODV Route Reply
- Data (3) - Application data
- Ack (4) - Delivery acknowledgment
- SosBroadcast (5) - Emergency broadcast
- ... and 18 more (see protocol spec)

## Security Features

- **Ed25519 Signatures**: All packets signed per v2 protocol
- **AES-256-GCM**: Per-message keys with unique nonces
- **Replay Prevention**: 8-byte random nonce + timestamp validation
- **Forward Secrecy**: Symmetric ratchet advances chain keys
- **Out-of-Order Decryption**: Skipped message key caching (up to 1000)

## Project Structure

```
src/
  constants.ts           - All protocol constants
  index.ts              - Main exports
  protocol/
    MeshPacket.ts       - Packet interface & factory
    PacketType.ts       - Packet type enumeration
    PacketSerializer.ts - Binary serialization
  security/
    Ed25519Service.ts   - Ed25519 signing
    SignalProtocol.ts   - Signal protocol implementation
    PacketSigning.ts    - Packet signing & deduplication
  transport/
    ITransportService.ts    - Transport interface
    InProcessTransport.ts   - In-process simulated network
  models/
    index.ts            - Core data models
  demo.ts              - Runnable demonstration
```

## Testing

The demo (`npm run dev`) exercises all major features:
- Packet creation and serialization (round-trip)
- Ed25519 key generation and signature verification
- Signal protocol session establishment
- Message encryption and decryption
- In-process transport delivery

For unit tests, extend with Jest or similar test runner.

## Compatibility Notes

- **C# Wire Format**: 100% compatible with C# PacketSerializer
- **Signed Packets**: Protocol version 2 with Ed25519 signatures
- **HKDF Derivation**: Using @noble/hashes (pure JavaScript implementation)
- **ECDH**: Node.js built-in crypto module (P-256 curve)

## Dependencies

- **tweetnacl**: Ed25519 signatures via TweetNaCl
- **@noble/hashes**: HKDF-SHA256 key derivation
- **uuid**: UUID generation and parsing
- **node crypto**: AES-256-GCM, HMAC-SHA256, ECDH

## License

MIT - See LICENSE file

## References

- [PROTOCOL_SPEC.md](../../docs/PROTOCOL_SPEC.md)
- [C# Implementation](../src/)
- [TweetNaCl.js](https://github.com/dchest/tweetnacl-js)
- [Noble Hashes](https://github.com/paulmillr/noble-hashes)
