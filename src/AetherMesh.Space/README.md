# AetherMesh.Space

Geo-pinned community noticeboards. A user drops a digital notice anchored to a geohash; passing devices auto-pull it, cache it, re-host it for other passersby. Fully offline. Use cases: campus events, taxi-rank price boards, market-stall listings, emergency alerts.

```bash
dotnet add package AetherMesh.Space
```

```csharp
using AetherMesh.Space;
using AetherMesh.Space.Models;

ISpaceService space = sp.GetRequiredService<ISpaceService>();

await space.DropAsync(
    location: GeoHash.Encode(latitude: -26.2041, longitude: 28.0473, precision: 6),
    content: utf8Bytes,
    type: BreadcrumbType.Notice,
    ttlHours: 72);

// On the receiving device
var nearby = await space.ScanAsync(centerGeohash: "kc4r0p", radiusCells: 1);
```

See [protocol-spec](https://github.com/bhengubv/aether-protocol/blob/main/docs/articles/protocol-spec.md)
for the wire format, and [formal/](https://github.com/bhengubv/aether-protocol/tree/main/formal)
for the machine-checked Petri net models that prove the safety and liveness
properties of every layer this package touches.
