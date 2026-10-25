import Foundation
import Testing
@testable import FlexarrKit

struct DeviceTests {
    @Test func serverSentEvents_splitsEventsAndSkipsComments() {
        var events = ServerSentEvents()
        #expect(events.feed(": connected") == nil)
        #expect(events.feed("") == nil)
        #expect(events.feed("event: command") == nil)
        #expect(events.feed("data: {\"a\":1}") == nil)
        let first = events.feed("")
        #expect(first == ServerSentEvents.Event(name: "command", data: "{\"a\":1}"))
        // The next event starts clean, with the default name.
        #expect(events.feed("data: one") == nil)
        #expect(events.feed("data: two") == nil)
        #expect(events.feed("") == ServerSentEvents.Event(name: "message", data: "one\ntwo"))
    }

    @Test func deviceCommand_decodes() throws {
        let json = """
        {"id":"ab12","kind":"Play","itemId":"019a4c1e-0d8a-7a2e-9f1b-2c3d4e5f6a7b","fromBeginning":true,"sentBy":"Dan's iPhone","sentAt":"2026-10-25T10:00:00Z"}
        """
        let command = try FlexarrClient.decoder.decode(DeviceCommand.self, from: Data(json.utf8))
        #expect(command.kind == .play)
        #expect(command.itemId == "019a4c1e-0d8a-7a2e-9f1b-2c3d4e5f6a7b")
        #expect(command.fromBeginning)
        #expect(command.sentBy == "Dan's iPhone")
        // A kind this app doesn't know yet is ignored rather than failing the whole stream.
        let newer = try FlexarrClient.decoder.decode(DeviceCommand.self, from: Data(json.replacingOccurrences(of: "\"Play\"", with: "\"Rewind\"").utf8))
        #expect(newer.kind == .unknown)
    }

    @Test func device_decodes() throws {
        let json = """
        [{"id":"d1","name":"Living room","online":true,"lastUsedAt":"2026-10-25T10:00:00Z","profileId":"owner","profileName":"Dan","isThisOne":false,"nowPlaying":null}]
        """
        let devices = try FlexarrClient.decoder.decode([Device].self, from: Data(json.utf8))
        #expect(devices.count == 1)
        #expect(devices[0].name == "Living room")
        #expect(devices[0].online)
        #expect(devices[0].nowPlaying == nil)
    }
}
