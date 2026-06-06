# Protocol Spec

The full Aether Mesh Networking Protocol Specification (v2.0) lives at
[`docs/PROTOCOL_SPEC.md`](https://github.com/bhengubv/aether-protocol/blob/main/docs/PROTOCOL_SPEC.md)
in the repository.

## Why it is not duplicated here

The protocol spec is the source of truth and is reconciled against the implementation on
every release. Duplicating it inside the DocFX site means it drifts. This page links to the
authoritative file in the repository instead.

## What it covers

- Section 2: Packet Format — fixed-size header, variable-length payload, signature trailer
- Section 3: Routing — neighbour discovery, link metric, multi-hop forwarding
- Section 4: Key Exchange — X3DH (4 X25519 DHs) and Signal Double Ratchet
- Section 9: Delay-Tolerant Networking — 72-hour store-and-forward, replay protection
- Section 10: Video Streaming — wire-defined; codec/BitTorrent binding pending
- Section 11: Watch Together — synced playback over the mesh

## Reference implementations

| Concern                | C# file                                                            |
|------------------------|--------------------------------------------------------------------|
| Wire format            | `src/AetherMesh.Core/Protocol/PacketSerializer.cs`                     |
| Signal stack           | `src/AetherMesh.Security/Services/SignalProtocolService.cs`            |
| Routing                | `src/AetherMesh.Core/Routing/RoutingService.cs`                        |
| DTN                    | `src/AetherMesh.Core/Dtn/DtnService.cs`                                |

For interop verification across the eight reference languages, see
[Cross-Language Fixtures](fixtures.md).
