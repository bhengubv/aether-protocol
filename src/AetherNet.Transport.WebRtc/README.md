# AetherNet.Transport.WebRtc

Direct peer-to-peer transport for [AetherNet](https://github.com/bhengubv/aether-protocol) over a
WebRTC `RTCDataChannel` — two nodes talk **directly over the internet with no central server in the
data path**.

Built on [SIPSorcery](https://github.com/sipsorcery-org/sipsorcery) (pure C#, cross-platform,
Android/iOS-capable). It implements `ITransportService`, so `TransportManager` slots it into the
transport ladder between the radio mesh (proximity, cheapest) and the QUIC/HTTP relay (last resort):
when a direct path can be negotiated the conversation flows peer-to-peer; otherwise the relay carries
it. `PowerCostRelative = 45`.

## How it stays serverless

Two things a WebRTC connection would normally reach a server for — and how AetherNet avoids both by
default:

1. **ICE (NAT traversal).** The default uses **no ICE servers**: host-candidate-only ICE forms a
   direct link on the same LAN or when a peer has a public address, so a node never contacts a
   STUN/TURN server. STUN/TURN remain **optional** — pass an explicit ICE-server list to opt in
   (see [Usage](#usage)) when you need to traverse NATs that host candidates alone can't.
2. **Signalling.** A WebRTC connection still needs a one-time **signalling** exchange (the SDP
   offer/answer plus ICE candidates). AetherNet carries that handshake over a channel you already
   have — the QUIC/HTTP relay, or even the radio mesh — instead of a dedicated signalling server.
   Once the channel is open, the conversation flows directly between the two devices.

- **`IWebRtcSignaling`** — the signalling seam.
- **`RelayWebRtcSignaling`** — carries the handshake over any `ITransportService` (the cross-device
  path), framed with a magic prefix + source-generated (AOT-safe) JSON; non-signalling bytes pass
  through untouched as ordinary app traffic.
- **`InMemoryWebRtcSignalingBus`** — ordered in-process signalling for simulations and tests.

## Install

```
dotnet add package AetherNet.Transport.WebRtc
```

## Usage

```csharp
services
    // 1. Choose a signalling carrier:
    .AddRelayWebRtcSignaling<QuicRelayTransportService>()   // cross-device: ride the relay
    // 2. Register the transport (joins TransportManager's ladder at PowerCostRelative 45):
    .AddWebRtcTransport(localUhid: "aether:alice:01");
```

ICE defaults to serverless (NO ICE servers), so a node never contacts a STUN/TURN server; direct
links form on the same LAN or when a peer has a public address, and for NAT traversal without a
server you route through the circuit-relay-v2 transport (peers relay for peers). Opt into public
STUN/TURN by passing an explicit list (an explicit **empty** list keeps host-candidate-only ICE):

```csharp
services.AddWebRtcTransport(
    localUhid: "aether:alice:01",
    iceServers: new[]
    {
        new RTCIceServer { urls = "stun:stun.l.google.com:19302" },
        new RTCIceServer { urls = "turn:turn.example.com", username = "u", credential = "p" },
    });
```

For a single-process host (simulations, a device holding several identities) use the in-process bus
instead of a relay:

```csharp
services.AddInMemoryWebRtcSignaling("aether:alice:01")
        .AddWebRtcTransport(localUhid: "aether:alice:01");
```

## License

MIT — see the repository root.
