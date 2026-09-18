# AetherNet.Node.Host

The reference **host** for the [Aether Node Service](https://github.com/bhengubv/aether-protocol/blob/main/docs/aether-node-service.md).

`AetherNodeService` implements `IAetherNodeClient` over the device's real identity and mesh: it maps `INodeIdentity` (tag, public key, sign — the key never leaves), a **messaging seam** (`INodeMessaging`) and a **presence seam** (`INodeLinkSource`) to the bind contract. It keeps the wire/Signal detail out of the contract, and a cross-process host (an Android bound `Service`) wraps this same object.

- `AetherNodeService` — the in-process `IAetherNodeClient`.
- `INodeMessaging` / `INodeLinkSource` — the seams a platform provides over `IMessagingService` and the SDK radios.
- `IGrantStore` / `InMemoryGrantStore` — where the node remembers which apps it has linked (a cross-process host enforces grants here).
- `AddAetherNode()` — DI registration.

A locked node surfaces as `AetherNodeException(NodeUnavailable)` — never as an absent identity.

MIT © The Other Bhengu (Pty) Ltd t/a The Geek Network.
