import Foundation
import Testing
@testable import FlexarrKit

struct ErrorReportTests {
    private let scrubber = ErrorReportScrubber(redactor: LogRedactor(secrets: ["device-token-123"]))

    @Test func takesOutWhatTheLogDoes() {
        #expect(scrubber.scrub("key device-token-123 for /api/v1/playback/0123456789abcdef0123456789abcdef/x?api_key=1")
                == "key <redacted> for <path>")
    }

    @Test func serverAddressesLoseTheirHost() {
        #expect(scrubber.scrub("GET http://mini.local:8383/api/v1/items failed") == "GET http://<server>/api/v1/items failed")
        #expect(scrubber.scrub("https://tv.tail1234.ts.net/x") == "https://<server>/x")
        #expect(scrubber.scrub("couldn't reach nas.lan:8383") == "couldn't reach <server>")
    }

    @Test func ipAndEmailAddressesGo() {
        #expect(scrubber.scrub("connect to 192.168.1.20:8383 refused") == "connect to <ip> refused")
        #expect(scrubber.scrub("from fe80::1c2b:3d4e:5f6a:7b8c now") == "from <ip> now")
        #expect(scrubber.scrub("sent to dan@example.com") == "sent to <email>")
        // Times aren't addresses.
        #expect(scrubber.scrub("at 12:30:45") == "at 12:30:45")
    }

    @Test func filePathsGo() {
        #expect(scrubber.scrub("open /var/mobile/Containers/Data/Application/ABC/Library/Caches/Dune.mkv failed")
                == "open <path> failed")
        #expect(scrubber.scrub("file:///private/var/x/Dune%20(2021).mp4 missing") == "<path> missing")
        #expect(scrubber.scrub("see ~/Movies/Dune.mkv") == "see <path>")
        // A lone slash or "and/or" isn't a path.
        #expect(scrubber.scrub("1/2 done, read and/or write") == "1/2 done, read and/or write")
    }

    @Test func quotedTextGoes_apostrophesStay() {
        #expect(scrubber.scrub("Couldn't start \"Dune: Part Two\"") == "Couldn't start <quoted>")
        #expect(scrubber.scrub("Couldn't start “Dune” or 'Alien' here, it isn't") == "Couldn't start <quoted> or <quoted> here, it isn't")
        #expect(scrubber.scrub("The operation couldn’t be completed. (NSURLErrorDomain error -1004.)")
                == "The operation couldn’t be completed. (NSURLErrorDomain error -1004.)")
    }

    @Test func contextsKeepOnlyModelOSAndAppVersion() {
        let contexts: [String: [String: Any]] = [
            "device": ["model": "AppleTV14,1", "name": "Living Room", "free_memory": 123, "family": "Apple TV", "timezone": "Europe/London",
                       "locale": "en_GB", "language": "en", "boot_time": "2026-10-27T08:00:00Z"],
            "os": ["name": "tvOS", "version": "26.1", "kernel_version": "Darwin", "build": "23J"],
            "app": ["app_version": "0.45.0", "app_build": "1", "device_app_hash": "abc", "app_name": "Flexarr", "app_start_time": "x"],
            "culture": ["timezone": "Europe/London", "locale": "en_GB", "calendar": "Gregorian"],
            "trace": ["trace_id": "x"],
        ]
        let kept = ErrorReportScrubber.filterContexts(contexts)
        #expect(Set(kept.keys) == ["device", "os", "app"])
        #expect(Set(kept["device"]!.keys) == ["model", "family"])
        #expect(Set(kept["os"]!.keys) == ["name", "version", "build"])
        #expect(Set(kept["app"]!.keys) == ["app_version", "app_build"])
    }

    @Test func onlyTheCategoryTagStays() {
        #expect(ErrorReportScrubber.filterTags(["category": "playback", "server": "mini"]) == ["category": "playback"])
        #expect(ErrorReportScrubber.filterTags(["device": "x"]) == nil)
    }

    @Test func fileNamesLoseTheirFolders() {
        #expect(ErrorReportScrubber.fileName("/private/var/containers/Bundle/Application/1234/Flexarr.app/Flexarr") == "Flexarr")
        #expect(ErrorReportScrubber.fileName("Flexarr") == "Flexarr")
        #expect(ErrorReportScrubber.fileName(nil) == nil)
    }

    @Test func theCacheKeepsOnlyAnEnabledSetting_perServer() throws {
        let suite = "errorReports-\(UUID().uuidString)"
        let defaults = try #require(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let cache = ErrorReportCache(defaults: defaults)
        let mini = URL(string: "http://mini.local:8383")!
        let other = URL(string: "http://other.local:8383")!
        #expect(cache.setting(for: mini) == nil)
        cache.save(ErrorReportSetting(enabled: true, installId: "abc"), for: mini)
        #expect(cache.setting(for: mini) == ErrorReportSetting(enabled: true, installId: "abc"))
        #expect(cache.setting(for: other) == nil)
        cache.save(ErrorReportSetting(enabled: false, installId: "abc"), for: mini)
        #expect(cache.setting(for: mini) == nil)
        cache.save(ErrorReportSetting(enabled: true), for: mini)
        cache.save(nil, for: mini)
        #expect(cache.setting(for: mini) == nil)
    }

    @Test func statusDecodesTheSwitch_andOlderServersHaveNone() throws {
        let base = #"{"appName":"Flexarr","version":"0.45.0","osDescription":"macOS","runtimeVersion":"10","startTime":"2026-10-27T10:00:00Z""#
        let on = try FlexarrClient.decoder.decode(SystemStatus.self, from: Data((base + #","errorReports":{"enabled":true,"installId":"r4nd"}}"#).utf8))
        #expect(on.errorReports == ErrorReportSetting(enabled: true, installId: "r4nd"))
        let old = try FlexarrClient.decoder.decode(SystemStatus.self, from: Data((base + "}").utf8))
        #expect(old.errorReports == nil)
    }

    @Test func expectedFailuresAreNotReported() {
        #expect(!ErrorReportFilter.shouldReport(CancellationError()))
        #expect(!ErrorReportFilter.shouldReport(URLError(.cancelled)))
        #expect(!ErrorReportFilter.shouldReport(URLError(.notConnectedToInternet)))
        #expect(!ErrorReportFilter.shouldReport(URLError(.timedOut)))
        #expect(!ErrorReportFilter.shouldReport(NSError(domain: NSURLErrorDomain, code: NSURLErrorCannotConnectToHost)))
        #expect(!ErrorReportFilter.shouldReport(FlexarrClientError.unreachable))
    }

    @Test func realFailuresAreReported() {
        #expect(ErrorReportFilter.shouldReport(URLError(.badServerResponse)))
        #expect(ErrorReportFilter.shouldReport(URLError(.serverCertificateUntrusted)))
        #expect(ErrorReportFilter.shouldReport(FlexarrClientError.invalidResponse))
        #expect(ErrorReportFilter.shouldReport(NSError(domain: "AVFoundationErrorDomain", code: -11800)))
    }
}
