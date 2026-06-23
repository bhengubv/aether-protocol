// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "AetherNetProtocol",
    platforms: [
        .macOS(.v13),
        .iOS(.v16)
    ],
    products: [
        .library(
            name: "AetherNetProtocol",
            targets: ["AetherNetProtocol"]
        ),
        .library(
            name: "AetherNetWebRTC",
            targets: ["AetherNetWebRTC"]
        ),
        .executable(
            name: "aethernet-demo",
            targets: ["AetherNetDemo"]
        )
    ],
    dependencies: [
        .package(url: "https://github.com/apple/swift-crypto.git", "2.0.0"..<"2.1.0")
    ],
    targets: [
        .target(
            name: "AetherNetProtocol",
            dependencies: [
                .product(name: "Crypto", package: "swift-crypto")
            ],
            path: "Sources/AetherNetProtocol"
        ),
        // libdatachannel C API binding. The header (rtc/rtc.h) and the `datachannel` library must be
        // discoverable at build/link time — install via the platform package manager:
        //   Linux:   apt install libdatachannel-dev   (or build from source)
        //   macOS:   brew install libdatachannel
        //   Windows: vcpkg install libdatachannel       (MSVC ABI, matches the Swift toolchain)
        // If they are not on the default search paths, point the build at them, e.g.:
        //   swift build -Xcc -I<prefix>/include -Xlinker -L<prefix>/lib
        // or place `datachannel.pc` on PKG_CONFIG_PATH.
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
        .executableTarget(
            name: "AetherNetDemo",
            dependencies: ["AetherNetProtocol"],
            path: "Sources/AetherNetDemo"
        ),
        .testTarget(
            name: "AetherNetProtocolTests",
            dependencies: ["AetherNetProtocol"],
            path: "Tests",
            // The WebRTC tests live in their own target (they need the AetherNetWebRTC + libdatachannel
            // dependency); keep them out of the core protocol test target.
            exclude: ["WebRTC"]
        ),
        .testTarget(
            name: "AetherNetWebRTCTests",
            dependencies: ["AetherNetWebRTC", "AetherNetProtocol"],
            path: "Tests/WebRTC"
        )
    ]
)
