// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "FlexarrKit",
    platforms: [.tvOS(.v18), .iOS(.v18), .macOS(.v15)],
    products: [
        .library(name: "FlexarrKit", targets: ["FlexarrKit"]),
    ],
    targets: [
        .target(name: "FlexarrKit", resources: [.copy("Resources/Languages")]),
        .testTarget(
            name: "FlexarrKitTests",
            dependencies: ["FlexarrKit"],
            resources: [.copy("Fixtures")]
        ),
    ]
)
