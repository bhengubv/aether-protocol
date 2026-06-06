// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "AetherMeshProtocol",
    platforms: [
        .macOS(.v13),
        .iOS(.v16)
    ],
    products: [
        .library(
            name: "AetherMeshProtocol",
            targets: ["AetherMeshProtocol"]
        ),
        .executable(
            name: "aethermesh-demo",
            targets: ["AetherMeshDemo"]
        )
    ],
    dependencies: [
        .package(url: "https://github.com/apple/swift-crypto.git", "2.0.0"..<"2.1.0")
    ],
    targets: [
        .target(
            name: "AetherMeshProtocol",
            dependencies: [
                .product(name: "Crypto", package: "swift-crypto")
            ],
            path: "Sources/AetherMeshProtocol"
        ),
        .executableTarget(
            name: "AetherMeshDemo",
            dependencies: ["AetherMeshProtocol"],
            path: "Sources/AetherMeshDemo"
        ),
        .testTarget(
            name: "AetherMeshProtocolTests",
            dependencies: ["AetherMeshProtocol"],
            path: "Tests"
        )
    ]
)
