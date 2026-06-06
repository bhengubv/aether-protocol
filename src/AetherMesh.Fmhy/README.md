# AetherMesh.Fmhy

FMHY (Free Media Heck Yeah) catalogue service — an opinionated, cross-language seed loader for community-curated content categories. Implements the AetherMesh convention of shipping a markdown catalogue per language, parsed once at startup.

```bash
dotnet add package AetherMesh.Fmhy
```

```csharp
using AetherMesh.Fmhy;

IFmhyCatalogueService catalogue = sp.GetRequiredService<IFmhyCatalogueService>();

await catalogue.LoadSeedAsync(); // pulls categories + entries from the bundled markdown

var entries = await catalogue.SearchAsync("educational");
```

See [protocol-spec](https://github.com/bhengubv/aether-protocol/blob/main/docs/articles/protocol-spec.md)
for the wire format, and [formal/](https://github.com/bhengubv/aether-protocol/tree/main/formal)
for the machine-checked Petri net models that prove the safety and liveness
properties of every layer this package touches.
