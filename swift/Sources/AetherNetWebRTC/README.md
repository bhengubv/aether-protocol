# AetherNetWebRTC

Direct peer-to-peer transport for [AetherNet](https://github.com/bhengubv/aether-protocol) over a
WebRTC data channel — two nodes talk **directly over the internet with no central server in the data
path**. The Swift counterpart of `AetherNet.Transport.WebRtc` (C#/SIPSorcery) and
`go/transport/webrtc` (Go/pion).

Built on [libdatachannel](https://github.com/paullouisageneau/libdatachannel) — a portable C/C++
WebRTC implementation (Linux / macOS / Windows) — reached through the `CDataChannel` system-library
binding. It conforms to `TransportService`, so `rankTransports` slots it between the radio mesh
(proximity, cheapest) and the QUIC/HTTP relay (last resort). `powerCostRelative = 5`.

## How it stays serverless

Two things a WebRTC connection would normally reach a server for — and how AetherNet avoids both by
default:

1. **ICE (NAT traversal).** The default uses **no ICE servers**: host-candidate-only ICE forms a
   direct link on the same LAN or when a peer has a public address, so a node never contacts a
   STUN/TURN server. STUN/TURN remain **optional** — pass an explicit ICE-server list to opt in
   (see [Usage](#usage)) when you need to traverse NATs that host candidates alone can't.
2. **Signalling.** A WebRTC connection still needs a one-time **signalling** exchange (the SDP
   offer/answer plus ICE candidates). AetherNet carries that handshake over a channel you already
   have — the relay, or even the radio mesh — instead of a dedicated signalling server.

- **`WebRtcSignaling`** — the signalling seam (a protocol).
- **`InMemoryWebRtcSignalingBus`** — ordered, in-process signalling for simulations and tests.
- **`WebRtcSignal`** / **`WebRtcSignalType`** — one offer / answer / ICE-candidate message.

## The libdatachannel dependency

`CDataChannel` is a SwiftPM **system library** target: it binds `rtc/rtc.h` and links `datachannel`.
The header and library must be present at build/link time.

| Platform | Install |
| --- | --- |
| Linux   | `apt install libdatachannel-dev` (or build from source) |
| macOS   | `brew install libdatachannel` |
| Windows | `vcpkg install libdatachannel` (MSVC ABI — matches the Swift `*-windows-msvc` toolchain) |

If they are not on the default search paths, point the build at them:

```
swift build  -Xcc -I<prefix>/include  -Xlinker -L<prefix>/lib
swift test   -Xcc -I<prefix>/include  -Xlinker -L<prefix>/lib
```

or place a `datachannel.pc` on `PKG_CONFIG_PATH` (the target declares `pkgConfig: "datachannel"`).

## Usage

```swift
let bus = InMemoryWebRtcSignalingBus()                       // or a relay-backed WebRtcSignaling
let alice = WebRtcTransportService(
    localUhid: "aether:alice:01",
    signaling: await bus.endpoint("aether:alice:01"))         // serverless default: NO ICE servers (host-only)
alice.onDataReceived { peer, data in /* inbound bytes */ }
_ = await alice.sendAsync(peerUhid: "aether:bob:01", data: payload, cancellationToken: nil)
```

The default is serverless (NO ICE servers), so a node never contacts a STUN/TURN server; direct
links form on the same LAN or when a peer has a public address, and for NAT traversal without a
server you route through the circuit-relay-v2 transport (peers relay for peers). Opt into public
STUN/TURN by passing an explicit list (an explicit **empty** list keeps host-candidate-only ICE):

```swift
WebRtcTransportService(
    localUhid: "aether:alice:01",
    signaling: endpoint,
    iceServers: ["stun:stun.l.google.com:19302",
                 "turn:user:pass@turn.example.com:3478"])
```

## License

MIT — see the repository root.
