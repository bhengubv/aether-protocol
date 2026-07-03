# AetherNet — offline-first mesh networking protocol

```
     ╔═╗ ╔═╗ ╔╦╗ ╦ ╦ ╔═╗ ╦═╗
     ╠═╣ ║╣   ║  ╠═╣ ║╣  ╠╦╝
     ╩ ╩ ╚═╝  ╩  ╩ ╩ ╚═╝ ╩╚═
     mesh networking protocol
```

**AetherNet is an open-source, MIT-licensed mesh networking protocol** for sending messages, files, voice, and video to people nearby — with **no internet, no servers, and no sign-up**. Devices connect directly over Bluetooth, Wi-Fi Direct, NearLink, and LoRa; when the recipient is out of range, messages hop through other devices and wait up to 72 hours for a route. It ships **byte-for-byte identical implementations in eight programming languages** — C#, Rust, TypeScript, Python, Go, Kotlin, Swift, and C.

Share files, messages, and streams with people nearby. No WiFi. No mobile data. No sign-up. Like AirDrop, except it works with everyone, on every platform.

[![MIT License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)

[English](README.md) · [Français](docs/i18n/fr/README.md) · [Español](docs/i18n/es/README.md) · [العربية](docs/i18n/ar/README.md) · [中文简体](docs/i18n/zh-CN/README.md) · [日本語](docs/i18n/ja/README.md) · [Deutsch](docs/i18n/de/README.md) · [Português (BR)](docs/i18n/pt-BR/README.md) · [Русский](docs/i18n/ru/README.md) · [فارسی](docs/i18n/fa/README.md) · [한국어](docs/i18n/ko/README.md) · [isiZulu](docs/i18n/zu/README.md) · [Afrikaans](docs/i18n/af/README.md) · [Sesotho](docs/i18n/st/README.md) · [Kiswahili](docs/i18n/sw/README.md) · [Hausa](docs/i18n/ha/README.md) · [አማርኛ](docs/i18n/am/README.md) · [हिन्दी](docs/i18n/hi/README.md) · [Bahasa Indonesia](docs/i18n/id/README.md) · [বাংলা](docs/i18n/bn/README.md) · [اردو](docs/i18n/ur/README.md)

> **One protocol, eight languages, identical on the wire.** Aether is implemented in **C#, Rust, TypeScript, Python, Go, Kotlin, Swift, and C** — and every packet is byte-for-byte identical across all of them, enforced by a shared cross-language fixture corpus in CI. Build your node in any of the eight; it interoperates with all the others. This README is also available in 11 human languages (links above).

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

## What you get — every service, in every language

Aether is not just a transport. Every packet type reserved by the protocol is now a **real, working service in all 8 languages**, and every one serializes to **byte-identical wire packets** — a packet built by the Go node is decoded, unchanged, by the Swift, Rust, C, Python, TypeScript, Kotlin, or C# node. Each service is pinned to a shared cross-language fixture under `fixtures/<service>/` and exercised by per-language unit tests, with Swift and C additionally verified on the macOS build server.

| Capability | What it does | Packet type(s) | Fixture | 8/8 |
|---|---|:-:|---|:-:|
| **Presence beacon & query** | Announce "I'm here" and ask "who's around?" — over a **rotating, key-derived ephemeral ID** (not your real identity) plus a coarse geohash | 21, 22 | `fixtures/presence/` | ✅ |
| **Heartbeat** | Lightweight liveness keep-alive between linked peers | 10 | `fixtures/heartbeat/` | ✅ |
| **Profile sync** | Exchange a signed profile card with a peer over the mesh | 23 | `fixtures/profiles/` | ✅ |
| **Ephemeral-ID announce** | Privately tell a friend your current rotating routing ID so they can still reach you after it rotates | 56 | `fixtures/erid/` | ✅ |
| **Pre-key exchange** | Request and deliver a Signal pre-key bundle over the mesh, to bootstrap an end-to-end session with someone you've never met | 25, 26 | `fixtures/prekey/` | ✅ |
| **Channels** | Signed messages to a private, members-only group channel | 7 | `fixtures/channels/` | ✅ |
| **Push-to-talk** | Walkie-talkie voice frames (opaque encoded audio payload) | 15 | `fixtures/media/` | ✅ |
| **Screen share** | Screen-share video frames (opaque encoded video payload) | 32 | `fixtures/media/` | ✅ |
| **Call control** | Ring / accept / decline / hang-up signalling for voice and video calls | 27 | `fixtures/videocall/` | ✅ |
| **SOS acknowledgement** | Confirm to the sender that their emergency broadcast was received | 6 | `fixtures/sos/` | ✅ |
| **Space breadcrumbs** | Location-tagged discovery crumbs for the "what's around me" layer | 40 | `fixtures/space/` | ✅ |
| **Forge announce** | Advertise a derived/forged content artefact to the mesh | 41 | `fixtures/forge/` | ✅ |
| **Vault shard request** | Fetch an erasure-coded storage shard (any K of N shards rebuild the file) | 42 | `fixtures/vaultshard/` | ✅ |
| **Bandwidth measurement** | Probe / ack / gossip link throughput so the mesh routes over the fattest pipe (ABMF) | 53, 54, 55 | `fixtures/bandwidth/` | ✅ |

These sit on top of the already-complete **messaging, 1-to-1 and group voice, video calls, live streaming, watch-together, AODV routing, DTN store-and-forward, and SOS flood** services — also implemented in all 8 languages.

> **What "built" means here, precisely.** Each service produces and handles its wire packet, raises the right events, and is pinned to a byte-level fixture that the whole language family must match. Your application wires the service to its Signal session, routing table, and local state. This is the protocol layer — proven in code, tests, and cross-language byte-fixtures — on the same honest RF footing as everything else: any path that ultimately rides a radio is field-unverified until the hardware bring-up tracked in `OPEN_ISSUES.md`.

## Security & privacy

Beyond the wire-service suite, Aether ships a small **security & privacy layer** — identity-key management and link-layer anti-tracking. Like everything else, each is implemented in **all 8 languages** and pinned to a shared cross-language fixture under `fixtures/<feature>/` (Swift and C additionally verified on the macOS build server). These are *not* four more of the 18 wire services: three define **no new wire packet type** at all, and the fourth carries its own envelopes **inside the existing DTN/mesh path** rather than as a new reserved packet.

| Capability | What it does | Layer | Fixture | 8/8 |
|---|---|---|---|:-:|
| **Recovery-phrase backup** | Back up an identity as a **24-word BIP-39** phrase and restore it on any device. Standard BIP-39 (verified against the official Trezor vectors), SHA-256-checksummed so a mistyped word is *rejected*, never silently wrong. No server, no custodian — the phrase **is** the identity. | local | `fixtures/bip39/` | ✅ |
| **Bluetooth tracking-protection** | Derives a rotating, key-derived BLE **Service UUID** (HMAC-SHA256, 15-minute window) and **resolvable private addresses** (IRK + the RFC `ah` function, AES-128) — the anti-tracking material a BLE advertiser needs so a passive scanner can't link it across time or place. | link-layer | `fixtures/bleprivacy/` | ✅ |
| **Panic-wipe** | A **duress PIN** (SHA-256, constant-time compared) that, under coercion, securely erases every identity key — overwrite-with-random then zero — leaving nothing to recover. | local | `fixtures/panicwipe/` | ✅ |
| **Multi-device sync** | **Decentralised, server-less** sync across your *own* devices: an Ed25519-signed **DeviceLink** pairs them, and last-write-wins **SyncRecord** envelopes reconcile state — carried E2E-encrypted over the existing DTN/mesh, with no cloud account and no sync server. | rides DTN | `fixtures/sync/` | ✅ |

**One honest asymmetry.** The multi-device `DeviceLink` is Ed25519-signed, and that signature is **byte-identical across 7 of the 8 languages**. Apple's CryptoKit deliberately *randomises* Ed25519 signatures, so on Swift the 64 signature bytes differ each time — but the **signed body is byte-identical** and every link still verifies on all 8 SDKs, so Swift reaches **verification** parity rather than signature-byte parity. That is a platform-crypto property, not a defect, and it is the only place across these four features where "byte-identical" carries an asterisk. Full wire formats are in [`PROTOCOL_SPEC.md`](docs/PROTOCOL_SPEC.md) §12; the threat model is in [`THREAT_MODEL.md`](docs/THREAT_MODEL.md).

## Transports

Each transport has a colour name used throughout the codebase. `IsAvailable` gates hardware-blocked paths — the `TransportManager` skips them and falls back to the next available transport.

**Status key:** ✅ real, built & verified · ⏳ real, verification in progress · ⚠️ real on some platforms, stub on others · ❌ stub (no transport code yet).

| Colour | Name | Range | Bandwidth | Status |
|--------|------|------:|----------:|--------|
| 🔵 Aether Blue | BLE GATT | ~100 m | 1 Mbps | ✅ Real — Windows (WinRT) + Android (`android/blue/`) |
| 🟢 Aether Green | Wi-Fi Direct | ~200 m | 250 Mbps | ✅ Real — Windows (WinRT) + Android (`android/green/`) |
| 🟣 Aether Purple | HTTP / QUIC relay | Unlimited | ~10 Mbps | ✅ Real — Windows; relay server in `samples/AetherNet.RelayServer/` |
| 🟪 WebRTC P2P | Internet data channel | Unlimited | ~100 Mbps | ✅ Real in all 8 languages — **loopback-verified in all 8** (C#/Go/Kotlin/TypeScript/Python/C/Swift/Rust each have two peers exchange bytes over a real ICE data channel) |
| ⚪ Aether White | NFC HCE | ~5 cm | 848 kbps | ⚠️ Real on Android (`android/white/`); Windows = real BLE-GATT + RSSI −40 dBm proximity approximation (`WinNfcBleTransportService`, compiles net9/10, runtime-unverified) — `Windows.Networking.Proximity` removed in Win 11 |
| 🩵 Aether Teal | NearLink | ~600 m | 12 Mbps | ⚠️ Real on HarmonyOS (`harmonyos/teal/`, `@kit.NearLinkKit` — pending on-device verification); Android + Windows = real SSAP-over-BLE approximation (`android/teal/AetherNetSleService`, `WinNearLinkBleTransportService`; compile + unit-test verified, runtime-unverified) |
| 🔴 Aether Red | LoRa / CircleLink | ~15 km | 37.5 kbps | ⚠️ Real RYLR SX127x/SX126x serial driver (`LoRaSerialTransport` in C#/Go/Rust/C; compiles, runtime-unverified — needs a physical module); BLE Coded-PHY bridge still a documented design |

The radio transports are real only where platform code exists (C#/Windows, Kotlin/Android, HarmonyOS). The eight language libraries otherwise ship an **in-process simulation** transport for testing — **WebRTC is the first real transport common to all of them** (complete; loopback-verified across the languages).

Priority is by power cost: the radio mesh is preferred, then WebRTC as a direct internet path, with the HTTP/QUIC relay as last resort.

## Deployment tiers

Aether works on any platform that supports Bluetooth or Wi-Fi. The tier you're on depends on the OS you're targeting.

---

### Standard tier — any platform

Android · Windows · Linux · macOS · iOS

Aether runs on any device with Bluetooth or Wi-Fi hardware. Where a radio is physically absent, each blocked transport is approximated over what is available. These approximations are now **real code** (compile-verified; **runtime-unverified** pending a 2-device / hardware RF test):

- **NearLink (Aether Teal)** — real SSAP-over-BLE-GATT approximation (Aether SLE UUID `61657468-6572-0003-…`) on Android (`android/teal/AetherNetSleService`) and Windows (`WinNearLinkBleTransportService`); compile + unit-test verified, runtime-unverified. The real NearLink radio exists only on HarmonyOS (`harmonyos/teal/`, pending on-device verification).
- **LoRa (Aether Red)** — real RYLR SX127x/SX126x serial driver (`LoRaSerialTransport` in **all 8 languages** — C#/Go/Rust/C/Python/TypeScript/Swift/Kotlin; every port compile-verified, including Swift + C on the Mac build server; runtime-unverified — needs a physical module). The Meshtastic-over-BLE-Coded-PHY bridge (~1.3 km) remains a documented design; real long-range LoRa needs a LoRa-capable node (gateway, SBC, or rugged handset with a LoRa module).
- **NFC (Aether White)** — real on Android (HCE). Windows now has a real BLE-GATT + RSSI −40 dBm proximity approximation (`WinNfcBleTransportService`, compiles net9/10; runtime-unverified); ACR122U PC/SC when a reader is present.

What is real and identical everywhere: **BLE, Wi-Fi Direct, the HTTP/QUIC relay, and the WebRTC P2P transport (loopback-verified in all 8 languages)**, plus Signal Protocol security (X3DH + Double Ratchet), AODV routing, DTN store-and-forward, SOS broadcast, voice, and streaming.

**Honest status:** BLE + Wi-Fi Direct + relay are production-real; **WebRTC P2P is real and loopback-verified in all 8 languages** (two peers exchange bytes over a real ICE data channel — Rust confirmed on the `.201` Linux box with working UDP ICE); the NearLink / LoRa / NFC-on-Windows approximations are now real code that compiles (LoRa compile-verified in all 8, incl. Swift + C on the Mac build server; NearLink-Android also unit-tested) but is **runtime-unverified** — no hardware / 2-device RF test yet. They participate in the mesh in code; don't deploy those three expecting field-proven RF.

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

Beyond wire format and Signal, the **entire wire-service suite** — presence, heartbeat, profile sync, ephemeral-ID announce, pre-key exchange, channels, push-to-talk, screen share, call control, SOS acknowledgement, space breadcrumbs, forge announce, vault shard request, and bandwidth measurement (see [What you get](#what-you-get--every-service-in-every-language)) — is likewise implemented in all 8 languages and pinned to its own fixtures (`fixtures/presence/`, `fixtures/media/`, `fixtures/bandwidth/`, `fixtures/prekey/`, `fixtures/videocall/`, `fixtures/vaultshard/`, and siblings). No feature is C#-only at the protocol layer.

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

**Done (all 8 languages — the full wire-service suite):**
- ✅ **Every reserved packet type is now a real, byte-identical service in all 8 languages.** Presence beacon/query (21/22), heartbeat (10), profile sync (23), ephemeral-routing-ID announce (56), pre-key exchange (25/26), channels (7), push-to-talk (15), screen share (32), call control (27), SOS acknowledgement (6), space breadcrumbs (40), forge announce (41), vault shard request (42), and bandwidth measurement / ABMF (53/54/55). Each is a thin service (produce + handle + event) that the host wires to its Signal session and routing table; each is pinned to a shared cross-language fixture (`fixtures/presence/`, `fixtures/media/`, `fixtures/bandwidth/`, `fixtures/prekey/`, `fixtures/videocall/`, `fixtures/vaultshard/`, `fixtures/channels/`, `fixtures/profiles/`, `fixtures/heartbeat/`, `fixtures/erid/`, `fixtures/space/`, `fixtures/forge/`, `fixtures/sos/`) and exercised by per-language unit tests, with Swift and C verified on the macOS build server. See [What you get](#what-you-get--every-service-in-every-language).

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

## Frequently asked questions

**Does AetherNet work without the internet?**
Yes — it is offline-first. Devices talk directly over Bluetooth, Wi-Fi Direct, NearLink, or LoRa and relay messages hop-by-hop through other devices, with no internet connection, cell tower, or server required. When no live route exists, messages are held (delay-tolerant store-and-forward) for up to 72 hours until one opens.

**Is it end-to-end encrypted?**
Yes. AetherNet uses the Signal Protocol (X3DH key agreement plus the Double Ratchet over X25519) for end-to-end encryption, AES-256-GCM for message payloads, and Ed25519 signatures on every packet. Devices that relay a message cannot read it.

**What transports does it use?**
Bluetooth LE, Wi-Fi Direct, NearLink (SLE), a LoRa/CircleLink serial radio, an HTTP/QUIC relay, and WebRTC for direct internet peer-to-peer. The protocol automatically selects the lowest-power available transport per packet and falls back to the next.

**Which programming languages is it available in?**
Eight — C#, Rust, TypeScript, Python, Go, Kotlin, Swift, and C. Every implementation produces byte-identical wire packets, enforced by a shared cross-language fixture corpus in CI, so a packet built by one language is decoded unchanged by any other.

**How is it different from Meshtastic, Briar, or Bridgefy?**
Meshtastic is LoRa-only; AetherNet is multi-transport (Bluetooth + Wi-Fi + NearLink + LoRa) and carries voice, video, and streaming as well as messages. Briar is Android-only and routes over Tor; AetherNet is cross-platform and pure mesh. Unlike closed SDKs, AetherNet is MIT-licensed and implemented openly in eight languages. The comparison table above has the details.

**Is it production-ready?**
The protocol layer — wire format, Signal security, routing, DTN store-and-forward, and the full service suite — is implemented and tested across all eight languages. Radio transports are real where platform code exists (Bluetooth and Wi-Fi on Windows and Android, WebRTC everywhere) and field-unverified elsewhere pending hardware bring-up, which is tracked honestly in `OPEN_ISSUES.md`. Read the status notes in each section before deploying.

**What license is it under?**
MIT — free for commercial and open-source use. See [LICENSE](LICENSE).

**Who builds AetherNet?**
It is developed as the open protocol behind The Geek Network's mesh ecosystem, built in South Africa for communication that works with or without mobile data.

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

## Translations

This README is also maintained in the other languages listed in the language bar at the top of this file, under [`docs/i18n/`](docs/i18n/) — spanning European, East Asian, Middle Eastern, South Asian, Southeast Asian, and African languages, because a network built for people with no data should not have a front door only the well-connected can read. The **English version is the source of truth**: where a translation and the English text disagree, the English text is authoritative, and translations may lag it by a release or two. The protocol, code, fixtures, and behaviour described are identical no matter which language you read.
