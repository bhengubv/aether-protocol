# Aether

Share files, messages, and streams with people nearby. No WiFi. No mobile data. No sign-up.

Phones talk directly over Bluetooth and WiFi. Messages hop from device to device until they arrive. Everything is end-to-end encrypted. There is no server.

Think AirDrop — except it works with everyone, on every platform, even when nobody has internet.

[![MIT License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)
[![PRs Welcome](https://img.shields.io/badge/PRs-welcome-brightgreen.svg)](CONTRIBUTING.md)

Built in 8 languages so it runs everywhere — phones, laptops, tablets, even microcontrollers.

## Your Phone, Your Rules

The obvious question: "other people's data hops through my phone?"

Yes — like passing a sealed envelope. You carry it, but you can't open it, and you can't pretend you wrote it.

- **Can't read it** — every message is encrypted with AES-256-GCM inside a Signal Protocol session (X3DH key exchange + Double Ratchet). Only the recipient has the key.
- **Can't impersonate** — every node has an Ed25519 identity key. Every packet is signed. Forge one and the network drops it.
- **Nothing stored** — keys are ephemeral. Once a message is delivered, the relay forgets it existed. No metadata lingers.
- **No accounts** — no server, no phone number, no email, no tracking. You generate a keypair and you're on the network.

Same encryption as Signal and WhatsApp. Except there's no server in the middle.

## What You Can Build With It

- **Study groups** — share lecture notes, past papers, slides across campus. Mesh relay means nobody spends data. Messages wait up to 72 hours for a route (DTN store-and-forward).
- **Events** — discover what's happening nearby, found by the network, not an algorithm. Devices announce proximity over BLE and WiFi Direct — no cloud, no feed.
- **Safety** — SOS broadcast hits every phone in range, no cell tower needed. Flood algorithm, not routed — it reaches everyone within the mesh.
- **Private channels** — for your floor, your society, your crew. Membership-verified, channel-encrypted. Nobody outside the group can read or inject messages.
- **Marketplace** — sell textbooks to people walking distance away. Proximity discovery via geohash means you see what's nearby, not what's promoted.

## How It Works

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

The protocol picks the right transport per packet. Small control messages go over BLE. Bulk transfers use WiFi Direct. NearLink when available. You can add your own — implement `ITransportService` and the transport manager slots it in automatically.

Routing is AODV with a twist: every route reply (RREP) is signed by the destination node's Ed25519 key. No node can claim to be a destination it isn't.

When there's no live route, packets don't die. DTN store-and-forward holds them for up to 72 hours, carrying messages across gaps in the mesh until a path opens up.

Voice and streaming work too — adaptive bitrate adjusts to mesh conditions, jitter buffering smooths out multi-hop latency, and group voice sessions are supported natively.

## Implementations

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

All implementations produce wire-compatible packets. A message encrypted by the Rust node can be decrypted by the Swift node, relayed by the Python node, and delivered by the C node.

## Quickstart

```bash
git clone https://github.com/bhengubv/aether-protocol.git
cd aether-protocol
```

Then pick your language:

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

Each demo creates simulated mesh nodes, establishes encrypted sessions, and demonstrates multi-hop message relay — all with real cryptography, no network hardware required.

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
| **Briar** | Android-only, Tor-dependent | Cross-platform, pure mesh — no internet fallback needed |
| **Meshtastic** | LoRa only (30 kbps max) | Multi-transport (BLE + WiFi + NearLink), voice and streaming capable |
| **Reticulum** | Python, small community | 8 languages, wire-compatible across all of them |
| **libp2p** | Assumes internet backbone | True offline-first, works with zero infrastructure |
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

---

Built by [The Other Bhengu (Pty) Ltd t/a The Geek and Bhengu B.V.](https://thegeeknetwork.co.za)
