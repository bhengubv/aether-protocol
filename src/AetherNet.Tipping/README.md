# AetherNet.Tipping

Optional **incentive / tipping** layer for [AetherNet](https://github.com/bhengubv/aether-protocol) — the offline-first mesh protocol. Relay and gateway nodes can be rewarded for forwarding traffic; this package carries the **generic, currency-agnostic** surface for that.

It provides:

- the `TipPacket` (packet type **24**) wire payload + send/receive path (`IMeshTipService` lives in `AetherNet.Core`),
- the **settlement seam** — a host plugs its wallet in by implementing `IAetherNetIncentiveProvider.SettleMeshTipAsync`; the default is a no-op (a node accepts and relays tip packets but settles nothing),
- in-tree services that queue and batch-sync incentive/reward events.

**The amount is just a number.** No currency is baked into the wire — settlement currency is entirely the host's concern. A wallet-backed deployment supplies its own `SettleMeshTipAsync` and its own backend; the protocol only carries the signal.

## Install

```
dotnet add package AetherNet.Tipping
```

## Quick start

```csharp
using AetherNet.DependencyInjection;

services.AddAetherNet(b => b
    .AddSignalProtocol()
    .AddRouting()
    .AddMeshTip()      // generic tip wire surface (TipPacket 24)
    .AddTipping());    // queue + batch-sync + settlement seam

// Reward a relay. `amount` is the host's own unit — nothing currency-specific on the wire:
await meshTip.SendTipAsync(recipientUhid, amount: 0.10m, trafficType: "message-relay");
```

The `TipPacket` wire format and signing are **byte-identical across every AetherNet implementation** (C#, Go, Python, TypeScript, Kotlin, Swift, Rust, C, ArkTS), verified against shared cross-language fixtures.

MIT licensed.
