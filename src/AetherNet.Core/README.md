# AetherNet.Core

Domain models, interfaces, and constants shared by every other AetherNet package — MeshPacket, AetherNetTag, IDtnService, IRoutingService, IHandshakeService, ISosBroadcastService, INodeReputationService, and the extensibility seams (IAetherAiProvider, IBiometricProvider, IAetherTelemetry, IAetherSecurityAudit, IAetherIncentiveProvider). Pulled in transitively by every other AetherNet package.

```bash
dotnet add package AetherNet.Core
```

```csharp
using AetherNet.Core;
using AetherNet.Models;
using AetherNet.Protocol;

// AetherNetTag = cryptographic identity, human-readable (e.g. KXJB7-MN2P4)
var tag = AetherNetTag.FromPublicKey(myEd25519PublicKey);

// MeshPacket = the wire-format unit. Bytes 0..7 are the packet header,
// bytes 8..27 are the routing envelope.
var packet = new MeshPacket {
    Id              = Guid.NewGuid(),
    Type            = PacketType.Data,
    SourceUhid      = tag.Uhid,
    DestinationUhid = peerTag.Uhid,
    Payload         = encryptedBytes,
    ProtocolVersion = 2,
};
```

See [protocol-spec](https://github.com/bhengubv/aether-protocol/blob/main/docs/articles/protocol-spec.md)
for the wire format, and [formal/](https://github.com/bhengubv/aether-protocol/tree/main/formal)
for the machine-checked Petri net models that prove the safety and liveness
properties of every layer this package touches.
