// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "AetherProtocol",
    platforms: [
        .macOS(.v13),
        .iOS(.v16)
    ],
    products: [
        .library(
            name: "AetherProtocol",
            targets: ["AetherProtocol"]
        ),
        .executable(
            name: "aether-demo",
            targets: ["AetherDemo"]
        )
    ],
    dependencies: [
        .package(url: "https://github.com/apple/swift-crypto.git", from: "3.0.0")
    ],
    targets: [
        .target(
            name: "AetherProtocol",
            dependencies: [
                .product(name: "Crypto", package: "swift-crypto")
            ],
            path: "Sources/AetherProtocol"
        ),
        .executableTarget(
            name: "AetherDemo",
            dependencies: ["AetherProtocol"],
            path: "Sources/AetherDemo"
        ),
        .testTarget(
            name: "AetherProtocolTests",
            dependencies: ["AetherProtocol"],
            path: "Tests"
        )
    ]
)
