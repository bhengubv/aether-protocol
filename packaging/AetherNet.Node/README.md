# AetherNet.Node

Talk to **AetherNetService** — the service that owns a device's one identity and its radios — instead of
embedding the protocol yourself.

```csharp
var tag = await node.GetTagAsync();          // this device's AetherTag
var link = await node.GetLinkAsync();        // its radios, and what they are reaching
var around = await node.GetAwareAsync();     // what Aether Aware hears nearby
```

A device has **one** identity, and it belongs to the device rather than to any app. An app asks the node for
it. That is the whole idea: install two apps and they are the same person on the mesh, because neither of
them minted anything.

Carries the node contract, a client, the host seam, the wire format, and the two ways a device answers — an
Android binder and a Windows named pipe. Also **Quiet help**: asking the people you chose for help without a
sound, and sharing a trail of where you are until you say you are safe.

Needs `AetherNet`, which it depends on rather than duplicating.

MIT. Source at <https://github.com/bhengubv/aether-protocol>.
