# AetherNet.Content

**This package now ships inside [AetherNet](https://www.nuget.org/packages/AetherNet) — one package for all of
AetherNet.**

AetherNet used to publish one package per project: thirty-two of them, versioned in lockstep, so the split
bought nothing a consumer could act on. At 3.1.x it became four, which was still three more than anybody
asked for. From **3.2.0 it is one**, and `AetherNet.Content`'s assembly travels inside it.

**You do not have to change anything.** This package carries no assemblies of its own; it depends on
`AetherNet`, so restoring `AetherNet.Content` gives you exactly the code it always did, at the current version.

To reference the one package directly instead:

```xml
<PackageReference Include="AetherNet" Version="3.2.0" />
```

Namespaces are unchanged either way — it is the same assembly. What does change, and is worth knowing: one
package means `AetherNet` brings SIPSorcery and a native SQLite whether you use them or not. In return the
WebRTC transport and the SQLite-backed stores now reach Android and Windows, which the separate packages
never offered at all.

MIT. Source at <https://github.com/bhengubv/aether-protocol>.
