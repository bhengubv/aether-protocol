# Introduction

Aether is a peer-to-peer mesh networking protocol with end-to-end encryption built in at the
packet layer. Devices talk directly over short-range links (Bluetooth, Wi-Fi Direct) and can
also connect over the internet via a real WebRTC data-channel transport — there is no central
server and no account. You generate an Ed25519 / X25519 keypair and you are on the network.

> **Transport reality:** real BLE and Wi-Fi Direct adapters exist on C#/Windows and Android;
> the real internet transport is WebRTC, built and tested on C#, Go, and Kotlin. NFC is
> Android-only, NearLink is HarmonyOS-only (unverified on-device), and LoRa is a stub
> everywhere. The six cross-language library ports (Rust, Go, TypeScript, Python, Kotlin, C)
> simulate their mesh transport in-process; WebRTC is the one real radio-or-wire transport
> several of them carry. See [`PROTOCOL_SPEC.md` §5.4](https://github.com/bhengubv/aether-protocol/blob/main/docs/PROTOCOL_SPEC.md).

This site documents the .NET / C# 10 reference implementation. The same wire format and
cryptographic stack is implemented in seven additional languages (Rust, Go, TypeScript,
Python, Kotlin, Swift, C) and pinned to a shared fixture corpus under `fixtures/`.

## Where to start

- **[Quickstart](quickstart.md)** — install the NuGet package, send a packet.
- **[Protocol Spec](protocol-spec.md)** — wire format, routing, key exchange, DTN.
- **[Threat Model](threat-model.md)** — adversaries, guarantees, residual risks.
- **[Cross-Language Fixtures](fixtures.md)** — proof of wire-format interop.
- **[API Reference](../api/index.md)** — generated from XML doc comments.

## Project layout

| Assembly                            | What it provides                                                |
|-------------------------------------|-----------------------------------------------------------------|
| `AetherNet.Core`                       | Wire format, routing, DTN, the protocol primitives              |
| `AetherNet.Security`                   | X3DH, Double Ratchet, identity, packet signing, recovery-phrase backup (BIP-39), BLE tracking-protection, panic-wipe, multi-device sync |
| `AetherNet.Messaging`                  | High-level send/receive of application messages                 |
| `AetherNet.Storage`                    | Encrypted at-rest storage of keys, sessions, queued packets     |
| `AetherNet.Transport`                  | Transport contract + in-process simulator; BLE / Wi-Fi Direct adapters are real on C#/Windows + Android (see `AetherNet.Transport.WebRtc` for the real internet transport) |
| `AetherNet.Streaming`                  | Adaptive secure streaming (video, large files)                  |
| `AetherNet.Voice`                      | Voice / Opus codec scaffolding                                  |
| `AetherNet.Content`                    | Content addressing, channels, watch-together                    |
| `AetherNet.DependencyInjection`        | `IServiceCollection` registration helpers                       |

Hop into the [API Reference](../api/index.md) to see what is in each.

## Status caveat

The C# reference ships the full one-time pre-key (OPK) pool that closes the
single-OPK concurrency hazard. The other seven languages are still on a single OPK at the
time of writing — see [OPEN_ISSUES.md](https://github.com/bhengubv/aether-protocol/blob/main/OPEN_ISSUES.md)
for the gap list. C ships only the X25519 + KDF_RK primitives, not full Signal session
machinery.
