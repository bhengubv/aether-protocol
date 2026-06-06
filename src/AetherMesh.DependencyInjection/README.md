# AetherMesh.DependencyInjection

Microsoft.Extensions.DependencyInjection wiring for AetherMesh. One fluent builder registers the whole protocol stack: routing, DTN, Signal Protocol, content, streaming, voice, messaging, reputation, handshake. Each capability is opt-in — hosts pick what they ship.

```bash
dotnet add package AetherMesh.DependencyInjection
```

```csharp
using AetherMesh.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

services.AddAetherMeshProtocol(opts => opts.LocalUhid = "KXJB7-MN2P4")
        .AddSignalProtocol()
        .AddRouting()
        .AddDtn()
        .AddSosBroadcast()
        .AddMessaging()
        .AddStreaming()
        .AddHandshake()
        .AddInProcessTransport("KXJB7-MN2P4")
        .AddHealthChecks();
```

See [protocol-spec](https://github.com/bhengubv/aether-protocol/blob/main/docs/articles/protocol-spec.md)
for the wire format, and [formal/](https://github.com/bhengubv/aether-protocol/tree/main/formal)
for the machine-checked Petri net models that prove the safety and liveness
properties of every layer this package touches.
