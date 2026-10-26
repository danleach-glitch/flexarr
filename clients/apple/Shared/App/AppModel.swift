import FlexarrKit
import Foundation
import Observation
import UIKit

/// App-wide state: the signed-in server, who's watching, their libraries and the player.
@MainActor
@Observable
final class AppModel {
    enum Phase: Equatable {
        case signedOut
        case signedIn(ServerCredentials)
    }

    private(set) var phase: Phase
    private(set) var libraries: [Library] = []
    private(set) var librariesError: String?
    /// Whether this profile has podcasts; nil for servers before them.
    private(set) var podcastStatus: PodcastStatus?
    /// Whether the server has Live TV set up; nil for servers before it.
    private(set) var liveTvStatus: LiveTvStatus?
    /// How this profile listens to podcasts (auto-download, how many to keep, speed); nil until loaded.
    private(set) var podcastPreferences: PodcastPreferences?
    /// The podcast episodes to hear next, as the server keeps them for this profile (every device sees the same).
    private(set) var upNext: [PodcastEpisode] = [] {
        didSet { if upNext != oldValue { onPodcastsChange?() } }
    }
    /// Up Next or what's playing changed (the iPhone's widgets follow).
    @ObservationIgnored var onPodcastsChange: (() -> Void)?
    private(set) var profiles: [Profile] = []
    /// False until the first profile list arrives (or fails), so "Who's watching?" doesn't flash in late.
    private(set) var profilesLoaded = false
    /// The household has several profiles and nobody has chosen one on this device yet.
    private(set) var needsProfileChoice = false
    /// Bumped whenever watch state changes (playback ended, marked watched) so screens reload.
    private(set) var refreshToken = 0
    /// Set by deep links (`flexarr://item/<id>`); the selected tab's stack pushes it.
    var pendingRoute: Route?
    /// Words to search for, from Siri ("Search for Fargo in Flexarr"); the Search tab takes them.
    var pendingSearch: String?
    /// Why the connect screen is showing when nobody asked for it: the server stopped accepting this device.
    private(set) var signedOutNotice: String?

    let store = CredentialStore()
    let player = PlaybackController()
    let liveTv = LiveTvController()
    private let commands = DeviceCommandListener()
    let music = MusicPlayer()
    #if DEBUG
    /// Launch-argument sign-ins (simulator runs, the screenshot tour) act as the main profile
    /// unless `-FlexarrAskProfile YES` asks.
    private var skipsProfileChoice = false
    #endif

    init() {
        #if DEBUG
        // `-FlexarrServer <url> -FlexarrApiKey <key>` launch arguments sign in directly
        // (used for simulator runs and screenshots).
        let defaults = UserDefaults.standard
        // `-FlexarrSignedOut YES` starts on the connect screen (keeps the last address).
        if defaults.bool(forKey: "FlexarrSignedOut") { store.clear() }
        if let server = defaults.string(forKey: "FlexarrServer"), let url = FlexarrClient.normalizeAddress(server) {
            let credentials = ServerCredentials(address: url, apiKey: defaults.string(forKey: "FlexarrApiKey") ?? "")
            try? store.save(credentials)
            skipsProfileChoice = !defaults.bool(forKey: "FlexarrAskProfile")
            // Keep the key given on the command line rather than making a new device sign-in every run.
            upgradeChecked = true
        }
        // `-FlexarrNoHistory YES` plays without reporting progress, so test runs against a
        // real server don't change its watch history.
        player.recordsHistory = !defaults.bool(forKey: "FlexarrNoHistory")
        music.recordsHistory = player.recordsHistory
        #endif
        phase = store.load().map(Phase.signedIn) ?? .signedOut
        player.onSessionEnded = { [weak self] in
            self?.invalidate()
            self?.music.videoEnded()
        }
        // One thing plays at a time: a film starting pauses the music, which the lock screen offers back after.
        player.onWillStart = { [weak self] in self?.music.yieldToVideo() }
        liveTv.onWillStart = { [weak self] in self?.music.yieldToVideo() }
        liveTv.onEnded = { [weak self] in self?.music.videoEnded() }
        // A speed picked in the player is the profile's speed from then on, everywhere.
        music.onPodcastStarted = { [weak self] in
            guard let self else { return }
            Task { await self.loadUpNext() }
        }
        music.onSpeedChange = { [weak self] speed in
            guard let self else { return }
            Task { await self.updatePodcastPreferences(.init(speed: speed)) }
        }
        #if DEBUG
        // `-FlexarrPlay <id>` plays a title once the app's up (as flexarr://play/<id>, without tvOS asking first).
        if let play = defaults.string(forKey: "FlexarrPlay") {
            Task { @MainActor in
                try? await Task.sleep(for: .seconds(3))
                if let url = URL(string: "flexarr://play/\(play)") { open(url: url) }
            }
        }
        #endif
    }

    /// The signed-in client. A 401 from it checks whether this device has been signed out on the server.
    var client: FlexarrClient? {
        guard case .signedIn(let credentials) = phase else { return nil }
        return credentials.client { [weak self] in
            Task { @MainActor in self?.checkSignIn(credentials) }
        }
    }

    var credentials: ServerCredentials? {
        if case .signedIn(let credentials) = phase { return credentials }
        return nil
    }

    var currentProfile: Profile? { profiles.first(where: \.isCurrent) }

    /// With exactly two profiles, the one that isn't watching: switching is a single press.
    var otherProfile: Profile? {
        profiles.count == 2 ? profiles.first(where: { !$0.isCurrent }) : nil
    }

    /// Validates the address and key against `/system/status`, then saves them. A typed-in API key is swapped
    /// for this device's own token first, so the key itself isn't kept here.
    func connect(address: String, apiKey: String) async throws {
        guard let url = FlexarrClient.normalizeAddress(address) else { throw FlexarrClientError.invalidAddress }
        let key = apiKey.trimmingCharacters(in: .whitespacesAndNewlines)
        var credentials = ServerCredentials(address: url, apiKey: key)
        do {
            // The server's address for away from home comes with its status; it's kept beside this one.
            credentials.remoteAddress = try await credentials.client.systemStatus().remoteUrl
        } catch {
            throw ConnectionError(error)
        }
        let typed = credentials
        credentials = await withOwnToken(credentials)
        do {
            try store.save(credentials)
        } catch {
            // Not kept, so nobody would hold the new token: sign it out rather than leave it in the list.
            if credentials != typed { try? await credentials.client.signOut() }
            throw error
        }
        upgradeChecked = true
        remoteChecked = true
        libraries = []
        resetProfiles()
        signedOutNotice = nil
        phase = .signedIn(credentials)
    }

    func signOut() {
        // Tell the server too, so this device's token stops working; best effort, it's forgotten here either way.
        if let client = credentials?.client { Task { try? await client.signOut() } }
        forget()
    }

    private func forget() {
        player.stop()
        music.stop()
        store.clear()
        libraries = []
        resetProfiles()
        phase = .signedOut
    }

    private var checkingSignIn = false

    /// A request was refused (401). If the server still refuses this sign-in when asked directly, it was signed
    /// out there (from the web's Sign-ins list, or a new key): forget it and go back to connecting, rather than
    /// leave every screen on an error that Try again can't fix. One refusal alone could be a server mid-restart.
    private func checkSignIn(_ refused: ServerCredentials) {
        guard !checkingSignIn, credentials == refused else { return }
        checkingSignIn = true
        Task {
            defer { checkingSignIn = false }
            try? await Task.sleep(for: .seconds(1))
            do {
                _ = try await refused.client.me()
                return // Accepted after all.
            } catch FlexarrClientError.unauthorized {
                // Refused again: signed out.
            } catch {
                return // Unreachable or restarting: no reason to forget the sign-in.
            }
            // Not if it's been replaced meanwhile (another profile chosen, or connected again).
            guard credentials == refused else { return }
            signedOutNotice = "This \(ThisDevice.name) was signed out. Connect it again to keep watching."
            forget()
        }
    }

    /// Set once this launch has checked whether the saved key should be swapped for a token of its own.
    private var upgradeChecked = false
    /// Set once this launch has asked the server for its address for away from home.
    private var remoteChecked = false

    /// Keeps the saved address for away from home in step with the server's (remote access turned on or off since,
    /// or a sign-in from before the app kept one). Best effort, once per launch, after the profiles are up.
    private func refreshRemoteAddress() async {
        guard !remoteChecked, let credentials else { return }
        remoteChecked = true
        guard let status = try? await credentials.client.systemStatus() else { return }
        let remote = status.remoteUrl == credentials.address ? nil : status.remoteUrl
        guard remote != credentials.remoteAddress, self.credentials == credentials else { return }
        var updated = credentials
        updated.remoteAddress = remote
        store.saveRemoteAddress(remote)
        phase = .signedIn(updated)
    }

    /// The same sign-in with this device's own token in place of the server's API key, if it holds the key
    /// (as Apple TVs set up by earlier versions do). Unchanged if it doesn't, or the server is too old to offer one.
    private func withOwnToken(_ credentials: ServerCredentials) async -> ServerCredentials {
        guard !credentials.apiKey.isEmpty,
              let me = try? await credentials.client.me(), me.method == "api-key",
              let token = try? await credentials.client.deviceToken(deviceName: UIDevice.current.name)
        else { return credentials }
        var own = credentials
        own.apiKey = token
        return own
    }

    func loadProfiles() async {
        if !upgradeChecked, let credentials {
            upgradeChecked = true
            let own = await withOwnToken(credentials)
            // Only switch once it's saved, so a Keychain failure leaves the working key in place, and the token
            // nobody could keep is signed out again (or every launch would add another to the server's list).
            if own != credentials {
                if (try? store.save(own)) != nil {
                    phase = .signedIn(own)
                } else {
                    try? await own.client.signOut()
                }
            }
        }
        guard let client else { return }
        defer { profilesLoaded = true }
        guard let list = try? await client.profiles() else { return }
        // A token for a profile that's since been removed acts as nobody: forget it and ask again.
        if !list.contains(where: \.isCurrent), var credentials, credentials.profileToken != nil {
            credentials.profileToken = nil
            store.saveProfile(nil)
            phase = .signedIn(credentials)
            return await loadProfiles()
        }
        profiles = list
        needsProfileChoice = list.count > 1 && credentials?.profileToken == nil
        #if DEBUG
        if skipsProfileChoice { needsProfileChoice = false }
        #endif
        // Siri learns the library's titles from the signed-in server, so "play the next <show>" is understood.
        if !needsProfileChoice { FlexarrShortcuts.updateAppShortcutParameters() }
        Task { await refreshRemoteAddress() }
    }

    #if os(iOS)
    /// The device Siri plays on when none is named: an Apple TV's sign-in id, or nil for this iPhone or iPad.
    static var siriDevice: String? {
        get { UserDefaults.standard.string(forKey: "FlexarrSiriDevice") }
        set {
            UserDefaults.standard.set(newValue, forKey: "FlexarrSiriDevice")
            if newValue == nil { UserDefaults.standard.removeObject(forKey: "FlexarrSiriDeviceName") }
        }
    }

    /// That device's name as last seen, for what Siri says.
    static var siriDeviceName: String? {
        get { UserDefaults.standard.string(forKey: "FlexarrSiriDeviceName") }
        set { UserDefaults.standard.set(newValue, forKey: "FlexarrSiriDeviceName") }
    }
    #endif

    /// Makes `profile` the one watching on this device (remembered across launches). A wrong
    /// PIN throws the server's message. Every screen reloads, since history and libraries differ.
    func switchProfile(to profile: Profile, pin: String? = nil) async throws {
        guard var credentials else { return }
        let selection: ProfileSelection
        do {
            selection = try await credentials.client.selectProfile(profile.id, pin: pin)
        } catch {
            throw ConnectionError(error)
        }
        player.stop()
        music.stop()
        credentials.profileToken = selection.token
        store.saveProfile(selection.token)
        profiles = profiles.map { $0.marked(current: $0.id == profile.id) }
        needsProfileChoice = false
        libraries = []
        phase = .signedIn(credentials)
    }

    private func resetProfiles() {
        profiles = []
        profilesLoaded = false
        needsProfileChoice = false
    }

    func loadLibraries() async {
        guard let client else { return }
        do {
            libraries = try await client.libraries()
            librariesError = nil
        } catch {
            librariesError = ConnectionError(error).message
        }
        podcastStatus = try? await client.podcastStatus()
        if podcastStatus?.available == true { podcastPreferences = try? await client.podcastPreferences() }
        liveTvStatus = try? await client.liveTvStatus()
    }

    /// Live TV has a place once the server has a source with channels.
    var hasLiveTv: Bool { liveTvStatus.map { $0.configured && $0.channels > 0 } ?? false }

    /// Plays a channel; Previous and Next go through `channels` in that order.
    func watch(_ channel: LiveChannel, among channels: [LiveChannel]) {
        guard let client else { return }
        liveTv.watch(channel, among: channels, client: client)
    }

    func invalidate() {
        refreshToken += 1
    }

    /// Plays random episodes of a show, one after another.
    func shuffle(_ series: ItemSummary) {
        guard let client else { return }
        player.play(itemId: series.id, preview: nil, startAt: nil, client: client, shuffle: true)
    }

    func play(_ item: ItemSummary, fromBeginning: Bool = false) {
        guard let client else { return }
        player.play(itemId: item.id, preview: item, startAt: fromBeginning ? 0 : nil, client: client)
    }

    /// Plays a movie or episode directly; for a series or season, plays its next-up episode
    /// (or opens the details when everything has been watched). Music opens its page.
    func playBest(_ item: ItemSummary, router: Router) async {
        guard let client else { return }
        switch item.kind {
        case .series, .season:
            if let next = try? await client.nextUp(item.id) {
                play(next)
            } else {
                router.push(.item(item))
            }
        case .artist, .album:
            // A Play button on an artist or album plays them; their pages are a click on the card.
            if let tracks = try? await client.tracks(of: item.id), !tracks.isEmpty {
                music.play(tracks, from: 0, shuffled: false, albums: item.kind == .album ? [item] : [], client: client)
            }
        case .track:
            playTracks([item])
        case .podcastEpisode:
            playEpisode(item)
        case .podcast:
            router.push(.item(item))
        default:
            play(item)
        }
    }

    // MARK: Podcasts

    /// Plays a podcast episode from where it was left, at the profile's speed.
    func playEpisode(_ episode: ItemSummary) {
        guard let client else { return }
        music.playPodcast(episode, speed: podcastPreferences?.speed, client: client)
    }

    func loadUpNext() async {
        guard let client, podcastStatus?.available != false else { return }
        if let queue = try? await client.upNext() { upNext = queue }
    }

    /// Puts an episode in Up Next: first (Play next), or last.
    func addToUpNext(_ episodeId: String, next: Bool) async {
        guard let client else { return }
        if let queue = try? await client.addToUpNext(episodeId, next: next) { upNext = queue }
    }

    func removeFromUpNext(_ episodeId: String) async {
        guard let client else { return }
        upNext.removeAll { $0.id == episodeId }
        try? await client.removeFromUpNext(episodeId)
    }

    /// A new order (dragged into place here at once, then kept on the server).
    func reorderUpNext(_ episodes: [PodcastEpisode]) async {
        guard let client else { return }
        upNext = episodes
        if let queue = try? await client.setUpNext(episodes.map(\.id)) { upNext = queue }
    }

    /// Changes how this profile listens to podcasts, on every device; nil when it couldn't be saved.
    @discardableResult
    func updatePodcastPreferences(_ update: PodcastPreferencesUpdate) async -> PodcastPreferences? {
        guard let client else { return nil }
        if var known = podcastPreferences {
            // Shown at once; the server's answer follows.
            if let value = update.autoDownload { known.autoDownload = value }
            if let value = update.keepEpisodes { known.keepEpisodes = value }
            if let value = update.speed { known.speed = value }
            podcastPreferences = known
        }
        guard let saved = try? await client.setPodcastPreferences(update) else { return nil }
        podcastPreferences = saved
        return saved
    }

    // MARK: Music

    /// Plays `tracks` from the one at `start` (or a fresh shuffle of them). `albums` lends them their album names.
    func playTracks(_ tracks: [ItemSummary], from start: Int = 0, shuffled: Bool = false, albums: [ItemSummary] = []) {
        guard let client else { return }
        music.play(tracks, from: start, shuffled: shuffled, albums: albums, client: client)
    }

    /// Everything an artist or album has, in order or shuffled.
    func playAll(of page: MusicPageModel, shuffled: Bool) async {
        guard let client else { return }
        let tracks = await page.allTracks(client: client)
        guard !tracks.isEmpty else { return }
        let albums = page.item?.kind == .album ? [page.item].compactMap { $0 } : page.albums
        music.play(tracks, shuffled: shuffled, albums: albums, client: client)
    }

    func setWatched(_ item: ItemSummary, _ watched: Bool) async {
        guard let client else { return }
        try? await client.setWatched(item.id, watched: watched)
        invalidate()
    }

    // MARK: Commands from other devices

    /// Listens for commands while the app is in front and signed in (with a profile chosen); stops otherwise.
    func listenForCommands(_ inFront: Bool) {
        guard inFront, let client, profilesLoaded, !needsProfileChoice else {
            commands.stop()
            return
        }
        commands.start(client) { [weak self] command in self?.handle(command) }
    }

    /// Does what another device, the web app or an assistant asked: play a title here, or pause, resume or stop.
    func handle(_ command: DeviceCommand) {
        switch command.kind {
        case .play:
            guard let client, let id = command.itemId else { return }
            // A podcast episode (the command carries only its id) plays in the podcast player; anything else as video.
            Task {
                if let episode = try? await client.podcastEpisode(id) {
                    playEpisode(episode.summary)
                } else {
                    player.play(itemId: id, preview: nil, startAt: command.fromBeginning ? 0 : nil, client: client, shuffle: command.shuffle)
                }
            }
        case .pause: player.pause()
        case .resume: player.resume()
        case .stop: player.closePlayer()
        case .unknown: break
        }
    }

    /// Plays a title here as an intent asked (Siri, Shortcuts): a show's next episode, a film as it is.
    func playFromIntent(itemId: String) async {
        guard let client else { return }
        if let episode = try? await client.podcastEpisode(itemId) {
            playEpisode(episode.summary)
        } else if let detail = try? await client.item(itemId), detail.item.kind == .series || detail.item.kind == .season {
            if let next = try? await client.nextUp(itemId) { play(next) } else { pendingRoute = .itemID(itemId) }
        } else {
            player.play(itemId: itemId, preview: nil, startAt: nil, client: client)
        }
    }

    func open(url: URL) {
        guard url.scheme == "flexarr", let host = url.host() else { return }
        let id = url.pathComponents.dropFirst().first ?? ""
        guard !id.isEmpty else { return }
        switch host {
        case "item": pendingRoute = .itemID(id)
        case "play":
            // flexarr://play/<id>?t=<seconds>&s=<subtitle key> starts at a given time, with those subtitles.
            guard let client else { return }
            let query = URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems ?? []
            let start = query.first(where: { $0.name == "t" })?.value.flatMap(Double.init)
            let subtitles = query.first(where: { $0.name == "s" })?.value
            let shuffle = query.first(where: { $0.name == "shuffle" })?.value == "1" // a show's episodes in a random order
            // A podcast episode (Siri's "Play The Rest Is History") goes to the podcast player.
            Task {
                if let episode = try? await client.podcastEpisode(id) {
                    playEpisode(episode.summary)
                } else {
                    player.play(itemId: id, preview: nil, startAt: start, client: client, shuffle: shuffle, subtitleKey: subtitles)
                }
            }
        default: break
        }
    }
}

/// Turns transport errors into sentences that say what happened and what to do.
struct ConnectionError: LocalizedError {
    let message: String

    init(_ error: Error) {
        if let error = error as? ConnectionError {
            message = error.message
        } else if let error = error as? FlexarrClientError {
            message = error.errorDescription ?? "Something went wrong."
        } else if let error = error as? URLError {
            switch error.code {
            case .cannotFindHost, .dnsLookupFailed:
                message = "Couldn't find that server. Check the address, or use its IP address instead of the name."
            case .cannotConnectToHost, .networkConnectionLost:
                message = "The server isn't answering at that address. Make sure Flexarr is running and the port is right (usually 8383)."
            case .timedOut:
                message = "The server took too long to answer. Is it on the same network as this \(ThisDevice.name)?"
            case .notConnectedToInternet:
                message = "This \(ThisDevice.name) isn't connected to a network."
            case .appTransportSecurityRequiresSecureConnection:
                message = "Plain http only works for local addresses (.local names or IP addresses). Use https for anything else."
            case .secureConnectionFailed, .serverCertificateUntrusted, .serverCertificateHasBadDate, .serverCertificateNotYetValid:
                message = "The server's https certificate isn't trusted. Try its http address on your local network."
            case .cancelled:
                message = "The request was cancelled."
            default:
                message = error.localizedDescription
            }
        } else {
            message = error.localizedDescription
        }
    }

    var errorDescription: String? { message }
}

/// What this device is called in messages: "Apple TV", "iPhone" or "iPad".
enum ThisDevice {
    #if os(tvOS)
    static let name = "Apple TV"
    #else
    /// Set once at launch (`UIDevice` is main-actor only, and messages are made anywhere); iPhone until then.
    nonisolated(unsafe) static var name = "iPhone"

    @MainActor static func identify() {
        name = UIDevice.current.userInterfaceIdiom == .pad ? "iPad" : "iPhone"
    }
    #endif
}

/// Navigation destinations shared by every tab.
enum Route: Hashable {
    /// An item we already have a summary for, so the header can render before detail loads.
    case item(ItemSummary)
    case itemID(String)
    case person(id: String, name: String)
    case library(Library, LibrarySort)
    case playlist(PlaylistSummary)
    /// The shows followed and their newest episodes (iPhone and iPad).
    case podcasts
    /// Finding podcasts in Apple's directory (iPhone and iPad).
    case podcastBrowse
    /// The channels and what's on them (iPhone and iPad; the Apple TV has a tab).
    case liveTv
}

/// One navigation stack per tab.
@MainActor
@Observable
final class Router {
    var path: [Route] = []

    func push(_ route: Route) {
        path.append(route)
    }
}

private extension Profile {
    func marked(current: Bool) -> Profile {
        Profile(id: id, name: name, color: color, hasPin: hasPin, isMain: isMain, isCurrent: current,
                hiddenLibraryIds: hiddenLibraryIds, image: image)
    }
}
