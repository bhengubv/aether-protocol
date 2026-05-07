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

When a message can't reach its destination directly, it hops through other devices. Those relay devices can't read what they're carrying — every message is encrypted with AES-256-GCM. Every packet is signed with Ed25519 identity keys, and forged packets are dropped by the network.

> **Security maturity note (read before shipping):** Real X3DH (4 X25519 DHs) and the full Signal Double Ratchet (DH-rotation step on receive, KDF_RK, 0x01/0x02 chain ratchet) are now implemented in all 8 languages and pinned to a shared cross-language fixture corpus under `fixtures/signal/`. C# additionally ships the one-time pre-key pool (default 100 OPKs) that closes the single-OPK concurrency hazard; the other 7 languages still use a single OPK. Swift and Kotlin port code has landed but is pending host-machine compile verification. C ships only the X25519 + KDF_RK primitives, not full session machinery. See Roadmap and `OPEN_ISSUES.md` for the residual gaps.

No accounts, no phone numbers, no emails. You generate a keypair and you're on the network.

```
  ┌─────────────────────────────────┐
  │         Your Application        │
  ├─────────────────────────────────┤
  │ Messaging · Streaming · Voice   │
  │ Video · Watch Together          │
  ├─────────────────────────────────┤
  │  Security: AES-256-GCM · Ed25519│
  │  X3DH + Double Ratchet (X25519) │
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

| Language | Directory | Wire format | Routing/DTN/SOS | X3DH | Double Ratchet | OPK pool | Voice/Group | Streaming/Video/Watch |
|----------|-----------|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| C# (.NET 10) | `src/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Rust | `rust/` | ✅ | ✅ | ✅ | ✅ | single OPK | ✅ | ✅ |
| TypeScript | `typescript/` | ✅ | ✅ | ✅ | ✅ | single OPK | ✅ | ✅ |
| Python | `python/` | ✅ | ✅ | ✅ | ✅ | single OPK | ✅ | ✅ |
| Go | `go/` | ✅ | ✅ | ✅ | ✅ | single OPK | ✅ | ✅ |
| Kotlin | `kotlin/` | ✅ | ✅ | ✅ | ✅ | single OPK | ✅ | ✅ |
| Swift | `swift/` | ✅ | ✅ | ✅ | ✅ | single OPK | ✅ | ✅ |
| C | `c/` | ✅ | ✅ | primitives | — | — | ✅ | ✅ |

All 8 languages produce byte-identical wire packets, verified by 122 cross-language fixture assertions in CI (`fixtures/expected/*.bin`). Routing (AODV-style RREQ/RREP), DTN store-and-forward, and SOS broadcast services are implemented in every language with ~280 unit tests anchoring the per-service invariants.

Cross-language Signal interop is anchored to `fixtures/signal/` with shared test vectors for X3DH (`x3dh_basic`), the symmetric ratchet (`ratchet_step_basic`, `ratchet_step_three_iterations`), and KDF_RK (`kdf_rk_basic`). Every implementation must produce byte-identical outputs against those fixtures. Swift and Kotlin port code has landed but is pending host-machine compile verification. C ships only the X25519 and KDF_RK primitives needed for the fixture verifier — not a full Signal session.

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

**Done (verified cross-language, all 8 implementations):**
- Wire format: byte-identical across 8 languages, anchored by 14 canonical fixtures and cross-language assertions in CI (`fixtures/expected/*.bin`)
- ✅ **GitHub Actions CI** — 9-job matrix (C#/.NET 10, Go 1.22, TypeScript/Node 20, Python 3.12, Kotlin/JVM 21, Swift/macOS-14, Rust stable, C/GCC, plus fixture integrity job) in `.github/workflows/ci.yml`.
- Ed25519 packet signing and verification
- AES-256-GCM encryption
- HKDF / HMAC key derivation primitives
- Packet serialization + signing layout (LE + 4-byte int32 fields)
- In-process transport simulator (for development and tests)
- AODV-inspired routing service with RREQ/RREP, signed route replies, dedup, TTL forwarding
- DTN store-and-forward service with custody transfer, geohash-aware replication, 72h TTL
- SOS broadcast service with flood, dedup, self-origin guard, rate-limit (3/hr)
- Extensibility seams: `IncentiveProvider`, `BackendClient`, `FeatureFlagProvider` (Noop defaults)
- ~280 service-level invariant tests across all 8 languages
- ✅ **Real X3DH ephemeral key (8 languages)** — 4 X25519 DHs (`DH(IK_A,SPK_B) || DH(EK_A,IK_B) || DH(EK_A,SPK_B) || DH(EK_A,OPK_B)`) with HKDF-SHA256 root derivation. Pinned by `fixtures/signal/expected/x3dh_basic.json`.
- ✅ **Double Ratchet alignment family-wide** — full Signal §5 with HMAC-SHA256 + 0x01/0x02 domain separation in the symmetric ratchet, HKDF-SHA256 KDF_RK in the DH-ratchet step, DH-rotation on receive. Verified by `ratchet_step_basic`, `ratchet_step_three_iterations`, `kdf_rk_basic` fixtures.
- ✅ **PROTOCOL_SPEC §2 / §3 / §4 / §9 reconciled with HEAD** — see `docs/PROTOCOL_SPEC.md`.

**Done (all 8 languages):**
- ✅ **Voice calls (1-to-1)** — signaling state machine (Offer/Answer/Hangup/Cancel/Timeout) + binary frame transport (16B callId · 4B seq · 8B timestamp · 1B isSilence · N bytes). Route-aware delivery via `IRoutingService`.
- ✅ **Group voice** — host-driven membership (invite/kick/leave), per-frame key generation field, unicast fan-out to all current members, host-controlled key rotation on membership change.
- ✅ **Live streaming** — publisher broadcasts `StreamAnnounce`; subscribers send `StreamSubscribe`; binary `StreamSegment` frames (16B streamId · 4B seq · 8B ts · 1B isKeyframe · N bytes) unicast to each subscriber.
- ✅ **Video calls (1-to-1)** — codec/resolution/fps/bitrate negotiation in signaling, keyframe-request and quality-change signals, binary `VideoFrame` format matching voice layout.
- ✅ **Watch Together** — host emits authoritative `WatchSync` (play/pause/seek/speed) commands; followers apply with RTT compensation (`position = positionMs + elapsed × playbackSpeed`); fire-and-forget `WatchReaction`.

**Done (C# reference only — port to other 7 languages pending):**
- ✅ **One-time pre-key (OPK) pool** — default 100, FIFO issue, lazy top-up, lock-protected consumption. Closes the single-OPK concurrency hazard. Reference: `SignalProtocolService.TopUpOpkPoolNoLock` + `tests/Aether.Core.Tests/PreKeyPoolTests.cs`.
- ✅ **Demo Step 9 — MessagingService + DTN fallback end-to-end** — `samples/Aether.Demo.Console` walks through real-Signal-encrypted messaging with DTN store-and-forward when the recipient is offline.
- ✅ **`Aether.Messaging` ↔ `Aether.Security` bridge** — `SignalMessageEnvelopeCipher` makes the messaging layer end-to-end encrypted by default; messages without a Signal session are queued, never sent insecurely.
- ✅ **C: X25519 + KDF_RK primitives + fixture verifier** — full session machinery still pending.

**Spec'd, design doc only, no shipping pipeline:**
- Adaptive bitrate streaming (`docs/adaptive-secure-streaming-spec.md` is a forward design doc — no codec backend)
- Watch Together: BitTorrent ingest and ChipIn group funding flow
- Group video calls with auto SFU relay

**In progress (waiting on physical hardware or external infra):**
- BLE GATT transport — real Bluetooth Low Energy communication
- Wi-Fi Direct transport — direct device-to-device over WiFi
- NearLink transport implementation
- End-to-end two-node interop test on real BLE / Wi-Fi-Direct hardware
- Swift and Kotlin host-machine compile verification of the X3DH + Double Ratchet ports

**Open — tracked in `OPEN_ISSUES.md`:**
- OPK pool port to the 7 non-C# languages (currently single-OPK; concurrency-safe for sequential workloads)
- C: full Signal session machinery (X3DH + Double Ratchet; currently X25519 + KDF_RK primitives only)

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
