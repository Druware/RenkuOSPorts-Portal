// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "RenkuPortsBuilder",
    platforms: [.macOS(.v14)],
    targets: [
        // Settings, API client and build worker; no UI, so the CLI and the tests can drive it.
        .target(name: "BuilderCore"),
        .executableTarget(name: "RenkuPortsBuilder", dependencies: ["BuilderCore"]),
        // Headless runner for the same Worker (end-to-end tests, servers without a desktop session).
        .executableTarget(name: "renkuports-builder-cli", dependencies: ["BuilderCore"], path: "Sources/BuilderCLI"),
        .testTarget(name: "BuilderCoreTests", dependencies: ["BuilderCore"]),
    ]
)
