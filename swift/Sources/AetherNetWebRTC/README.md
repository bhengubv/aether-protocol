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

A WebRTC connection still needs a one-time **signalling** exchange (the SDP offer/answer plus ICE
candidates). AetherNet carries that handshake over a channel you already have — the relay, or even
the radio mesh — instead of a dedicated signalling server.

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
    signaling: await bus.endpoint("aether:alice:01"))         // STUN default; pass [] for host-only ICE
alice.onDataReceived { peer, data in /* inbound bytes */ }
_ = await alice.sendAsync(peerUhid: "aether:bob:01", data: payload, cancellationToken: nil)
```

Pass an explicit ICE list to override; an explicit **empty** list forces host-candidate-only ICE
(same-LAN / loopback, no STUN/TURN):

```swift
WebRtcTransportService(
    localUhid: "aether:alice:01",
    signaling: endpoint,
    iceServers: ["stun:stun.l.google.com:19302",
                 "turn:user:pass@turn.example.com:3478"])
```

## License

MIT — see the repository root.
