# AetherNet.Node

The **Aether Node Service** bind contract — the platform-neutral surface by which an app binds to the device's one AetherNet node instead of embedding the stack.

One device has one node identity (one Ed25519 key, one AetherTag). When every app links the full stack in-process, every app mints its own key and presents a *different* tag. `AetherNet.Node` is the contract that lets apps **bind** to a single installed node instead: they get `Sign`, addressing, messaging and presence — never the private key — so every app on the device presents the **same tag by construction**.

This package is the **contract only** — `IAetherNodeClient`, its DTOs, and the versioned handshake. The reference host, the client SDK, and the platform (Android AIDL) binding are separate. See [`docs/aether-node-service.md`](https://github.com/bhengubv/aether-protocol/blob/main/docs/aether-node-service.md).

## What's here

- `IAetherNodeClient` / `IAetherNodeEvents` — the bind surface (Task-based, callback-driven, marshal-safe).
- `NodeLinkStatus` / `RadioStatus`, `InboundMessage`, `OutboundResult` — DTOs.
- `AetherNodeHello` / `AetherNodeHelloAck` + `AetherNodeHandshake` — versioned handshake and capability negotiation.
- `GrantState` / `AppGrant` — the per-app grant model.
- `AetherNodeErrorCode` / `AetherNodeException` — the typed error contract that preserves *unavailable ≠ absent*.

The handshake wire format is pinned byte-identical across the eight language SDKs by `tests/cross-language/node-fixtures.json`.

MIT © The Other Bhengu (Pty) Ltd t/a The Geek Network.
