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
    .executableTarget(
        name: "AetherNetDemo",
        dependencies: ["AetherNetProtocol"],
        path: "Sources/AetherNetDemo"
    ),
    .testTarget(
        name: "AetherNetProtocolTests",
        dependencies: ["AetherNetProtocol"],
        path: "Tests",
        // The WebRTC tests live in their own target (they need the AetherNetWebRTC +
        // libdatachannel dependency); keep them out of the core protocol test target.
        // The exclude also keeps Tests/WebRTC from becoming "unhandled" files when the
        // WebRTC test target is gated out below.
        exclude: ["WebRTC"]
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
        // carried by an injected WebRtcSignaling channel (no central server).
        .target(
            name: "AetherNetWebRTC",
            dependencies: [
                "AetherNetProtocol",
                "CDataChannel"
            ],
            path: "Sources/AetherNetWebRTC",
            exclude: ["README.md"]
        ),
        .testTarget(
            name: "AetherNetWebRTCTests",
            dependencies: ["AetherNetWebRTC", "AetherNetProtocol"],
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
