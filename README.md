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
  │   Messaging · Streaming · Voice │
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

**Voice and streaming** — Adaptive bitrate adjusts to mesh conditions. Jitter buffering handles multi-hop latency. Group voice is supported.

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

Each demo creates simulated mesh nodes, establishes encrypted sessions, and demonstrates multi-hop message relay — real cryptography, no network hardware required.

**C#** (.NET 10 SDK)
```bash
dotnet run --project samples/Aether.Demo.Console
```

**Rust** (1.70+)
```bash
cd rust && cargo run
```

**TypeScript** (Node 18+, tsx)
```bash
cd typescript && npm install && npm run dev
```

**Python** (3.10+)
```bash
cd python && pip install -e . && python3 demo.py
```

**Go** (1.22+)
```bash
cd go && go run ./cmd/demo/main.go
```

**Kotlin** (JDK 17+, Gradle 8+)
```bash
cd kotlin && ./gradlew run
```

**Swift** (5.9+, macOS 13+ / iOS 16+)
```bash
cd swift && swift run aether-demo
```

**C** (CMake 3.16+, C11, libsodium)
```bash
cd c && mkdir -p build && cd build && cmake .. && make && ./aether-demo
```

## Project Structure

```
aether-protocol/
  src/
    Aether.Core/          Protocol models, constants, packet serialization
    Aether.Security/      Signal Protocol, Ed25519, packet signing
    Aether.Transport/     Transport abstractions, NearLink, in-process simulator
    Aether.Messaging/     Message handling and relay
    Aether.Storage/       DTN store-and-forward persistence
    Aether.Streaming/     Adaptive bitrate streaming
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
