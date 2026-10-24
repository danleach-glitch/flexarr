import Foundation

public struct SystemStatus: Codable, Sendable, Equatable {
    public let appName: String
    public let version: String
    public let osDescription: String
    public let runtimeVersion: String
    public let startTime: Date
}
