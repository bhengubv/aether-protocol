# AetherNet.Forge

Forge — the mesh-native package-cache extension. Proxies git / npm / pip / cargo / go through localhost:2301, caches each fetched artifact as an Aether content chunk, serves cache hits from peers in the mesh. Zero-data offline package install across a team or campus.

```bash
dotnet add package AetherNet.Forge
```

```csharp
using AetherNet.Forge;

IForgeService forge = sp.GetRequiredService<IForgeService>();

var entry = await forge.QueryAsync("npm:react@18.2.0");
if (entry is null)
{
    var bytes = await DownloadFromUpstream("https://registry.npmjs.org/react/-/react-18.2.0.tgz");
    entry = await forge.CacheAsync("npm:react@18.2.0", bytes, integrityHash: "sha512-...");
}

var cached = await forge.FetchAsync(entry.PackageId);
```

See [protocol-spec](https://github.com/bhengubv/aether-protocol/blob/main/docs/articles/protocol-spec.md)
for the wire format, and [formal/](https://github.com/bhengubv/aether-protocol/tree/main/formal)
for the machine-checked Petri net models that prove the safety and liveness
properties of every layer this package touches.
