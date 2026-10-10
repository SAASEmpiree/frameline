// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "FrameLine",
    platforms: [.macOS(.v14)],
    targets: [
        .executableTarget(
            name: "FrameLine",
            path: "Sources/FrameLine"
        )
    ]
)
