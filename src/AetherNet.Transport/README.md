# AetherNet.Transport

Cross-platform transport abstraction layer. In-process transport (for tests, demos, and same-process multi-node samples), simulated BLE / Wi-Fi Direct / NearLink transports, predictive transport selector (EWMA + Kalman ranking), FEC codecs (Polar SCL, Raptor RFC 5053, RLNC).

```bash
dotnet add package AetherNet.Transport
```

```csharp
using AetherNet.Transport.Services;

// In-process transport for tests
ITransportService transport = new InProcessTransportService(localUhid: "alice");
ITransportManager manager  = new TransportManager(new[] { transport });

// PredictiveTransportSelector ranks live transports by predicted throughput
var ranked = selector.Rank(availableTransports);
var chosen = ranked.First();
```

See [protocol-spec](https://github.com/bhengubv/aether-protocol/blob/main/docs/articles/protocol-spec.md)
for the wire format, and [formal/](https://github.com/bhengubv/aether-protocol/tree/main/formal)
for the machine-checked Petri net models that prove the safety and liveness
properties of every layer this package touches.
