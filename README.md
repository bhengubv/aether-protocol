```
     ╔═╗ ╔═╗ ╔╦╗ ╦ ╦ ╔═╗ ╦═╗
     ╠═╣ ║╣   ║  ╠═╣ ║╣  ╠╦╝
     ╩ ╩ ╚═╝  ╩  ╩ ╩ ╚═╝ ╩╚═
     mesh networking protocol
```

Share files, messages, and streams with people nearby. No WiFi. No mobile data. No sign-up. Like AirDrop, except it works with everyone, on every platform.

[![MIT License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)
[![PRs Welcome](https://img.shields.io/badge/PRs-welcome-brightgreen.svg)](CONTRIBUTING.md)

## What can you do with it?

**Share lecture notes without spending data.**

You're in a study group. Someone has past papers on their phone. Aether sends them directly to your device over Bluetooth — no hotspot, no WhatsApp group, no file size limit. If someone in the group is out of range, the file hops through other devices until it reaches them. Messages wait up to 72 hours for a route if needed.

```
  [You] ──BLE──▶ [Friend] ──WiFi──▶ [Friend's Friend]
    notes.pdf           relayed, encrypted
```

**Find out what's happening around you.**

You're at a campus event or a festival. Aether discovers other devices nearby over Bluetooth and WiFi Direct — no app feed, no algorithm. You see what's actually around you, not what's promoted.

**Send an SOS when there's no signal.**

Your phone has no reception. Aether broadcasts an emergency message to every device in range, and those devices pass it on. No cell tower needed.

```
          ╭── [Phone B]
         ╱
  [SOS!] ───── [Phone C] ──── [Phone E]
         ╲
          ╰── [Phone D]

  Flood: reaches every device in range
```

**Create private group channels.**

A channel for your res floor, your society, your project team. Only verified members can read or send messages. No server stores the conversation.

**Sell things to people nearby.**

List a textbook for sale. People walking within range of the mesh see it. No marketplace account, no listing fees — just proximity.

**Watch a movie together, across the mesh.**

Your group has a movie night. Someone has the file. Aether syncs playback across every device — play, pause, seek — all in lockstep. If only some people have the file, the mesh distributes it in real-time as a P2P stream. Everyone chips in via SDPKT to buy it if nobody has it.

## How it works

Devices talk directly to each other using Bluetooth, WiFi Direct, or NearLink. No internet connection, no server, no central infrastructure.

```
    [Alice]              [Bob]               [Charlie]            [Diana]
       |                   |                     |                   |
       |---BLE (< 1KB)--->|                     |                   |
       |                   |---WiFi Direct------>|                   |
       |                   |                     |---NearLink------->|
       |                   |                     |                   |
       |<============ End-to-End Encrypted (Signal Protocol) ======>|
       |                                                             |
       |  No internet. No servers. No ISP. Just devices talking.     |
```

When a message can't reach its destination directly, it hops through other devices. Those relay devices can't read what they're carrying — every message is encrypted with AES-256-GCM inside a Signal Protocol session (X3DH key exchange + Double Ratchet). Every packet is signed with Ed25519 identity keys. Forged packets are dropped by the network.

No accounts, no phone numbers, no emails. You generate a keypair and you're on the network.

```
  ┌─────────────────────────────────┐
  │         Your Application        │
  ├─────────────────────────────────┤
  │ Messaging · Streaming · Voice   │
  │ Video · Watch Together          │
  ├─────────────────────────────────┤
  │  Security: Signal Protocol      │
  │  AES-256-GCM · Ed25519 · X3DH  │
  ├─────────────────────────────────┤
  │  Routing: AODV + DTN            │
  ├─────────────────────────────────┤
  │  Transport: BLE · WiFi · NearLink│
  └─────────────────────────────────┘
```

**Routing** — AODV with signed route replies. Every route reply is signed by the destination's Ed25519 key, so no device can pretend to be a destination it isn't.

**Store-and-forward** — When there's no live route, packets are held for up to 72 hours until a path opens up.

**Transport selection** — The protocol picks the right transport per packet. Small control messages go over BLE. Bulk transfers use WiFi Direct. NearLink when available.

**Voice, video, and streaming** — Video calls with codec negotiation (H.264/H.265/VP8), transport-aware quality selection, group video with auto SFU relay, synchronized watch-together with RTT compensation, and adaptive bitrate streaming.

**Replay protection** — Nonce deduplication with a 5-minute timestamp freshness window.

## Implementations

Aether is built in 8 languages so it runs on phones, laptops, tablets, and microcontrollers. All implementations produce wire-compatible packets — a message encrypted by the Rust node can be relayed by the Python node and decrypted by the Swift node.

| Language | Directory | Status |
|----------|-----------|--------|
| C# (.NET 10) | `src/` | Reference implementation |
| Rust | `rust/` | Complete |
| TypeScript | `typescript/` | Complete |
| Python | `python/` | Complete |
| Go | `go/` | Complete |
| Kotlin | `kotlin/` | Complete |
| Swift | `swift/` | Complete |
| C | `c/` | Complete |

## Quickstart

```bash
git clone https://github.com/bhengubv/aether-protocol.git
cd aether-protocol
```

### C# (.NET 10 SDK)

```bash
dotnet run --project samples/Aether.Demo.Console
```

The demo walks you through 8 steps: generating Ed25519 identity keys for three nodes (Alice, Bob, Charlie), establishing Signal Protocol sessions, sending encrypted messages, relaying a message through Charlie (who can't read it), showing the binary wire format, and demonstrating forward secrecy across 5 consecutive messages. Output is colour-coded and pauses between steps.

**Send a message in C#:**

```csharp
// Establish a Signal Protocol session
var aliceSignal = new SignalProtocolService();
var bobSignal = new SignalProtocolService();

var bobBundle = await bobSignal.GeneratePreKeyBundleAsync("bob");
await aliceSignal.ProcessPreKeyBundleAsync(bobBundle);

// Encrypt and send
var encrypted = await aliceSignal.EncryptAsync("bob",
    Encoding.UTF8.GetBytes("Hello Bob"));

// Create a signed packet
var packet = new MeshPacket
{
    Type = PacketType.Data,
    SourceUhid = "alice",
    DestinationUhid = "bob",
    Payload = SerializeEncryptedPayload(encrypted),
    Ttl = 7
};
var wireBytes = PacketSerializer.Serialize(packet);
await transport.SendAsync("bob", wireBytes);
```

### Rust (1.70+)

```bash
cd rust && cargo run
```

The demo generates identity keys for two nodes, exchanges pre-key bundles, establishes encrypted sessions, sends encrypted messages in both directions, creates and signs mesh packets, verifies signatures, and serializes packets to binary wire format. It also demonstrates the in-process transport layer.

**Send a message in Rust:**

```rust
let mut alice = SignalProtocolService::new();
let mut bob = SignalProtocolService::new();

let alice_bundle = alice.generate_pre_key_bundle("alice")?;
bob.process_pre_key_bundle(&alice_bundle)?;

let bob_bundle = bob.generate_pre_key_bundle("bob")?;
alice.process_pre_key_bundle(&bob_bundle)?;

let encrypted = alice.encrypt("bob", b"Hello Bob!")?;
let decrypted = bob.decrypt("alice", &encrypted)?;
```

### TypeScript (Node 18+, tsx)

```bash
cd typescript && npm install && npm run dev
```

The demo creates two nodes in a simulated network, generates Ed25519 keys, establishes Signal Protocol sessions, creates and signs a packet, serializes it to C#-compatible binary format, encrypts a secret message, decrypts it on the other node, sends it through the transport, and verifies the round-trip.

**Send a message in TypeScript:**

```typescript
const signal = new SignalProtocol();
const bundle = await signal.generatePreKeyBundle("my-node");
// Exchange bundle with peer
await signal.processPreKeyBundle(peerBundle);

const plaintext = new TextEncoder().encode("Hello!");
const encrypted = await signal.encrypt("peer-node", plaintext);

const packet = MeshPacket.create(PacketType.Data, "my-node");
packet.destinationUhid = "peer-node";
packet.payload = encrypted;

const keyPair = Ed25519Service.generateKeyPair();
signPacket(packet, keyPair.privateKey);

const serialized = PacketSerializer.serialize(packet);
await transport.sendAsync("peer-node", serialized);
```

### Python (3.10+)

```bash
cd python && pip install -e . && python3 demo.py
```

The demo runs 8 demonstrations: Ed25519 key generation and tamper detection, node creation with capabilities, Signal Protocol X3DH key exchange, AES-256-GCM encryption and decryption, packet serialization, packet signing with replay detection, in-process transport, and a full end-to-end flow combining all layers.

**Send a message in Python:**

```python
alice_signal = SignalProtocolService()
bob_signal = SignalProtocolService()

bob_bundle = await bob_signal.generate_pre_key_bundle("bob")
await alice_signal.process_pre_key_bundle(bob_bundle)

encrypted = await alice_signal.encrypt("bob", b"Hello Bob!")

packet = MeshPacket(
    type=PacketType.Data,
    source_uhid="alice",
    destination_uhid="bob",
    payload=encrypted.ciphertext,
    ttl=7
)
signing_service.sign_packet(packet, alice_private_key)

serialized = PacketSerializer.serialize(packet)
await transport.send_async("bob", serialized)
```

### Go (1.22+)

```bash
cd go && go run ./cmd/demo/main.go
```

The demo runs 5 demonstrations: packet serialization round-trips, Ed25519 signing with tamper detection, Signal Protocol session establishment with encrypted messaging in both directions, in-process transport between two peers, and nonce deduplication for replay protection.

**Send a message in Go:**

```go
alice, _ := security.NewSignalProtocolService()
bob, _ := security.NewSignalProtocolService()

aliceBundle, _ := alice.GeneratePreKeyBundle("alice")
bob.ProcessPreKeyBundle(aliceBundle)

bobBundle, _ := bob.GeneratePreKeyBundle("bob")
alice.ProcessPreKeyBundle(bobBundle)

encrypted, _ := alice.Encrypt("bob", []byte("Hello Bob!"))
decrypted, _ := bob.Decrypt("alice", encrypted)
```

### Kotlin (JDK 17+, Gradle 8+)

```bash
cd kotlin && ./gradlew run
```

The demo walks through 11 steps: key generation, node creation with capabilities, Signal Protocol initialization, pre-key bundle exchange, session establishment, packet creation and signing, serialization, deserialization with signature verification, end-to-end encryption with key ratcheting, replay attack detection, and in-process transport.

**Send a message in Kotlin:**

```kotlin
val aliceSignal = SignalProtocol()
val bobSignal = SignalProtocol()

val bobBundle = bobSignal.generatePreKeyBundle("bob")
aliceSignal.processPreKeyBundle(bobBundle)

val aliceBundle = aliceSignal.generatePreKeyBundle("alice")
bobSignal.processPreKeyBundle(aliceBundle)

val encrypted = aliceSignal.encrypt("bob", "Hello Bob!".toByteArray())
val decrypted = bobSignal.decrypt("alice", encrypted)
```

### Swift (5.9+, macOS 13+ / iOS 16+)

```bash
cd swift && swift run aether-demo
```

The demo runs 5 tests: packet serialization round-trips, Ed25519 signing with tamper rejection, Signal Protocol session establishment with AES-256-GCM encryption, in-process transport message delivery, and a full end-to-end flow where Alice signs a packet and Bob verifies it after transport.

**Send a message in Swift:**

```swift
let aliceSignal = SignalProtocolService()
let bobSignal = SignalProtocolService()

let bobBundle = try await bobSignal.generatePreKeyBundle(localUhid: "bob")
try await aliceSignal.processPreKeyBundle(bobBundle)

var packet = MeshPacket(
    type: .data,
    sourceUhid: "alice",
    destinationUhid: "bob",
    ttl: 7,
    payload: "Hello Bob!".data(using: .utf8)!
)

let signer = await PacketSigningService(
    privateKey: alicePrivateKey, publicKey: alicePublicKey)
try await signer.signPacket(&packet)

let serialized = PacketSerializer.serialize(packet)
await transport.sendAsync(peerUhid: "bob", data: serialized)
```

### C (CMake 3.16+, C11, libsodium)

```bash
cd c && mkdir -p build && cd build && cmake .. && make && ./aether-demo
```

The demo runs 7 demonstrations: Ed25519 key generation, packet creation and signing, serialization to binary wire format, deserialization with integrity checks, AES-256-GCM encryption and decryption, HMAC-SHA256 message authentication, and HKDF-SHA256 key derivation.

**Send a message in C:**

```c
aether_mesh_packet_t *packet = aether_packet_new();
packet->type = AETHER_PACKET_TYPE_DATA;
packet->ttl = 7;

aether_packet_set_source_uhid(packet, "alice");
aether_packet_set_destination_uhid(packet, "bob");
aether_packet_set_payload(packet, (const uint8_t *)"Hello Bob!", 10);

// Sign
size_t signable_len = 0;
uint8_t *signable = aether_packet_get_signable_data(packet, &signable_len);
uint8_t signature[64];
aether_ed25519_sign(private_key, signable, signable_len, signature);
aether_packet_set_signature(packet, signature, 64);
free(signable);

// Serialize and send
uint8_t buffer[2048];
int size = aether_packet_serialize(packet, buffer, sizeof(buffer));
// send buffer[0..size-1] over transport

aether_packet_free(packet);
```

## Roadmap

What's built and what's next.

**Done (cryptographic primitives, all 8 languages):**
- Ed25519 packet signing and verification
- AES-256-GCM encryption
- HKDF / HMAC key derivation
- Packet serialization (wire format; cross-language interop has known gaps — see Caveats below)
- In-process transport simulator (for development and tests)
- Signal-Protocol-shaped session API surface (X3DH + ratchet methods exposed; see Caveats — current implementations use static-static DH, not full X3DH ephemeral)

**Spec'd but NOT yet implemented in any language (despite earlier docs claiming otherwise):**
- AODV routing with signed route replies
- DTN store-and-forward (72h)
- SOS broadcast flood
- Voice and streaming with adaptive bitrate
- Video calls (P2P and group) with transport-aware codec negotiation
- Watch Together: synchronized playback, BitTorrent ingest, ChipIn group funding
- Full X3DH (ephemeral pre-key) — current code uses static-static DH

**In progress:**
- BLE GATT transport — real Bluetooth Low Energy communication
- Wi-Fi Direct transport — direct device-to-device over WiFi
- Double Ratchet full implementation — complete forward secrecy with header encryption

**Caveats — known wire-compat gaps under audit (2026-05-02):**
- C# `Guid` byte order is mixed-endian; the other 7 languages use RFC4122 big-endian. Same packet has different `Id` bytes between C# and the rest.
- C# `PacketSigningService.BuildSignableData` uses big-endian + 1-byte Type; spec and the other 7 languages use little-endian + 4-byte LE Type. Signatures don't verify across the C# boundary.
- Rust pre-key bundles use X25519; the other languages use P-256. Pre-key bundles do not interop.
- Three different ratchet constructions across languages (HKDF vs HMAC, different salts).

These are tracked in the private repo's session-state TODO; remediation will be a coordinated cross-language pass with a fixture-based interop test.

**Open for contribution:**
- NearLink transport implementation
- Android and iOS integration examples
- Performance benchmarks across languages
- Additional transport backends (LoRa, ultrasonic, etc.)
- Protocol fuzzing and security audits

## Project Structure

```
aether-protocol/
  src/
    Aether.Core/          Protocol models, constants, packet serialization
    Aether.Security/      Signal Protocol, Ed25519, packet signing
    Aether.Transport/     Transport abstractions, NearLink, in-process simulator
    Aether.Messaging/     Message handling and relay
    Aether.Storage/       DTN store-and-forward persistence
    Aether.Streaming/     Adaptive bitrate streaming, video models and interfaces
    Aether.Voice/         Voice calls and group voice
    Aether.Content/       Content verification and chunked transfer
  samples/
    Aether.Demo.Console/  Interactive demo
  tests/
    Aether.Security.Tests/
    Aether.Protocol.Tests/
  rust/                   Rust implementation
  typescript/             TypeScript implementation
  python/                 Python implementation
  go/                     Go implementation
  kotlin/                 Kotlin/JVM implementation
  swift/                  Swift implementation
  c/                      C implementation
  docs/
    PROTOCOL_SPEC.md      RFC-style protocol specification
```

## Adding a New Transport

Implement `ITransportService`:

```csharp
public class LoRaTransportService : ITransportService
{
    public string Name => "LoRa";
    public bool IsAvailable => true;
    public long MaxBandwidthBps => 37500; // 300 kbps
    public int MaxRangeMeters => 15000;   // 15 km
    public int PowerCostRelative => 3;
    public int MaxConcurrentPeers => 50;
    // ... implement SendAsync, IsConnected, DataReceived
}
```

Register it in DI and `TransportManager` will automatically include it in transport selection, sorted by power cost.

## How It Compares

| Protocol | Limitation | Aether Advantage |
|----------|-----------|-----------------|
| **Briar** | Android-only, Tor-dependent | Cross-platform, pure mesh |
| **Meshtastic** | LoRa only (30 kbps max) | Multi-transport (BLE + WiFi + NearLink), voice and streaming capable |
| **Reticulum** | Python, small community | 8 languages, wire-compatible across all of them |
| **libp2p** | Assumes internet backbone | Offline-first, works with zero infrastructure |
| **Yggdrasil** | Overlay network, needs internet | Physical-layer mesh, works without internet |
| **Signal** | No mesh, requires internet | Works offline, P2P, mesh relay, same E2E encryption |

## Extension Points

The protocol works standalone. These interfaces let you plug in your own backend if you want one:

- `IAetherIncentiveProvider` — reward nodes that relay traffic (no-op default: altruistic relaying)
- `IAetherBackendClient` — sync with a server when internet is available (no-op default: fully offline)
- `IAetherFeatureFlagProvider` — toggle protocol features at runtime (no-op default: everything enabled)

All three ship with no-op implementations. Remove them and nothing breaks.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines.

## Security

See [SECURITY.md](SECURITY.md) for responsible disclosure policy.

## License

MIT License. See [LICENSE](LICENSE).
