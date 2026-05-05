---
_layout: landing
title: Aether Protocol
---

# Aether Protocol

**Signal-Protocol-style E2EE mesh networking for .NET.**

Share files, messages, and streams with people nearby. No WiFi, no mobile data, no sign-up.
Aether reference implementation in C# / .NET 10, with cross-language interoperability across
8 languages (C#, Rust, Go, TypeScript, Python, Kotlin, Swift, C).

<div class="cta-row" style="margin-top: 2rem;">

[Quickstart](articles/quickstart.md){ .button .primary }
[API Reference](api/index.md){ .button }
[Source on GitHub](https://github.com/bhengubv/aether-protocol){ .button }

</div>

---

## Status

| Component                      | Status        | Notes                                           |
|--------------------------------|---------------|-------------------------------------------------|
| Wire format (8-language parity)| Implemented   | 10 named fixture cases under `fixtures/expected/`|
| X25519 key exchange            | Implemented   | All 8 reference languages                       |
| X3DH (Signal handshake)        | Implemented   | All 8 languages; pinned to fixtures             |
| Double Ratchet                 | Implemented   | DH ratchet + 0x01/0x02 chain ratchet            |
| One-time pre-key pool (OPKs)   | C# only       | Default 100 OPKs; other languages use single OPK|
| Routing (mesh + DTN)           | Implemented   | 72-hour delay-tolerant store-and-forward        |
| AES-256-GCM payload encryption | Implemented   | All 8 languages                                 |
| Ed25519 packet signing         | Implemented   | Forged packets dropped by network               |
| Video streaming pipeline       | Wire-defined  | Codec/BitTorrent/ChipIn binding pending         |
| Watch Together (synced playback)| Wire-defined | Pending playback adapter binding                |
| Voice / opus codec             | Scaffolded    | API surface only                                |

The C# reference is authoritative wherever this site and an individual language port diverge.
See [OPEN_ISSUES.md](https://github.com/bhengubv/aether-protocol/blob/main/OPEN_ISSUES.md)
for the residual gap list.

---

## What it is

A peer-to-peer mesh networking protocol where every packet is end-to-end encrypted with the
Signal Double Ratchet, every routing decision is local, and every device on the mesh can be
the relay that gets a packet to its destination. The transport is Bluetooth, WiFi Direct, or
NearLink — never the internet.

Think AirDrop, but cross-platform, multi-hop, and built on the same cryptographic primitives
that secure WhatsApp and Signal.

## What it is not

Aether is not a routed VPN, not a blockchain, not a social network. It does not require
accounts, phone numbers, or email addresses — you generate a keypair and you are on the
network. Aether is also not a finished product: §10 (Video Streaming) and §11
(Watch Together) of the [protocol spec](articles/protocol-spec.md) are wire-defined but the
codec / BitTorrent / ChipIn binding is still in progress.

---

## License

MIT — see [LICENSE](https://github.com/bhengubv/aether-protocol/blob/main/LICENSE).
Copyright 2026 The Other Bhengu (Pty) Ltd t/a The Geek and Bhengu B.V.
