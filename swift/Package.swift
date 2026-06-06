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
        .executableTarget(
            name: "AetherNetDemo",
            dependencies: ["AetherNetProtocol"],
            path: "Sources/AetherNetDemo"
        ),
        .testTarget(
            name: "AetherNetProtocolTests",
            dependencies: ["AetherNetProtocol"],
            path: "Tests"
        )
    ]
)
