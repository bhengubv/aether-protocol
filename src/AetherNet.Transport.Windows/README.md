# AetherNet.Transport.Windows

Windows-specific transports — Win32 BLE GATT, Wi-Fi Direct via Windows Runtime, NearLink stub (Huawei OEM-only), NFC stub, QUIC relay over HTTP/3. Requires Windows 10 build 19041+ (UWP minimum). Add this only on a Windows host.

```bash
dotnet add package AetherNet.Transport.Windows
```

```csharp
using AetherNet.Transport.Windows;
using Microsoft.Extensions.DependencyInjection;

services.AddAetherNetProtocol(opts => opts.LocalUhid = "KXJB7-MN2P4")
        .AddSignalProtocol()
        .AddRouting()
        .AddWindowsTransports(httpRelayBaseUrl: "https://relay.aethernet.network");
```

See [protocol-spec](https://github.com/bhengubv/aether-protocol/blob/main/docs/articles/protocol-spec.md)
for the wire format, and [formal/](https://github.com/bhengubv/aether-protocol/tree/main/formal)
for the machine-checked Petri net models that prove the safety and liveness
properties of every layer this package touches.
