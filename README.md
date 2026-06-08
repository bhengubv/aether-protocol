```
     ╔═╗ ╔═╗ ╔╦╗ ╦ ╦ ╔═╗ ╦═╗
     ╠═╣ ║╣   ║  ╠═╣ ║╣  ╠╦╝
     ╩ ╩ ╚═╝  ╩  ╩ ╩ ╚═╝ ╩╚═
     mesh networking protocol
```

Share files, messages, and streams with people nearby. No WiFi. No mobile data. No sign-up. Like AirDrop, except it works with everyone, on every platform.

[![MIT License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)

[English](README.md) · [Français](docs/i18n/fr/README.md) · [Español](docs/i18n/es/README.md) · [العربية](docs/i18n/ar/README.md) · [中文简体](docs/i18n/zh-CN/README.md) · [日本語](docs/i18n/ja/README.md) · [Deutsch](docs/i18n/de/README.md) · [Português (BR)](docs/i18n/pt-BR/README.md) · [Русский](docs/i18n/ru/README.md) · [فارسی](docs/i18n/fa/README.md) · [한국어](docs/i18n/ko/README.md)

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

> **Security maturity note (read before shipping):** Real X3DH (4 X25519 DHs), the full Signal Double Ratchet (DH-rotation step on receive, KDF_RK, 0x01/0x02 chain ratchet), and the one-time pre-key pool (default 100 OPKs, FIFO, lock-protected) are implemented in **all 8 languages** and pinned to a shared cross-language fixture corpus under `fixtures/signal/`. The only remaining open item is physical RF bring-up on real BLE hardware (tracked in `OPEN_ISSUES.md`).

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

## Transports

Each transport has a colour name used throughout the codebase. `IsAvailable` gates hardware-blocked paths — the `TransportManager` skips them automatically and falls back to the next available transport.

| Colour | Name | Range | Bandwidth | Status |
|--------|------|------:|----------:|--------|
| 🔵 Aether Blue | BLE GATT | ~100 m | 1 Mbps | ✅ Windows + Android (`android/blue/`) |
| 🟢 Aether Green | Wi-Fi Direct | ~200 m | 250 Mbps | ✅ Windows + Android (`android/green/`) |
| 🟣 Aether Purple | Cellular HTTP relay | Unlimited | ~10 Mbps | ✅ Windows — relay server in `samples/AetherNet.RelayServer/` |
| ⚪ Aether White | NFC HCE | ~5 cm | 848 kbps | ⚠️ Android HCE (`android/white/`); Windows: NDEF-over-BLE-GATT + ACR122U PC/SC approximation (`Windows.Networking.Proximity` removed in Win 11) |
| 🩵 Aether Teal | NearLink | ~600 m | 12 Mbps | ✅ `harmonyos/teal/` — HarmonyOS ArkTS `@kit.NearLinkKit`; Windows + Android: SSAP-over-BLE approximation (API-analogous, not wire-compatible) |
| 🔴 Aether Red | LoRa / CircleLink | ~15 km | 37.5 kbps | ⚠️ Meshtastic wire format over BLE LR (~1.3 km); radio swap to SX1276/SX1278 when LoRa module present |

Priority order in `TransportManager`: NearLink → BLE (≤ 1 KB) → Wi-Fi Direct → NFC → LoRa → HTTP Relay (last resort, `PowerCostRelative = 100`).

## Deployment tiers

Aether works on any platform that supports Bluetooth or Wi-Fi. The tier you're on depends on the OS you're targeting.

---

### Standard tier — any platform

Android · Windows · Linux · macOS · iOS

Aether runs fully on any device with Bluetooth or Wi-Fi hardware. Where a radio is physically absent, each blocked transport is approximated using what is available:

- **NearLink (Aether Teal)** — approximated over BLE GATT using the canonical Aether SLE service UUID (`61657468-6572-0003-0000-000000000000`). The SSAP application-protocol layer is API-identical to GATT. The radio layer (BPSK/QPSK/8PSK, Polar codes, 1–4 MHz channels) is not — nodes running the standard tier cannot exchange raw bytes with real NearLink hardware; they interoperate with other standard-tier Aether nodes.
- **LoRa (Aether Red)** — approximated using the full Meshtastic wire format over BLE 5.0 Coded PHY (S=8, ~1.3 km outdoor). Bridge-node federation with real LoRa hardware works automatically — the same Meshtastic packet format rides all hops with no translation.
- **NFC (Aether White)** — approximated via NDEF-over-BLE-GATT with an RSSI proximity gate (≥ −40 dBm ≈ 5–10 cm) that reproduces tap-to-connect semantics. PC/SC path via USB NFC reader is also supported on Windows.

All other capabilities — BLE, Wi-Fi Direct, HTTP relay, Signal Protocol security (X3DH + Double Ratchet), AODV routing, DTN store-and-forward, SOS broadcast, voice, streaming — are native and identical to the native tier.

**This is a fully capable, production-grade deployment.** Most apps start here.

---

### Native tier — CircleOS / OpenHarmony

CircleOS · HarmonyOS · any OpenHarmony-based OS

CircleOS is built on OpenHarmony, which ships NearLink (SLE) silicon and the `@kit.NearLinkKit` SDK as a first-class OS capability. On CircleOS and HarmonyOS devices with NearLink hardware, no approximation is needed — `harmonyos/teal/` uses the real SLE radio directly:

```
ssap.createClient(deviceAddress)  →  client.connect()  →  client.writeProperty(WRITE_NO_RESPONSE)
advertising.startAdvertising()    →  scan.startScan()   →  client.on('propertyChange')
```

This is not just a better version of the standard tier. At the NearLink layer it is a categorically different network:

| Capability | Standard tier (BLE approx) | Native tier (CircleOS / OpenHarmony) |
|---|---|---|
| **NearLink range** | ~100 m (BLE) | **600 m** |
| **NearLink bandwidth** | ~1 Mbps (BLE) | **12 Mbps** |
| **NearLink latency** | ~10 ms (BLE) | **20 µs** |
| **NearLink power** | BLE baseline | **60% less than BLE 5.0** |
| **Concurrent NearLink peers** | ~7 (BLE connection limit) | **500+** |
| **NearLink source** | SSAP-over-BLE (`android/teal/`, `WinNearLinkStubTransportService`) | Real SLE radio (`harmonyos/teal/`, `@kit.NearLinkKit`) |
| **BLE / Wi-Fi Direct / HTTP relay** | Native | Native (identical) |
| **Signal Protocol security** | Full | Full (identical) |
| **Routing / DTN / SOS** | Full | Full (identical) |
| **Aether Tag identity** | Supported | Supported (identical) |

---

### Moving between tiers

No code changes are required. The tier is determined at runtime by `IsAvailable` on each transport service:

1. On a CircleOS or HarmonyOS device with NearLink silicon, `IsAvailable` on the NearLink transport returns `true` (hardware-probed via permission check + passive scan attempt).
2. `TransportManager` automatically promotes NearLink to priority position — lowest power cost, highest bandwidth.
3. App code, packet format, routing algorithm, security layer, and Aether Tags are identical across both tiers.

A node on the standard tier and a node on the native tier can communicate freely — they share the same wire format, the same Signal Protocol sessions, and the same Aether Tags. The tier difference affects only the radio used for NearLink packets, not the protocol above it.

---

> **Internally these tiers are referred to as the Asterix variant (standard) and the Obelix variant (native).** Asterix works well with what is available. Obelix — running on CircleOS with native NearLink — operates at permanently elevated capability, the way Obelix carries the magic potion's strength without needing to drink again.

---

## Implementations

Aether is built in 8 languages so it runs on phones, laptops, tablets, and microcontrollers. All implementations produce wire-compatible packets — a message encrypted by the Rust node can be relayed by the Python node and decrypted by the Swift node.

| Language | Directory | Wire format | Routing/DTN/SOS | X3DH | Double Ratchet | OPK pool | Voice/Group | Streaming/Video/Watch |
|----------|-----------|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| C# (.NET 10) | `src/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Rust | `rust/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| TypeScript | `typescript/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Python | `python/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Go | `go/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Kotlin | `kotlin/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| Swift | `swift/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |
| C | `c/` | ✅ | ✅ | ✅ | ✅ | ✅ (100) | ✅ | ✅ |

All 8 languages produce byte-identical wire packets, verified by 14 canonical wire-format fixtures and 4 Signal test vectors run in CI (`fixtures/expected/*.bin`, `fixtures/signal/expected/*.json`). Routing (AODV-style RREQ/RREP), DTN store-and-forward, SOS broadcast, voice, streaming, and security-hardening services are implemented in every language with **~3,000 tests** across all 8 implementations:

| Language | Tests | CI platform |
|----------|------:|-------------|
| C# (.NET 10) | 530 | ubuntu-latest |
| TypeScript / Node 20 | 459 | ubuntu-latest |
| Kotlin / JVM 21 | 457 | ubuntu-latest |
| Go 1.22 | 423 | ubuntu-latest |
| Python 3.12 | 387 | ubuntu-latest |
| Swift 6 | 295 | macos-14 |
| C (GCC) | 253 | ubuntu-latest |
| Rust (stable) | ~195 | ubuntu-latest |
| **Total** | **~3,000** | |

Cross-language Signal interop is anchored to `fixtures/signal/` with shared test vectors for X3DH (`x3dh_basic`), the symmetric ratchet (`ratchet_step_basic`, `ratchet_step_three_iterations`), and KDF_RK (`kdf_rk_basic`). Every implementation must produce byte-identical outputs against those fixtures. All 8 languages now ship a full Signal session (`generate_pre_key_bundle`, `process_pre_key_bundle`, `encrypt`, `decrypt`).

## Quickstart

```bash
git clone https://github.com/bhengubv/aether-protocol.git
cd aether-protocol
```

### C# (.NET 10 SDK)

```bash
dotnet run --project samples/AetherNet.Demo.Console
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
aethernet_mesh_packet_t *packet = aethernet_packet_new();
packet->type = AETHERNET_PACKET_TYPE_DATA;
packet->ttl = 7;

aethernet_packet_set_source_uhid(packet, "alice");
aethernet_packet_set_destination_uhid(packet, "bob");
aethernet_packet_set_payload(packet, (const uint8_t *)"Hello Bob!", 10);

// Sign
size_t signable_len = 0;
uint8_t *signable = aethernet_packet_get_signable_data(packet, &signable_len);
uint8_t signature[64];
aethernet_ed25519_sign(private_key, signable, signable_len, signature);
aethernet_packet_set_signature(packet, signature, 64);
free(signable);

// Serialize and send
uint8_t buffer[2048];
int size = aethernet_packet_serialize(packet, buffer, sizeof(buffer));
// send buffer[0..size-1] over transport

aethernet_packet_free(packet);
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
- **~3,000 tests** across all 8 languages (C# 530, TypeScript 459, Kotlin 457, Go 423, Python 387, Swift 295, C 253, Rust ~195) — all green in CI
- ✅ **Real X3DH ephemeral key (8 languages)** — 4 X25519 DHs (`DH(IK_A,SPK_B) || DH(EK_A,IK_B) || DH(EK_A,SPK_B) || DH(EK_A,OPK_B)`) with HKDF-SHA256 root derivation. Pinned by `fixtures/signal/expected/x3dh_basic.json`.
- ✅ **Double Ratchet alignment family-wide** — full Signal §5 with HMAC-SHA256 + 0x01/0x02 domain separation in the symmetric ratchet, HKDF-SHA256 KDF_RK in the DH-ratchet step, DH-rotation on receive. Verified by `ratchet_step_basic`, `ratchet_step_three_iterations`, `kdf_rk_basic` fixtures.
- ✅ **PROTOCOL_SPEC §2 / §3 / §4 / §9 reconciled with HEAD** — see `docs/PROTOCOL_SPEC.md`.

**Done (all 8 languages):**
- ✅ **Voice calls (1-to-1)** — signaling state machine (Offer/Answer/Hangup/Cancel/Timeout) + binary frame transport (16B callId · 4B seq · 8B timestamp · 1B isSilence · N bytes). Route-aware delivery via `IRoutingService`.
- ✅ **Group voice** — host-driven membership (invite/kick/leave), per-frame key generation field, unicast fan-out to all current members, host-controlled key rotation on membership change.
- ✅ **Live streaming** — publisher broadcasts `StreamAnnounce`; subscribers send `StreamSubscribe`; binary `StreamSegment` frames (16B streamId · 4B seq · 8B ts · 1B isKeyframe · N bytes) unicast to each subscriber.
- ✅ **Video calls (1-to-1)** — codec/resolution/fps/bitrate negotiation in signaling, keyframe-request and quality-change signals, binary `VideoFrame` format matching voice layout.
- ✅ **Watch Together** — host emits authoritative `WatchSync` (play/pause/seek/speed) commands; followers apply with RTT compensation (`position = positionMs + elapsed × playbackSpeed`); fire-and-forget `WatchReaction`.
- ✅ **One-time pre-key (OPK) pool** — default 100, FIFO issue, lazy top-up, lock-protected consumption across all 8 languages. Closes the single-OPK concurrency hazard.
- ✅ **C: full Signal session** — `aethernet_signal_service_init`, `generate_pre_key_bundle`, `process_pre_key_bundle`, `encrypt`, `decrypt` in `c/src/signal_protocol.c`; 6 two-node E2E tests in `c/tests/test_signal_session.c`. All 8 languages now have full session-capable Signal Protocol.

**Done (C# reference only):**
- ✅ **Demo Step 9 — MessagingService + DTN fallback end-to-end** — `samples/AetherNet.Demo.Console` walks through real-Signal-encrypted messaging with DTN store-and-forward when the recipient is offline.
- ✅ **`AetherNet.Messaging` ↔ `AetherNet.Security` bridge** — `SignalMessageEnvelopeCipher` makes the messaging layer end-to-end encrypted by default; messages without a Signal session are queued, never sent insecurely.
- ✅ **Adaptive bitrate streaming** — `AdaptiveBitrateController` with spec-mandated bitrate ladders for Profile A (real-time), B (live broadcast), and C (VOD). Publisher selects the highest sustainable rung (20% headroom) and emits `StreamAbandon` (`PacketType.StreamAbandon`) instead of a segment when below the floor. `IStreamingService` exposes `UpdateBandwidthEstimate` and `GetCurrentBitrateRung`.
- ✅ **Watch Together: BitTorrent ingest + ChipIn group funding** — `TorrentInfo` / `TorrentFile` models; `WatchTogetherService` handles `PacketType.TorrentMetadata` and fires `TorrentReceived`. `ChipInPool` / `ChipInContribution` state machine (Collecting → Funded → Purchasing → Acquired / Failed / Refunded); `StartChipInAsync` / `ContributeAsync` / `GetChipIn` on `IWatchTogetherService`.
- ✅ **Group video calls with auto SFU relay** — `GroupVideoService` / `IGroupVideoService`. FullMesh topology for ≤ 3 participants; automatic switch to SFU at `SfuThresholdParticipants` (4) with relay re-assignment via `GroupVideoSignaling(SfuAssigned)`. Fan-out in FullMesh, relay-only send in SFU mode. Signaling packet type `GroupVideoSignaling = 35`.
- ✅ **BLE GATT transport simulation** — `SimulatedBleGattTransportService` (`IBleTransportService`). GATT MTU framing via `BleGattFramer` (1024 B/frame, `[2B count][2B index][payload]`), in-process static peer registry, advertisement broadcast. All `BleMaxPayloadBytes` constraints enforced.
- ✅ **Wi-Fi Direct transport simulation** — `SimulatedWifiDirectTransportService` (`IWifiDirectService`). Explicit `ConnectAsync`/`DisconnectAsync` lifecycle, direct large-payload delivery (no framing), bidirectional `PeerConnected`/`PeerDisconnected` events.
- ✅ **NearLink transport simulation** — `SimulatedNearLinkTransportService` (`INearLinkTransportService`). 4096 B frame MTU, 500-peer registry, `ConnectedPeerCount`, `IsAvailable` settable at runtime.
- ✅ **RF bring-up simulation tests** — Two-node interop tests (`SimulatedTransportTests`): BLE + NearLink `MeshPacket` round-trip, WiFi Direct 64 KB payload transfer. Software layer fully verified; physical device lab session needed for on-hardware validation.

**Done (C# transport layer — all fail-fast):**
- ✅ **BLE GATT real transport** — `WinBleGattTransportService` (Windows WinRT) + `android/blue/` (Android GATT server). Full RF bring-up test in `samples/AetherNet.BleRfTest/`.
- ✅ **Wi-Fi Direct real transport** — `WinWifiDirectTransportService` (WinRT, `WiFiDirectAdvertisementPublisher` + TCP StreamSocket port 8888) + `android/green/` (`WifiP2pManager`). RF test in `samples/AetherNet.WifiDirectRfTest/`.
- ✅ **HTTP relay transport (Aether Purple)** — `HttpRelayTransportService` with 10-second long-poll, `PowerCostRelative = 100`, always last resort. Relay server in `samples/AetherNet.RelayServer/` (ASP.NET Core minimal API, port 5200). RF test in `samples/AetherNet.RelayRfTest/`.
- ✅ **NFC (Aether White)** — `android/white/` implements `HostApduService` with AID `F061657468657200`. `WinNfcStubTransportService` documents two Windows approximation paths: (1) NDEF-over-BLE-GATT with RSSI gate ≥ −40 dBm (simulates tap-to-connect without NFC silicon, `IsAvailable = Bluetooth present`); (2) ACR122U USB reader via `Windows.Devices.SmartCards` PC/SC (`IsAvailable = contactless reader enumerated`). Upgrade path: implement `ITransportService` when Microsoft ships a first-party P2P NFC API.
- ✅ **NearLink (Aether Teal)** — **`harmonyos/teal/`** — full HarmonyOS 5.0.1 (API 13) ArkTS implementation using `@kit.NearLinkKit` (`scan.startScan` + `ssap.createClient` + `advertising.startAdvertising`); `isAvailable` probed at runtime. `WinNearLinkStubTransportService` + `android/teal/` document the SSAP-over-BLE approximation: BLE GATT with Aether SLE service UUID `61657468-6572-0003-0000-000000000000` — API-analogous to SSAP, not wire-compatible with real NearLink hardware. Upgrade path: replace BLE GATT calls with `ssapc_*`/`ssaps_*` SDK calls; UUIDs and `TransportManager` slot unchanged.
- ✅ **LoRa / CircleLink (Aether Red)** — `LoRaCircleLinkStub` + `android/red/` document the Meshtastic-over-BLE-LR approximation: full Meshtastic wire format (16-byte header + AES-256-CTR protobuf) over BLE 5.0 Coded PHY S=8 (~1.3 km outdoor), with managed-flood routing and RSSI-weighted contention window. Bridge-node federation with real LoRa hardware works automatically (same Meshtastic packet format, no translation). Upgrade path: replace BLE LR radio with SX1276/SX1278 AT-command or SPI driver; packet format and routing unchanged.

**Open — tracked in `OPEN_ISSUES.md`:**
- RF bring-up on real hardware: end-to-end two-node interop test on physical BLE / Wi-Fi Direct devices (simulation tests pass; hardware lab session needed)
- NearLink: `harmonyos/teal/` complete; requires Huawei Mate 60/70 / Pura 70 Pro+ / Mate X6 hardware (NearLink silicon not present on non-Huawei devices). Windows + Android fall back to SSAP-over-BLE approximation automatically.
- LoRa / CircleLink: radio module required for true LoRa range. Without one, the Meshtastic wire format is carried over BLE LR (~1.3 km) and bridge-node federation with real LoRa hardware is available.
- ✅ **(RESOLVED v1.2.0)** Consumer protocol surface (Wave 16/17) — `IDtnService.BundleReceived` event for inbound bundles ([#59](https://github.com/bhengubv/aether-protocol/issues/59)), application-layer naming/discovery directory ([#60](https://github.com/bhengubv/aether-protocol/issues/60)), author-tipping interface ([#61](https://github.com/bhengubv/aether-protocol/issues/61)). All 3 shipped additively across 8 languages with byte-equal cross-language fixtures. See CHANGELOG.

**Not yet open for external contribution:**
- The protocol is still under active development. External contributions are not being accepted at this time.
- NearLink transport implementation, Android/iOS integration examples, additional transport backends, performance benchmarks, and protocol fuzzing are tracked internally and will be opened when the project reaches a stable public contribution point.

## Project Structure

```
aether-protocol/
  src/
    AetherNet.Core/          Protocol models, constants, packet serialization
    AetherNet.Security/      Signal Protocol, Ed25519, packet signing
    AetherNet.Transport/     Transport abstractions, NearLink, in-process simulator
    AetherNet.Messaging/     Message handling and relay
    AetherNet.Storage/       DTN store-and-forward persistence
    AetherNet.Streaming/     Adaptive bitrate streaming, video models and interfaces
    AetherNet.Voice/         Voice calls and group voice
    AetherNet.Content/       Content verification and chunked transfer
  samples/
    AetherNet.Demo.Console/  Interactive demo
  tests/
    AetherNet.Security.Tests/
    AetherNet.Protocol.Tests/
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

- `IAetherNetIncentiveProvider` — reward nodes that relay traffic (no-op default: altruistic relaying)
- `IAetherNetBackendClient` — sync with a server when internet is available (no-op default: fully offline)
- `IAetherNetFeatureFlagProvider` — toggle protocol features at runtime (no-op default: everything enabled)

All three ship with no-op implementations. Remove them and nothing breaks.

## Contributing

External contributions are not open yet. The project is still under active development. Check back when we announce a public contribution window.

## Security

See [SECURITY.md](SECURITY.md) for responsible disclosure policy.

## License

MIT License. See [LICENSE](LICENSE).
