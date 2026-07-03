// swift-tools-version:5.9
import PackageDescription
import Foundation

// ─── Optional WebRTC P2P transport (libdatachannel) ────────────────────────────
// OFF by default, mirroring the C build's `-DAETHERNET_WITH_WEBRTC` option
// (see c/CMakeLists.txt). The real RTCDataChannel transport binds libdatachannel
// (https://github.com/paullouisageneau/libdatachannel — pinned to v0.22.4 by the C
// build), which drags in a sizeable native dependency tree (libjuice + usrsctp +
// MbedTLS) that not every consumer of the protocol needs. Keeping it opt-in means
// the default `swift build` / `swift test` — and the macos-14 CI gate, which
// provisions no extra native libraries — stay dependency-free and build the core
// protocol plus its full test suite without libdatachannel present.
//
// Enable the AetherNetWebRTC target, its library product, and the WebRTC test
// target by setting the env var (accepts 1/on/true/yes, case-insensitive):
//
//   AETHERNET_WITH_WEBRTC=1 swift build
//   AETHERNET_WITH_WEBRTC=1 swift test
//
// With the flag on, libdatachannel's header (rtc/rtc.h) and the `datachannel` link
// library must be discoverable. Install via the platform package manager or a
// source build, and if they are off the default search paths point the build at
// them, e.g.:
//
//   AETHERNET_WITH_WEBRTC=1 swift build \
//     -Xcc -I<prefix>/include -Xlinker -L<prefix>/lib
//
// or place a `datachannel.pc` on PKG_CONFIG_PATH (the CDataChannel target's
// pkgConfig name). Note: SwiftPM may cache a prior manifest evaluation — run
// `swift package reset` (or clear .build) when toggling the flag in place.
let webRTCEnabled: Bool = {
    guard let raw = ProcessInfo.processInfo.environment["AETHERNET_WITH_WEBRTC"] else { return false }
    switch raw.lowercased() {
    case "1", "on", "true", "yes": return true
    default: return false
    }
}()

var products: [Product] = [
    .library(
        name: "AetherNetProtocol",
        targets: ["AetherNetProtocol"]
    ),
    // The transport-agnostic WebRTC signalling carrier (SDP/ICE offer/answer framing over any
    // byte channel) has NO libdatachannel dependency — it imports only Foundation. Like the C
    // SDK's carrier, it lives in an UNCONDITIONAL library so it (and its acceptance test) build
    // and run on the DEFAULT `swift build` / `swift test`. Only the real libdatachannel P2P
    // transport (AetherNetWebRTC, below) stays gated behind AETHERNET_WITH_WEBRTC.
    .library(
        name: "AetherNetWebRTCSignaling",
        targets: ["AetherNetWebRTCSignaling"]
    ),
    .executable(
        name: "aethernet-demo",
        targets: ["AetherNetDemo"]
    )
]

var targets: [Target] = [
    .target(
        name: "AetherNetProtocol",
        dependencies: [
            .product(name: "Crypto", package: "swift-crypto")
        ],
        path: "Sources/AetherNetProtocol"
    ),
    // Foundation-only WebRTC signalling carrier: WebRtcSignal / WebRtcSignaling /
    // RelayWebRtcSignaling. Always built (no CDataChannel), so the SDP/ICE handshake plumbing is
    // available to every consumer and testable without the native lib.
    .target(
        name: "AetherNetWebRTCSignaling",
        dependencies: ["AetherNetProtocol"],
        path: "Sources/AetherNetWebRTCSignaling"
    ),
    .executableTarget(
        name: "AetherNetDemo",
        dependencies: ["AetherNetProtocol"],
        path: "Sources/AetherNetDemo"
    ),
    .testTarget(
        name: "AetherNetProtocolTests",
        dependencies: ["AetherNetProtocol"],
        path: "Tests",
        // The WebRTC-signalling and WebRTC-P2P tests live in their own targets; keep them out of
        // the core protocol test target. WebRTCSignaling has its own always-built test target
        // (below); WebRTC needs the AetherNetWebRTC + libdatachannel dependency and is gated. The
        // exclude also keeps both dirs from becoming "unhandled" files when the WebRTC test target
        // is gated out.
        exclude: ["WebRTC", "WebRTCSignaling"]
    ),
    // Always-built acceptance test for the Foundation-only carrier: round-trip + byte-identity +
    // non-signalling-ignored. Runs on the DEFAULT `swift test` (no libdatachannel), mirroring the
    // C SDK's carrier test that runs on every build.
    .testTarget(
        name: "AetherNetWebRTCSignalingTests",
        dependencies: ["AetherNetWebRTCSignaling"],
        path: "Tests/WebRTCSignaling"
    )
]

// The WebRTC product/target/test-target only exist when the opt-in flag is set, so
// the default build never references CDataChannel and therefore never needs rtc/rtc.h.
if webRTCEnabled {
    products.append(
        .library(
            name: "AetherNetWebRTC",
            targets: ["AetherNetWebRTC"]
        )
    )
    targets.append(contentsOf: [
        // libdatachannel C API binding. The header (rtc/rtc.h) and the `datachannel`
        // library must be discoverable at build/link time — see the notes at the top
        // of this file for how to point the build at a non-default prefix.
        .systemLibrary(
            name: "CDataChannel",
            path: "Sources/CDataChannel",
            pkgConfig: "datachannel"
        ),
        // Real WebRTC peer-to-peer transport mirroring the C# (SIPSorcery) and Go (pion)
        // implementations: a TransportService over an RTCDataChannel, with SDP/ICE signalling
        // carried by an injected WebRtcSignaling channel (no central server). The signalling model
        // and carrier live in the always-built AetherNetWebRTCSignaling target; this gated target
        // holds ONLY the libdatachannel-backed transport (WebRtcTransportService + WebRtcPeerLink).
        .target(
            name: "AetherNetWebRTC",
            dependencies: [
                "AetherNetProtocol",
                "AetherNetWebRTCSignaling",
                "CDataChannel"
            ],
            path: "Sources/AetherNetWebRTC",
            exclude: ["README.md"]
        ),
        .testTarget(
            name: "AetherNetWebRTCTests",
            dependencies: ["AetherNetWebRTC", "AetherNetWebRTCSignaling", "AetherNetProtocol"],
            path: "Tests/WebRTC"
        )
    ])
}

let package = Package(
    name: "AetherNetProtocol",
    platforms: [
        .macOS(.v13),
        .iOS(.v16)
    ],
    products: products,
    dependencies: [
        .package(url: "https://github.com/apple/swift-crypto.git", "2.0.0"..<"2.1.0")
    ],
    targets: targets
)
