# AetherNet

A serverless mesh protocol for phones. Devices reach each other over the radios they already have —
Bluetooth LE, Wi-Fi Direct, Wi-Fi Aware, NFC, LoRa, or the internet when there is one — with no towers,
no accounts and nobody in the middle.

```csharp
using AetherNet.Core;
using AetherNet.Messaging;
```

One reference brings the protocol: identity and addressing, sealed messaging, the transports and the radio
ladder that chooses between them, storage, content, voice and streaming, and **Aether Aware** — which names
the things around a phone and notices anything that keeps up with the person carrying it.

## What is not in here

Two things are their own package, because each arrives with weight you should choose rather than inherit:

| Package | Why |
|---|---|
| `AetherNet.WebRtc` | brings SIPSorcery with it |
| `AetherNet.Sqlite` | brings a native SQLite per platform |

And `AetherNet.Node` is for talking to **AetherNetService** — the node that owns a device's identity and
radios — rather than embedding the protocol yourself.

## Platforms

`net9.0` and `net10.0`, with Android and Windows builds that add the platform's own radios. Aether Aware is
`net10.0` and up.

MIT. Source at <https://github.com/bhengubv/aether-protocol>.
