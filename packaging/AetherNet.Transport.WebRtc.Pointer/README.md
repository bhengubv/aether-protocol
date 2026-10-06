# AetherNet.Transport.WebRtc

**This package now ships inside [AetherNet.WebRtc](https://www.nuget.org/packages/AetherNet.WebRtc).**

AetherNet used to publish one package per project — thirty-two of them, versioned in lockstep, so the
split bought nothing a consumer could act on. It is now four packages, and `AetherNet.Transport.WebRtc`'s assembly travels
inside `AetherNet.WebRtc`.

**You do not have to change anything.** This package carries no assemblies of its own; it depends on
`AetherNet.WebRtc`, so restoring `AetherNet.Transport.WebRtc` gives you exactly the code it always did, at the current version.

If you would rather reference the bundle directly:

```xml
<PackageReference Include="AetherNet.WebRtc" Version="3.1.1" />
```

Namespaces are unchanged either way — it is the same assembly.

MIT. Source at <https://github.com/bhengubv/aether-protocol>.
