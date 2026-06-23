# AetherNet.Transport.Windows

Windows-specific transports — Win32 BLE GATT, Wi-Fi Direct via Windows Runtime, NearLink as a real SSAP-over-BLE-GATT central (`WinNearLinkBleTransportService`; genuine SLE silicon is HarmonyOS-only), NFC as a real BLE-GATT central with an RSSI −40 dBm proximity gate (`WinNfcBleTransportService`; Windows removed its NFC P2P API in Win 11), QUIC relay over HTTP/3. Requires Windows 10 build 19041+ (UWP minimum). Add this only on a Windows host.

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
