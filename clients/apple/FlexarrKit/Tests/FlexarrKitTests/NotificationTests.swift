import CryptoKit
import Foundation
import Testing
@testable import FlexarrKit

struct NotificationTests {
    private let device = #"{"profileId":"p1","profileName":"Dan","films":true,"shows":false,"podcasts":true,"recordings":true,"requests":true,"upcoming":true,"upcomingHour":7,"timeZone":"Europe/London","relay":true}"#

    @Test func notRegisteredIsNil() async throws {
        let (client, host) = stubbedClient(.init(status: 404, body: Data()))

        #expect(try await client.notificationDevice() == nil)
        #expect(StubProtocol.recorded(host: host).first?.url?.path == "/api/v1/notifications/device")
    }

    @Test func readsTheRegistration() async throws {
        let (client, _) = stubbedClient(.init(status: 200, body: Data(device.utf8)))

        let registered = try #require(try await client.notificationDevice())

        #expect(registered.profileName == "Dan")
        #expect(registered.settings == NotificationSettings(shows: false, upcoming: true, upcomingHour: 7))
        #expect(registered.relay)
    }

    @Test func registersWithTheSettingsBesideTheRest() async throws {
        let (client, host) = stubbedClient(.init(status: 200, body: Data(device.utf8)))
        let key = SymmetricKey(data: Data(repeating: 1, count: 32))

        _ = try await client.registerForNotifications(.init(token: "abcd", environment: "sandbox", key: key,
                                                            timeZone: "Europe/London", settings: .init()))

        let request = try #require(StubProtocol.recorded(host: host).first)
        #expect(request.httpMethod == "PUT")
        #expect(request.url?.path == "/api/v1/notifications/device")
        let body = try #require(try JSONSerialization.jsonObject(with: request.httpBody ?? Data()) as? [String: Any])
        #expect(body["token"] as? String == "abcd")
        #expect(body["environment"] as? String == "sandbox")
        #expect(body["key"] as? String == Data(repeating: 1, count: 32).base64EncodedString())
        #expect(body["films"] as? Bool == true)
        #expect(body["upcoming"] as? Bool == false)
        #expect(body["upcomingHour"] as? Int == 9)
    }

    @Test func aRefusedTestSaysWhy() async throws {
        let (client, host) = stubbedClient(.init(status: 502, body: Data(#"{"reason":"BadDeviceToken"}"#.utf8)))

        await #expect(throws: FlexarrClientError.http(status: 502, message: "BadDeviceToken")) {
            try await client.sendTestNotification()
        }
        #expect(StubProtocol.recorded(host: host).first?.url?.path == "/api/v1/notifications/device/test")
        #expect(StubProtocol.recorded(host: host).first?.httpMethod == "POST")
    }

    @Test func opensASealedPayload() throws {
        let key = SymmetricKey(size: .bits256)
        let json = #"{"t":"New show: Severance","b":"Season 1 · 9 episodes","c":"flexarr.video","g":"series:1","i":"https://image.tmdb.org/t/p/w500/a.jpg","u":{"item":"s1","play":"e1"}}"#
        let sealed = try AES.GCM.seal(Data(json.utf8), using: key).combined!.base64EncodedString()

        let payload = try PushPayload.open(sealed, key: key)

        #expect(payload.title == "New show: Severance")
        #expect(payload.body == "Season 1 · 9 episodes")
        #expect(payload.category == "flexarr.video")
        #expect(payload.thread == "series:1")
        #expect(payload.image?.host() == "image.tmdb.org")
        #expect(payload.info == ["item": "s1", "play": "e1"])
        // Sealed with another key, it doesn't open.
        #expect(throws: (any Error).self) { try PushPayload.open(sealed, key: SymmetricKey(size: .bits256)) }
    }

    @Test func aTestPayloadNeedsLittle() throws {
        let key = SymmetricKey(size: .bits256)
        let sealed = try AES.GCM.seal(Data(#"{"t":"Flexarr","b":"Notifications work.","c":"flexarr.test"}"#.utf8), using: key).combined!.base64EncodedString()

        let payload = try PushPayload.open(sealed, key: key)

        #expect(payload.image == nil)
        #expect(payload.info.isEmpty)
    }
}
