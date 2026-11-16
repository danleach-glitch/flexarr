import Foundation
import Testing
@testable import FlexarrKit

@Suite struct LinkSpeedTests {
    @Test func isTheMedianOfRecentRates() {
        let speed = LinkSpeed()
        let now = Date()
        #expect(speed.current(at: now) == nil)
        speed.sample(bytes: 1_000_000, seconds: 2, at: now)      // 4 Mbps
        speed.sample(bitsPerSecond: 8_000_000, at: now)
        speed.sample(bitsPerSecond: 16_000_000, at: now)
        speed.sample(bitsPerSecond: 32_000_000, at: now)
        #expect(speed.current(at: now) == 8_000_000) // the lower middle of four
        speed.sample(bitsPerSecond: 64_000_000, at: now)
        #expect(speed.current(at: now) == 16_000_000)
    }

    @Test func ignoresSmallDownloads() {
        let speed = LinkSpeed()
        let now = Date()
        speed.sample(bytes: Int64(LinkSpeed.minSampleBytes - 1), seconds: 0.01, at: now)
        #expect(speed.current(at: now) == nil)
    }

    @Test func isFreshForHalfAnHourThenAStartingPointForADay() {
        let speed = LinkSpeed()
        let now = Date()
        speed.sample(bitsPerSecond: 8_000_000, at: now)
        #expect(speed.fresh(at: now) == 8_000_000)
        let later = now.addingTimeInterval(LinkSpeed.freshFor + 1)
        #expect(speed.fresh(at: later) == nil)
        #expect(speed.current(at: later) == 8_000_000)
        #expect(speed.current(at: now.addingTimeInterval(LinkSpeed.startingPointFor + 1)) == nil)
        // A fresh reading wins over the older ones.
        speed.sample(bitsPerSecond: 2_000_000, at: later)
        #expect(speed.current(at: later) == 2_000_000)
    }

    @Test func keepsEachLinkApart() {
        let speed = LinkSpeed()
        let now = Date()
        speed.use(link: "server|home")
        speed.sample(bitsPerSecond: 100_000_000, at: now)
        speed.use(link: "server|remote")
        #expect(speed.current(at: now) == nil)
        speed.sample(bitsPerSecond: 5_000_000, at: now)
        #expect(speed.current(at: now) == 5_000_000)
        speed.sample(bitsPerSecond: 9_000_000, link: "server|home", at: now)
        speed.use(link: "server|home")
        #expect(speed.current(at: now) == 9_000_000)
    }

    @Test func followsTheAddressInUse() {
        let speed = LinkSpeed()
        let home = URL(string: "http://192.168.1.5:8383")!
        let remote = URL(string: "http://100.1.2.3:8383")!
        let addresses = ServerAddresses(home: home, remote: remote, serverId: "abc")
        speed.follow(addresses)
        #expect(speed.link == "abc|home")
        speed.sample(bitsPerSecond: 80_000_000)
        addresses.reached(remote)
        #expect(speed.link == "abc|remote")
        #expect(speed.current() == nil)
        speed.sample(bitsPerSecond: 6_000_000)
        #expect(speed.current() == 6_000_000)
        addresses.reached(home)
        #expect(speed.current() == 80_000_000)
    }

    @Test func outlivesALaunch() throws {
        let suite = "LinkSpeedTests-\(UUID().uuidString)"
        let memory = try #require(UserDefaults(suiteName: suite))
        defer { memory.removePersistentDomain(forName: suite) }
        let now = Date()
        let first = LinkSpeed(memory: memory)
        first.use(link: "server|remote")
        first.sample(bitsPerSecond: 7_000_000, at: now)
        let next = LinkSpeed(memory: memory)
        next.use(link: "server|remote")
        #expect(next.current(at: now) == 7_000_000)
        next.use(link: "server|home")
        #expect(next.current(at: now) == nil)
    }
}

@Suite struct AwayFromHomeTests {
    @Test func awayIsTheRemoteAddressOrAGuestsTunnel() {
        let home = URL(string: "http://192.168.1.5:8383")!
        let remote = URL(string: "http://100.1.2.3:8383")!
        let addresses = ServerAddresses(home: home, remote: remote)
        #expect(!addresses.awayFromHome)
        addresses.reached(remote)
        #expect(addresses.awayFromHome)
        #expect(addresses.linkKey == "\(home.absoluteString)|remote")
        let guest = ServerAddresses(home: URL(string: "http://127.0.0.1:50000")!, remote: nil, guest: "100.9.9.9:8383")
        #expect(guest.awayFromHome)
        #expect(guest.linkKey == "100.9.9.9:8383|guest")
    }
}

@Suite struct SpeedTestTests {
    private func guestClient(_ stub: StubProtocol.Stub?) -> (FlexarrClient, String) {
        let host = "speed-\(UUID().uuidString.lowercased()).local"
        if let stub { StubProtocol.register(host: host, stub) }
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [StubProtocol.self]
        let addresses = ServerAddresses(home: URL(string: "http://\(host):8383")!, remote: nil, guest: host)
        return (FlexarrClient(addresses: addresses, apiKey: "secret", session: URLSession(configuration: configuration)), host)
    }

    @Test func measuresTheServersBytes() async throws {
        let (client, host) = guestClient(.init(status: 200, body: Data(count: 400_000), contentType: "application/octet-stream"))
        let reading = try await client.speedTest(bytes: 400_000)
        #expect((reading ?? 0) > 0)
        let asked = try #require(StubProtocol.recorded(host: host).last?.url)
        #expect(asked.path == "/api/v1/speedtest")
        #expect(asked.query == "bytes=400000")
    }

    @Test func anOlderServerHasNone() async throws {
        let (client, _) = guestClient(.init(status: 404, body: Data()))
        #expect(try await client.speedTest(bytes: 1_000_000) == nil)
    }

    @Test func runsAwayAtMostEveryTenMinutesUnlessTheNetworkChanged() async {
        let (client, host) = guestClient(.init(status: 200, body: Data(count: 300_000), contentType: "application/octet-stream"))
        let test = SpeedTest()
        let speed = LinkSpeed()
        speed.follow(client.addresses)
        let now = Date()
        #expect(await test.runIfDue(client, expensive: true, speed: speed, now: now) != nil)
        #expect(StubProtocol.recorded(host: host).last?.url?.query == "bytes=\(SpeedTest.mobileBytes)")
        #expect(speed.fresh(at: now) != nil)
        #expect(await test.runIfDue(client, expensive: false, speed: speed, now: now.addingTimeInterval(60)) == nil)
        test.networkChanged()
        #expect(await test.runIfDue(client, expensive: false, speed: speed, now: now.addingTimeInterval(120)) != nil)
        #expect(StubProtocol.recorded(host: host).last?.url?.query == "bytes=\(SpeedTest.wifiBytes)")
        #expect(await test.runIfDue(client, expensive: false, speed: speed, now: now.addingTimeInterval(120 + SpeedTest.interval)) != nil)
        // The title page's only asks with no fresh reading.
        #expect(await test.runIfDue(client, expensive: false, onlyIfStale: true, speed: speed,
                                    now: now.addingTimeInterval(240 + 2 * SpeedTest.interval)) == nil)
    }

    @Test func neverAtHome() async {
        let (client, host) = stubbedClient(.init(status: 200, body: Data(count: 300_000)))
        #expect(await SpeedTest().runIfDue(client, expensive: false, speed: LinkSpeed()) == nil)
        #expect(StubProtocol.recorded(host: host).isEmpty)
    }
}
