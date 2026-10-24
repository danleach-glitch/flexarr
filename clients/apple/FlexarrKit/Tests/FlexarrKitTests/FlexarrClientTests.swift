import Foundation
import Testing
@testable import FlexarrKit

struct FlexarrClientTests {
    @Test func decodesSystemStatusFromServerJSON() throws {
        let json = """
        {
          "appName": "Flexarr",
          "version": "0.1.0+abc123",
          "osDescription": "Linux 6.8",
          "runtimeVersion": ".NET 10.0.0",
          "startTime": "2026-10-24T06:40:12.1234567+00:00"
        }
        """.data(using: .utf8)!

        let status = try FlexarrClient.decoder.decode(SystemStatus.self, from: json)

        #expect(status.appName == "Flexarr")
        #expect(status.version == "0.1.0+abc123")
    }

    @Test func decodesDatesWithoutFractionalSeconds() throws {
        let json = #"{"appName":"Flexarr","version":"0.1.0","osDescription":"","runtimeVersion":"","startTime":"2026-10-24T06:40:12Z"}"#

        let status = try FlexarrClient.decoder.decode(SystemStatus.self, from: Data(json.utf8))

        #expect(status.startTime == Date(timeIntervalSince1970: 1_792_824_012))
    }
}
