# Aether Protocol

[![MIT License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)
[![PRs Welcome](https://img.shields.io/badge/PRs-welcome-brightgreen.svg)](CONTRIBUTING.md)

**The end of ISP dependency.** Aether is a mesh networking protocol that lets devices communicate directly -- no internet, no servers, no permission needed.

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

## What is Aether?

Aether is an open-source mesh networking protocol built in C#/.NET. It enables devices to form ad-hoc networks and communicate securely without any centralized infrastructure.

Every message is end-to-end encrypted using the Signal Protocol. Every packet is signed with Ed25519. Every relay is verified. And it all works offline.

### Features

- **End-to-end encryption** -- Signal Protocol (X3DH key exchange + symmetric ratchet + AES-256-GCM)
- **Multi-transport** -- BLE, Wi-Fi Direct, NearLink, or bring your own transport
- **AODV routing** -- Reactive route discovery with signed route replies (no spoofing)
- **Delay-tolerant networking** -- Store-and-forward for 72 hours when no route exists
- **SOS broadcast** -- Emergency flood algorithm that reaches every node in range
- **Voice and streaming capable** -- Adaptive bitrate, jitter buffering, group voice
- **Replay protection** -- Nonce deduplication + 5-minute timestamp freshness window
- **Extensible** -- Add new transports, plug in your own backend, or run fully standalone

## 5-Minute Quickstart

```bash
git clone https://github.com/thegeeknetwork/aether-protocol.git
cd aether-protocol
dotnet run --project samples/Aether.Demo.Console
```

The demo creates three simulated mesh nodes, establishes encrypted sessions, and demonstrates multi-hop message relay -- all with real cryptography, no network hardware required.

## Project Structure

```
aether-protocol/
  src/
    Aether.Core/          Protocol models, constants, packet serialization
    Aether.Security/      Signal Protocol, Ed25519, packet signing
    Aether.Transport/     Transport abstractions, NearLink, in-process simulator
  samples/
    Aether.Demo.Console/  Interactive demo -- the "hello world" of mesh networking
  tests/
    Aether.Security.Tests/
    Aether.Protocol.Tests/
  docs/
    PROTOCOL_SPEC.md      RFC-style protocol specification
```

## How It Compares

| Protocol | Limitation | Aether Advantage |
|----------|-----------|-----------------|
| **Briar** | Android-only, Tor-dependent | Cross-platform (.NET MAUI), pure mesh |
| **Meshtastic** | LoRa only (30 kbps max) | Multi-transport (BLE + WiFi + NearLink), voice/streaming capable |
| **Reticulum** | Python, small community | C#/.NET, NuGet ecosystem, student-friendly |
| **libp2p** | Assumes internet backbone | True offline-first, works with zero infrastructure |
| **Yggdrasil** | Overlay network, needs internet | Physical-layer mesh, works without internet |

## Architecture

**Protocol Layer** -- Packet format, AODV routing, reliability, serialization.

**Security Layer** -- Ed25519 identity keys, Signal Protocol sessions, packet signing with nonce deduplication, replay protection.

**Transport Layer** -- Pluggable transport backends. Ships with BLE, Wi-Fi Direct, and NearLink interfaces. The `ITransportService` abstraction lets anyone add new physical layers.

**Extension Points** -- `IAetherIncentiveProvider`, `IAetherBackendClient`, `IAetherFeatureFlagProvider` -- all with no-op defaults so the protocol works standalone.

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

## Building

```bash
dotnet build
dotnet test
dotnet run --project samples/Aether.Demo.Console
```

Requires .NET 10 SDK.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines.

## Security

See [SECURITY.md](SECURITY.md) for responsible disclosure policy.

## License

MIT License. See [LICENSE](LICENSE).

---

Built by [The Other Bhengu (Pty) Ltd t/a The Geek and Bhengu B.V.](https://thegeeknetwork.co.za). Because communication should be as abundant as the aether.
