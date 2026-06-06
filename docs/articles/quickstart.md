# Quickstart

> The canonical Quickstart lives at
> [`docs/QUICKSTART.md`](https://github.com/bhengubv/aether-protocol/blob/main/docs/QUICKSTART.md)
> in the repository. This page is a thin wrapper so the document is reachable from the API
> reference site navigation. If the canonical file does not yet exist, follow the steps
> below.

## Install

```bash
dotnet add package AetherMesh.Core
dotnet add package AetherMesh.Security
dotnet add package AetherMesh.Messaging
dotnet add package AetherMesh.DependencyInjection
```

## Register

```csharp
using Microsoft.Extensions.DependencyInjection;
using AetherMesh.DependencyInjection;

var services = new ServiceCollection();
services.AddAetherMeshCore();
services.AddAetherMeshSecurity();
services.AddAetherMeshMessaging();

var provider = services.BuildServiceProvider();
```

See <xref:AetherMesh.DependencyInjection> in the API reference for the full extension-method
surface.

## Send a packet

The high-level entry point is `IMessagingService` in `AetherMesh.Messaging`. Look up the
interface at <xref:AetherMesh.Messaging.IMessagingService> for the current method signatures —
the XML doc comments are the authoritative description.

## Where next

- [Protocol Spec](protocol-spec.md) — for understanding the wire format.
- [Threat Model](threat-model.md) — before deploying to production.
- [API Reference](../api/index.md) — for every public type.
