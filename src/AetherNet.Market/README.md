# AetherNet.Market

Offline-capable peer-to-peer commerce. Combines Proof-of-Vicinity (PoV) anti-Sybil trust scoring with Space-pinned listings and Vault-escrowed documents. The PoV anti-Sybil proof in formal/pov-anti-sybil/ is the basis for the SARB Exempt 17 eKYC pathway in standards/sarb-exempt-17/.

```bash
dotnet add package AetherNet.Market
```

```csharp
using AetherNet.Market;
using AetherNet.Market.Models;

IPoVService pov = sp.GetRequiredService<IPoVService>();
IMarketService market = sp.GetRequiredService<IMarketService>();

// Vouch for someone (must be in BLE / NFC range)
await pov.IssueTokenAsync(subjectAetherTag: "KXJB7-MN2P4");

// Browse nearby listings
var listings = await market.BrowseNearbyAsync(GeoHash.Encode(myLat, myLon), radiusCells: 2);
```

See [protocol-spec](https://github.com/bhengubv/aether-protocol/blob/main/docs/articles/protocol-spec.md)
for the wire format, and [formal/](https://github.com/bhengubv/aether-protocol/tree/main/formal)
for the machine-checked Petri net models that prove the safety and liveness
properties of every layer this package touches.
