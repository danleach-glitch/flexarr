import FlexarrKit
import Foundation
import Observation
import UIKit

private let log = FlexarrLog("signin")

/// App-wide state: the signed-in server, who's watching, their libraries and the player.
@MainActor
@Observable
final class AppModel {
    enum Phase: Equatable {
        case signedOut
        case signedIn(ServerCredentials)
    }

    private(set) var phase: Phase {
        // Warnings and errors go to the server signed in to, and its sign-in is kept out of the log.
        didSet {
            guard phase != oldValue else { return }
            GuestMode.isActive = credentials?.isGuest == true
            LogHub.shared.signedIn(client)
            // Error reports follow the server signed in to: none signed out, the last answer for a new one.
            if credentials?.address != Self.credentials(in: oldValue)?.address {
                ErrorReports.stop()
                ErrorReports.resume(server: credentials?.address)
                Task { await refreshErrorReports() }
            }
        }
    }
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
    /// A device asking to connect, or one that's stopped asking (from the command stream, signed in as the main
    /// profile): the iPhone asks whether to allow it. Apple TV leaves it to the phones.
    @ObservationIgnored var onPairingEvent: (@MainActor (DeviceEvent) -> Void)?
    /// What a device is playing changed (nil: stopped), from the command stream: the iPhone's remote follows it.
    @ObservationIgnored var onNowPlaying: (@MainActor (String, DeviceNowPlaying?, Bool?) -> Void)?
    /// `flexarr://remote/<device id>` (the Live Activity): the iPhone opens its remote on that TV.
    @ObservationIgnored var onOpenRemote: (@MainActor (String) -> Void)?
    /// About to sign out, with the sign-in still working: the iPhone stops its notifications.
    @ObservationIgnored var onSignOut: (@MainActor (FlexarrClient) async -> Void)?
    private(set) var profiles: [Profile] = []
    /// False until the first profile list arrives (or fails), so "Who's watching?" doesn't flash in late.
    private(set) var profilesLoaded = false
    /// The household has several profiles and nobody has chosen one on this device yet.
    private(set) var needsProfileChoice = false
    /// This device is locked to kids profiles: the picker offers only them, until the admin PIN releases it.
    private(set) var kidsLock = false
    /// The admin PIN, just given to turn child lock off: the next profile chosen within a minute takes it, so
    /// leaving the kids profile doesn't ask for it again.
    @ObservationIgnored private var heldParentPin: (pin: String, until: Date)?

    /// The admin PIN given here in the last minute (turning child lock off), if any.
    var recentParentPin: String? { heldParentPin.flatMap { $0.until > Date() ? $0.pin : nil } }
    /// How this app is signed in ("device" for its own sign-in; see `SignInInfo.method`), as the server last said.
    private(set) var signInMethod: String?
    /// A link (or another device) asked to play as this profile, which has a PIN: "Who's watching?" opens on its PIN.
    private(set) var linkProfile: Profile?
    /// What to open once a profile is chosen, for a link that asked for one with a PIN.
    @ObservationIgnored private var afterProfileChoice: URL?
    /// This launch has already asked who's watching (see `asksWhoIsWatching`).
    @ObservationIgnored private var askedWhoIsWatching = false
    /// Bumped whenever watch state changes (playback ended, marked watched) so screens reload.
    private(set) var refreshToken = 0
    /// Set by deep links (`flexarr://item/<id>`); the selected tab's stack pushes it.
    var pendingRoute: Route?
    /// Words to search for, from Siri ("Search for Fargo in Flexarr"); the Search tab takes them.
    var pendingSearch: String?
    /// Set to open the Settings tab (iPhone and iPad), as "Someone's guessing your PIN" does; the tabs clear it.
    var pendingSettings = false
    /// A podcast episode was heard to the end: the phone's downloads send the play and drop its file (see RootView).
    @ObservationIgnored var onPodcastHeard: (() -> Void)?
    /// Bumped when Play or Shuffle starts music on Apple TV, which then goes to its Now Playing tab.
    var nowPlayingRequests = 0
    /// The server's error-report switch, as it last said (nil until asked, and from servers before it).
    private(set) var errorReports: ErrorReportSetting?
    /// Why the connect screen is showing when nobody asked for it: the server stopped accepting this device.
    private(set) var signedOutNotice: String?
    /// A family guest's tunnel to its host's server didn't start: why, shown with Try again and Leave.
    private(set) var tunnelProblem: String?
    /// An invite link was opened (or pasted): the app asks "Join Dan's Flexarr?" before anything happens, since a
    /// link can come from anywhere.
    var pendingInvite: GuestInvite?
    /// "Now playing on iPhone": a title moved to a phone with Continue on iPhone (the Apple TV shows it a moment).
    var handOffNotice: String?

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
        #if DEBUG
        // `-FlexarrVisitServer <url>` uses the saved sign-in with another server (a test copy of this one, which
        // knows the same tokens) for this launch only: nothing is saved, so the next launch is back home.
        if let visit = defaults.string(forKey: "FlexarrVisitServer"), let url = FlexarrClient.normalizeAddress(visit),
           case .signedIn(var saved) = phase {
            saved.address = url
            saved.remoteAddress = nil
            phase = .signedIn(saved)
            upgradeChecked = true
            remoteChecked = true
            skipsProfileChoice = true
        }
        #endif
        // A family guest reaches its host only through the tunnel: start it before anything asks the server.
        GuestMode.isActive = credentials?.isGuest == true
        if credentials?.isGuest == true { Task { await ensureGuestTunnel() } }
        LogHub.shared.signedIn(client)
        // Before anything else can crash: the server's answer comes later (see `refreshErrorReports`).
        ErrorReports.resume(server: credentials?.address)
        Task { await refreshErrorReports() }
        #if DEBUG
        // `-FlexarrOpen flexarr://item/<id>` opens a screen as the app starts, as a link would, without the simulator
        // asking first (used for checking a screen in the simulator).
        if let link = defaults.string(forKey: "FlexarrOpen").flatMap(URL.init(string:)) { open(url: link) }
        // `-FlexarrJoin <invite link>` joins as a family guest straight away, without "Join?" (end-to-end runs).
        if let link = defaults.string(forKey: "FlexarrJoin"), let invite = GuestInvite.parse(link) {
            Task {
                do { try await join(invite) } catch { log.warning("Joining failed: \(error.localizedDescription)") }
            }
        }
        #endif
        // Searching for subtitles is a parent's job, not a child's.
        player.offersSubtitleSearch = { [weak self] in self?.isKids != true }
        player.onSessionEnded = { [weak self] in
            self?.watchStateChanged()
            self?.music.videoEnded()
        }
        // One thing plays at a time: a film starting pauses the music, which the lock screen offers back after.
        player.onWillStart = { [weak self] in self?.music.yieldToVideo() }
        liveTv.onWillStart = { [weak self] in self?.music.yieldToVideo() }
        liveTv.onEnded = { [weak self] in self?.music.videoEnded() }
        // A speed picked in the player is the profile's speed from then on, everywhere.
        // Heard to the end: Home's new episodes and the downloads drop it now, not at the next catch-up. A moment later,
        // once the server has the play and the player has moved off its file.
        music.onPodcastEnded = { [weak self] _ in
            Task { @MainActor [weak self] in
                try? await Task.sleep(for: .seconds(1.5))
                guard let self else { return }
                self.invalidate()
                self.onPodcastHeard?()
            }
        }
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
        // `-FlexarrSearch <words>` searches for them once the app's up, as Siri would, for screenshots.
        if let words = defaults.string(forKey: "FlexarrSearch") {
            Task { @MainActor in
                try? await Task.sleep(for: .seconds(3))
                pendingSearch = words
            }
        }
        // `-FlexarrOpen <flexarr:// link>` opens a screen once the app's up (flexarr://watchlist, say), for screenshots.
        if let link = defaults.string(forKey: "FlexarrOpen"), let url = URL(string: link) {
            Task { @MainActor in
                try? await Task.sleep(for: .seconds(3))
                open(url: url)
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

    private static func credentials(in phase: Phase) -> ServerCredentials? {
        if case .signedIn(let credentials) = phase { return credentials }
        return nil
    }

    /// Asks the server whether it wants error reports now (after signing in, and each time the app comes to the
    /// front), and starts or stops them to match.
    func refreshErrorReports() async {
        guard let credentials, let client else { return }
        guard let status = try? await client.systemStatus(), self.credentials?.address == credentials.address else { return }
        errorReports = status.errorReports
        ErrorReports.apply(status.errorReports, server: credentials.address)
    }

    var currentProfile: Profile? { profiles.first(where: \.isCurrent) }

    /// A child is watching: the kids app (no Settings, no requests, bigger artwork).
    var isKids: Bool { currentProfile?.isKidsProfile == true }

    /// The profile has an age limit: Discover, Coming Soon and requests aren't offered, since their titles have no age.
    var hasAgeLimit: Bool { currentProfile?.hasAgeLimit == true }

    /// Who's watching can be changed here: several profiles, or a lock to kids profiles that Unlock releases.
    var offersProfilePicker: Bool { profiles.count > 1 || kidsLock }

    /// "Lock to kids profiles" is offered: a grown-up watching, a kids profile to lock to, and this app's own sign-in
    /// (the server keeps the lock on a device's sign-in, which the server's key or a Tailscale address doesn't have).
    var offersKidsLock: Bool {
        signInMethod == "device" && credentials?.isGuest != true && !kidsLock && !isKids && profiles.contains(where: \.isKidsProfile)
    }

    /// The picker's padlock over kids profiles: whenever child lock is on, or could be turned on here. On a kids
    /// profile too (Settings doesn't offer it there), where turning it on takes the admin PIN.
    var showsKidsPadlock: Bool { kidsLock || offersKidsLock || (signInMethod == "device" && isKids) }

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
        let status: SystemStatus
        do {
            status = try await credentials.client.systemStatus()
        } catch {
            throw ConnectionError(error)
        }
        // The server's address for away from home comes with its status; it's kept beside this one.
        credentials.remoteAddress = await Self.remoteAddress(for: status, address: url)
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
        if let client = credentials?.client {
            let leaving = onSignOut
            Task {
                await leaving?(client)
                try? await client.signOut()
            }
        }
        forget()
    }

    private func forget() {
        player.stop()
        music.stop()
        // A guest leaving leaves its host's tailnet too: nothing of the tunnel stays on the device.
        if credentials?.isGuest == true { GuestTunnel.shared.stop(forget: true) }
        tunnelProblem = nil
        store.clear()
        libraries = []
        resetProfiles()
        forgetWatchlist()
        phase = .signedOut
    }

    /// Nothing of this server's or profile's watchlist stays: not its menus, and not the widget (another person, or
    /// another server, may be next).
    private func forgetWatchlist() {
        hasWatchlist = false
        watchlistItems = []
        onWatchlistForgotten?()
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
            signedOutNotice = "This \(ThisDevice.name) was signed out."
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
        // A guest only ever reaches its host through the tunnel.
        guard !remoteChecked, let credentials, !credentials.isGuest else { return }
        remoteChecked = true
        guard let status = try? await credentials.client.systemStatus() else { return }
        let remote = await Self.remoteAddress(for: status, address: credentials.address, kept: credentials.remoteAddress)
        // Who's watching may have been chosen meanwhile: what matters is that it's still the same sign-in.
        guard let now = self.credentials, remote != now.remoteAddress,
              now.address == credentials.address, now.apiKey == credentials.apiKey else { return }
        var updated = now
        updated.remoteAddress = remote
        store.saveRemoteAddress(remote)
        phase = .signedIn(updated)
    }

    /// The server's address for away from home, once it's a Tailscale one with the same server there (see
    /// `FlexarrClient.remoteAddress`); one that isn't is left out, and said so in the log.
    private static func remoteAddress(for status: SystemStatus, address: URL, kept: URL? = nil) async -> URL? {
        await FlexarrClient.remoteAddress(for: status, address: address, kept: kept, refused: { log.warning($0) })
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

    /// Starts a family guest's tunnel, or finds it running (cheap), and keeps the sign-in's address in step with the
    /// tunnel's port. False, with `tunnelProblem` saying why, when it can't. True for everyone else.
    @discardableResult
    func ensureGuestTunnel() async -> Bool {
        guard let credentials, let guest = credentials.guest else { return true }
        do {
            let address = try await GuestTunnel.shared.start(guest)
            tunnelProblem = nil
            // The port it asked for was taken: the sign-in moves to the one it got.
            if let now = self.credentials, now.guest == guest, now.address != address {
                var moved = now
                moved.address = address
                store.clear()
                try? store.save(moved)
                phase = .signedIn(moved)
            }
            return true
        } catch {
            guard self.credentials?.guest == guest else { return false }
            tunnelProblem = error.localizedDescription
            return false
        }
    }

    enum JoinError: LocalizedError {
        case expired(owner: String)
        case used(owner: String)
        case paused(owner: String)
        case otherServer
        case failed(String)

        var errorDescription: String? {
            switch self {
            case .expired(let owner): "This invite has run out. Ask \(owner) for a new one."
            case .used(let owner): "This invite has been used, or was cancelled. Ask \(owner) for a new one."
            case .paused(let owner): "\(owner)’s server isn’t taking invites just now. Ask \(owner) to check Settings › Access."
            case .otherServer: "This invite led to a different server than it named. Ask for a new one."
            case .failed(let why): why
            }
        }
    }

    /// Joins as a family guest with `invite`: Tailscale joins the host's tailnet with its one-use key, the invite's
    /// token is swapped for this device's own sign-in through the tunnel, and that's saved in place of any other.
    func join(_ invite: GuestInvite) async throws {
        guard !invite.isExpired() else { throw JoinError.expired(owner: invite.ownerName) }
        if credentials != nil { signOut() }
        // One tunnel, one host: an identity from an earlier invite goes.
        GuestTunnel.shared.stop(forget: true)
        let info = GuestTunnelInfo(target: invite.tailnetTarget, hostname: invite.hostname, ownerName: invite.ownerName)
        do {
            let address = try await GuestTunnel.shared.start(info, authKey: invite.authKey)
            let redeemed: RedeemedInvite
            do {
                redeemed = try await FlexarrClient(baseURL: address).redeemInvite(token: invite.token)
            } catch FlexarrClientError.http(let status, let message) {
                switch status {
                case 410: throw JoinError.used(owner: invite.ownerName)
                case 503: throw JoinError.paused(owner: invite.ownerName)
                default: throw JoinError.failed(message ?? "The server refused the invite.")
                }
            }
            if !invite.serverId.isEmpty, redeemed.serverId != invite.serverId { throw JoinError.otherServer }
            let credentials = ServerCredentials(address: address, apiKey: redeemed.apiKey, guest: info)
            try store.save(credentials)
            signedOutNotice = nil
            tunnelProblem = nil
            upgradeChecked = true
            remoteChecked = true
            phase = .signedIn(credentials)
            log.info("Joined as a family guest")
        } catch let error as JoinError {
            GuestTunnel.shared.stop(forget: true)
            throw error
        } catch GuestTunnel.Failure.needsInvite {
            GuestTunnel.shared.stop(forget: true)
            throw JoinError.used(owner: invite.ownerName)
        } catch {
            GuestTunnel.shared.stop(forget: true)
            throw JoinError.failed(error.localizedDescription)
        }
    }

    /// The error `work` throws, if any.
    private static func failure(of work: () async throws -> Any?) async -> Error? {
        do {
            _ = try await work()
            return nil
        } catch {
            return error
        }
    }

    /// Try again, after the tunnel didn't start.
    func retryGuestTunnel() async {
        tunnelProblem = nil
        await loadProfiles()
    }

    func loadProfiles() async {
        if let guest = credentials?.guest {
            guard await ensureGuestTunnel() else { return }
            upgradeChecked = true // a guest's sign-in is its own already
            // Removed by its host: the server refuses everything from this device (403), not just the sign-in.
            if case FlexarrClientError.http(status: 403, _)? = await Self.failure(of: { try await self.client?.me() }) {
                signedOutNotice = "This \(ThisDevice.name) was removed. Ask \(guest.ownerName) for a new invite."
                forget()
                return
            }
        }
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
        async let listed = try? client.profiles()
        async let signIn = try? client.me()
        guard let list = await listed else { return }
        // A token for a profile that's since been removed acts as nobody: forget it and ask again.
        if !list.contains(where: \.isCurrent), var credentials, credentials.profileToken != nil {
            credentials.profileToken = nil
            store.saveProfile(nil)
            phase = .signedIn(credentials)
            return await loadProfiles()
        }
        profiles = list
        let me = await signIn
        kidsLock = me?.kidsLock == true
        signInMethod = me?.method
        // Asked to, a shared Apple TV asks who's watching once each time the app starts, whoever watched last.
        let asksNow = Self.asksWhoIsWatching && !askedWhoIsWatching
        askedWhoIsWatching = true
        needsProfileChoice = list.count > 1 && (credentials?.profileToken == nil || asksNow)
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

    /// Settings on Apple TV: open on "Who's watching?" each time the app starts, rather than as whoever watched last.
    static var asksWhoIsWatching: Bool {
        get { UserDefaults.standard.bool(forKey: "FlexarrAsksWhoIsWatching") }
        set { UserDefaults.standard.set(newValue, forKey: "FlexarrAsksWhoIsWatching") }
    }

    /// Makes `profile` the one watching on this device (remembered across launches). A wrong
    /// PIN throws the server's message. Every screen reloads, since history and libraries differ. Leaving a kids
    /// profile takes the admin PIN as `parentPin`: without it, `FlexarrClientError.parentPinNeeded` comes back
    /// as it is, so the picker can ask; so does `.ownPinWrong`, a right `parentPin` with a wrong `pin`.
    func switchProfile(to profile: Profile, pin: String? = nil, parentPin: String? = nil) async throws {
        guard let credentials else { return }
        let held = heldParentPin.flatMap { $0.until > Date() ? $0.pin : nil }
        let selection: ProfileSelection
        do {
            selection = try await credentials.client.selectProfile(profile.id, pin: pin, parentPin: parentPin ?? held)
        } catch let error as FlexarrClientError {
            switch error {
            case .parentPinNeeded, .ownPinWrong: throw error
            default: break
            }
            throw ConnectionError(error)
        } catch {
            throw ConnectionError(error)
        }
        chose(selection, among: profiles)
    }

    /// Locks this device to kids profiles, choosing one: the admin PIN unless it's unlocked here. With several
    /// kids profiles, "Who's watching?" then asks which one this device stays on.
    func lockToKids(pin: String?) async throws {
        guard let credentials else { return }
        let selection: ProfileSelection
        do {
            selection = try await credentials.client.lockToKids(pin: pin)
        } catch let error as FlexarrClientError {
            if case .parentPinNeeded = error { throw error }
            throw ConnectionError(error)
        } catch {
            throw ConnectionError(error)
        }
        kidsLock = true
        chose(selection, among: profiles.filter(\.isKidsProfile))
        if profiles.count > 1 { needsProfileChoice = true }
    }

    /// Releases this device from kids profiles with the admin PIN; every profile is offered again.
    func releaseKidsLock(pin: String) async throws {
        guard let client else { return }
        do {
            try await client.releaseKidsLock(pin: pin)
        } catch {
            throw ConnectionError(error)
        }
        kidsLock = false
        heldParentPin = (pin, Date().addingTimeInterval(60))
        await loadProfiles()
    }

    /// Whether child lock is still on here: it can be turned off from the web's Devices page while the app's open.
    func refreshKidsLock() async {
        guard let client, let me = try? await client.me() else { return }
        if kidsLock, me.kidsLock != true {
            kidsLock = false
            await loadProfiles()
        } else {
            kidsLock = me.kidsLock == true
        }
    }

    /// Takes on the profile the server chose: its token, and every screen afresh.
    private func chose(_ selection: ProfileSelection, among list: [Profile]) {
        guard var credentials else { return }
        let profile = selection.profile
        player.stop()
        music.stop()
        credentials.profileToken = selection.token
        store.saveProfile(selection.token)
        profiles = list.map { $0.marked(current: $0.id == profile.id) }
        // Which profiles ask for a PIN depends on who's watching (leaving a kids profile takes the main profile's), so the
        // list is read again as the new profile.
        Task { [weak self] in
            guard let self, let client = self.client, let fresh = try? await client.profiles() else { return }
            if fresh.contains(where: { $0.isCurrent && $0.id == profile.id }) { self.profiles = fresh }
        }
        needsProfileChoice = false
        libraries = []
        forgetWatchlist()
        phase = .signedIn(credentials)
        // A link that was waiting for this (or, if they chose someone else, whoever is watching now) goes ahead.
        linkProfile = nil
        if let link = afterProfileChoice {
            afterProfileChoice = nil
            open(url: link)
        }
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
        // A kids profile has no podcasts: with no status, nothing offers them.
        podcastStatus = isKids ? nil : try? await client.podcastStatus()
        if podcastStatus?.available == true { podcastPreferences = try? await client.podcastPreferences() }
        // Nor Live TV.
        liveTvStatus = isKids ? nil : try? await client.liveTvStatus()
        await refreshWatchlist()
    }

    /// The server keeps watchlists (0.56 and later): the watchlist's buttons, menu items and screen show.
    private(set) var hasWatchlist = false

    /// The library titles on the profile's watchlist, so poster menus offer Add or Remove.
    private(set) var watchlistItems: Set<String> = []

    /// Why the last watchlist change (adding or taking off) didn't happen, for an alert; nil when it did.
    var watchlistError: String?

    /// Why the last request (Request on a title the library doesn't have) didn't go through, for an alert.
    var requestError: String?

    /// Called when the watchlist is forgotten (signing out, another profile), so the widget empties at once.
    var onWatchlistForgotten: (() -> Void)?

    /// Called when the watchlist may have changed (the iPhone's widget follows it).
    var onWatchlistChange: (() -> Void)?

    /// Learns what's on the watchlist (cheaply: the server doesn't ask Seerr). Network trouble keeps what was known, so
    /// the watchlist doesn't vanish while offline; an older server, which has none, hides it.
    func refreshWatchlist() async {
        guard let client else { return }
        // A kids profile has no watchlist.
        if isKids {
            if hasWatchlist || !watchlistItems.isEmpty {
                hasWatchlist = false
                watchlistItems = []
                onWatchlistChange?()
            }
            return
        }
        do {
            let keys = try await client.watchlistKeys()
            let items = Set(keys?.libraryItemIds ?? [])
            let changed = hasWatchlist != (keys != nil) || watchlistItems != items
            hasWatchlist = keys != nil
            watchlistItems = items
            // Only when something changed: listeners (the widget) fetch again.
            if changed { onWatchlistChange?() }
        } catch {
            log.warning("Couldn't check the watchlist", error: error)
        }
    }

    /// Puts a film or show on the profile's watchlist, or takes it off. Nil if that failed; true if adding it also
    /// requested it (the profile requests what it adds).
    @discardableResult
    func setOnWatchlist(_ key: WatchlistKey, _ on: Bool) async -> Bool? {
        guard let client else { return nil }
        do {
            var requested = false
            if on { requested = try await client.addToWatchlist(key).requested } else { try await client.removeFromWatchlist(key) }
            if case .library(let id) = key {
                if on { watchlistItems.insert(id) } else { watchlistItems.remove(id) }
                // Home's watchlist row can only change for a library title.
                invalidate()
            }
            onWatchlistChange?()
            Task { await refreshWatchlist() }
            return requested
        } catch {
            log.warning("Watchlist change failed", error: error)
            watchlistError = ConnectionError(error).message
            return nil
        }
    }

    /// The order of this profile's libraries in the menu, the same on every device; nil from servers before 0.54,
    /// which keep none.
    var libraryOrder: [String]? { currentProfile?.libraryOrder }

    /// Puts the `shown` entries (library ids, and `LibraryOrder.liveTv`) in this order for the profile, here straight
    /// away and on its other devices once saved. Entries this app doesn't show keep their places.
    func setLibraryOrder(_ shown: [String]) {
        guard let client, var profile = currentProfile, let saved = profile.libraryOrder else { return }
        let order = LibraryOrder.reordered(saved: saved, shown: shown)
        profile.libraryOrder = order
        profiles = profiles.map { $0.id == profile.id ? profile : $0 }
        libraries = LibraryOrder.sorted(libraries, by: order, id: \.id)
        Task {
            if (try? await client.setLibraryOrder(profileId: profile.id, order: order)) == nil {
                // Not saved: back to what the server has.
                await loadProfiles()
                await loadLibraries()
            }
        }
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

    /// Something was watched or marked: titles can leave the watchlist.
    private func watchStateChanged() {
        invalidate()
        if hasWatchlist { Task { await refreshWatchlist() } }
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
                showNowPlaying(router)
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
        // Already the one playing (or paused): carry on from where it is. Starting it again would go back to the place
        // in `episode`, which may be from when the screen loaded.
        if music.current?.id == episode.id {
            if !music.isPlaying { music.togglePlayPause() }
            return
        }
        music.playPodcast(episode, speed: podcastPreferences?.speed, client: client)
    }

    func loadUpNext() async {
        guard let client, !isKids, podcastStatus?.available != false else { return }
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
    /// Play and Shuffle pass their screen's `router` to show Now Playing too; picking a track leaves you where you are.
    func playTracks(_ tracks: [ItemSummary], from start: Int = 0, shuffled: Bool = false, albums: [ItemSummary] = [],
                    showingNowPlaying router: Router? = nil) {
        guard let client, !tracks.isEmpty else { return }
        music.play(tracks, from: start, shuffled: shuffled, albums: albums, client: client)
        if let router { showNowPlaying(router) }
    }

    /// Everything an artist or album has, in order or shuffled, then Now Playing.
    func playAll(of page: MusicPageModel, shuffled: Bool, router: Router) async {
        guard let client else { return }
        let tracks = await page.allTracks(client: client)
        guard !tracks.isEmpty else { return }
        let albums = page.item?.kind == .album ? [page.item].compactMap { $0 } : page.albums
        music.play(tracks, shuffled: shuffled, albums: albums, client: client)
        showNowPlaying(router)
    }

    /// Music just started from Play or Shuffle: the iPhone opens Now Playing over the tab, Apple TV goes to its tab.
    private func showNowPlaying(_ router: Router) {
        #if os(tvOS)
        nowPlayingRequests += 1
        #else
        router.showsNowPlaying = true
        #endif
    }

    func setWatched(_ item: ItemSummary, _ watched: Bool) async {
        guard let client else { return }
        try? await client.setWatched(item.id, watched: watched)
        if watched { player.offlineLibrary?.markedPlayed(itemId: item.id) } else { player.offlineLibrary?.forgetPosition(itemId: item.id) }
        watchStateChanged()
    }

    /// Every episode of an episode's show watched.
    func setShowWatched(of episode: ItemSummary) async {
        guard let client, let show = episode.seriesId else { return }
        try? await client.setWatched(show, watched: true)
        watchStateChanged()
    }

    func removeFromContinueWatching(_ item: ItemSummary) async {
        guard let client else { return }
        try? await client.removeFromContinueWatching(item.id)
        player.offlineLibrary?.forgetPosition(itemId: item.id)
        invalidate()
    }

    /// A search result was picked, for the profile's recent searches. Sent and forgotten: servers before 0.56
    /// don't keep them, and `-FlexarrNoHistory` runs leave them alone.
    func recordSearch(query: String?, item: ItemSummary? = nil) {
        guard let client, player.recordsHistory else { return }
        let words = query?.trimmingCharacters(in: .whitespaces)
        Task { try? await client.recordSearch(query: words?.isEmpty == false ? words : nil, itemId: item?.id) }
    }

    // MARK: Commands from other devices

    /// Listens for commands while the app is in front and signed in (with a profile chosen); stops otherwise.
    func listenForCommands(_ inFront: Bool) {
        guard inFront, let client, profilesLoaded, !needsProfileChoice else {
            commands.stop()
            return
        }
        // The iPhone follows what the TVs are playing, for its remote.
        #if os(iOS)
        let nowPlaying = true
        #else
        let nowPlaying = false
        #endif
        commands.start(client, nowPlaying: nowPlaying) { [weak self] event in
            switch event {
            case .command(let command): self?.handle(command)
            case .nowPlaying(let deviceId, let playing, let online): self?.onNowPlaying?(deviceId, playing, online)
            case .pairing, .pairingEnded: self?.onPairingEvent?(event)
            }
        }
    }

    /// Does what another device, the web app or an assistant asked: play a title here, or pause, resume or stop.
    func handle(_ command: DeviceCommand) {
        switch command.kind {
        case .play:
            guard let client, let id = command.itemId else { return }
            // As another profile: switched to first, as a link asking for one is.
            if let profileId = command.profileId, profileId != currentProfile?.id {
                var link = URLComponents()
                link.scheme = "flexarr"
                link.host = "play"
                link.path = "/\(id)"
                link.queryItems = [URLQueryItem(name: "profile", value: profileId)]
                    + (command.fromBeginning ? [URLQueryItem(name: "t", value: "0")] : command.seconds.flatMap { Int(exactly: $0.rounded()) }.map { [URLQueryItem(name: "t", value: String($0))] } ?? [])
                    + (command.shuffle ? [URLQueryItem(name: "shuffle", value: "1")] : [])
                if let url = link.url { open(url: url) }
                return
            }
            // A podcast episode (the command carries only its id) plays in the podcast player; anything else as video.
            Task {
                if let episode = try? await client.podcastEpisode(id) {
                    playEpisode(episode.summary)
                } else {
                    // From where it was asked to start (Undo after Stop), else from the beginning or where it was left.
                    player.play(itemId: id, preview: nil, startAt: command.fromBeginning ? 0 : command.seconds, client: client, shuffle: command.shuffle)
                }
            }
        case .watch:
            guard let id = command.channelId else { return }
            Task { await watchChannel(id: id) }
        // A title in the player takes them; otherwise the music (or podcast) playing here.
        case .pause: if player.isShowingTitle { player.pause() } else { music.pause() }
        case .resume: if player.isShowingTitle { player.resume() } else { music.resume() }
        case .stop:
            // Continued on a phone: the title's page, saying where it went, rather than wherever the player was opened from.
            let title = player.showingItem
            player.closePlayer()
            if command.handOff == true {
                if let title { pendingRoute = .item(title) }
                handOffNotice = "Now playing on \(command.sentBy ?? "your phone")"
            }
        case .seek, .skip, .next, .previous, .skipSegment, .audioTrack, .subtitleTrack, .rate:
            if player.remote(command) { return }
            guard music.current != nil else { return }
            switch command.kind {
            case .seek: if let seconds = command.seconds { music.seek(to: seconds) }
            case .skip: if let seconds = command.seconds { music.skip(by: seconds) }
            case .next: music.next()
            case .previous: music.previous()
            case .rate: if let rate = command.rate { music.setSpeed(rate) }
            default: break
            }
        case .sound:
            // A player that's open follows the change, as it does one made in Settings.
            guard let sound = command.sound else { return }
            var settings = SoundSettings.current
            if let boost = sound.dialogueBoost.flatMap(SoundSettings.DialogueBoost.init(rawValue:)) { settings.dialogueBoost = boost }
            if let night = sound.nightMode.flatMap(SoundSettings.NightMode.init(rawValue:)) { settings.nightMode = night }
            if let speakers = sound.speakers.flatMap(SoundSettings.Speakers.init(rawValue:)) { settings.speakers = speakers }
            SoundSettings.current = settings
            player.reportForRemotes()
        case .unknown: break
        }
    }

    /// Puts a Live TV channel on here (Siri, or another device asked), with every channel to change through,
    /// favourites first.
    func watchChannel(id: String) async {
        guard let client, !isKids, let channels = try? await client.liveChannels(), let channel = channels.first(where: { $0.id == id }) else { return }
        watch(channel, among: channels.filter(\.isFavourite) + channels.filter { !$0.isFavourite })
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

    /// `flexarr://item/<id>` shows a title; `flexarr://play/<id>` plays one (a show or season: the episode to watch
    /// next), with `t=<seconds>`, `s=<subtitle key>` and `shuffle=1`; `flexarr://live/<channel id>` puts a channel on;
    /// `flexarr://watchlist` opens the watchlist. Any of them can add `profile=<id or name>` to switch to that profile first, asking for its PIN if it has one.
    func open(url: URL) {
        // https://flexarr.io/join#… or flexarr://join#…: a family invite, asked about first.
        if let invite = GuestInvite.parse(url.absoluteString) {
            pendingInvite = invite
            return
        }
        guard url.scheme == "flexarr", let host = url.host() else { return }
        // A guest's link at launch (Siri, a widget) waits for the tunnel, or it would find no server.
        if credentials?.isGuest == true, GuestTunnel.shared.address == nil {
            Task { if await ensureGuestTunnel() { open(url: url) } }
            return
        }
        let id = url.pathComponents.dropFirst().first ?? ""
        // flexarr://watchlist (the profile's watchlist) has no id; the rest need one.
        guard !id.isEmpty || host == "watchlist" else { return }
        if host == "remote" {
            onOpenRemote?(id)
            return
        }
        var components = URLComponents(url: url, resolvingAgainstBaseURL: false)
        let query = components?.queryItems ?? []
        if let wanted = query.first(where: { $0.name == "profile" })?.value, !wanted.isEmpty {
            components?.queryItems = query.filter { $0.name != "profile" }
            if components?.queryItems?.isEmpty == true { components?.queryItems = nil }
            guard let rest = components?.url else { return }
            Task { await open(rest, as: wanted) }
            return
        }
        switch host {
        case "watchlist":
            pendingRoute = .watchlist
        case "item":
            // With its summary, which says what library it's in: the Apple TV opens it on that library's tab.
            Task {
                let item = (try? await client?.item(id))?.item
                pendingRoute = item.map(Route.item) ?? .itemID(id)
            }
        // flexarr://live/<channel id>: Siri's "Put on BBC One".
        case "live": Task { await watchChannel(id: id) }
        case "play":
            guard let client else { return }
            let start = query.first(where: { $0.name == "t" })?.value.flatMap(Double.init)
            let subtitles = query.first(where: { $0.name == "s" })?.value
            let shuffle = query.first(where: { $0.name == "shuffle" })?.value == "1" // a show's episodes in a random order
            Task {
                // A podcast episode (Siri's "Play The Rest Is History") goes to the podcast player.
                if let episode = try? await client.podcastEpisode(id) {
                    playEpisode(episode.summary)
                    return
                }
                // A show or season (a link made before the profile was chosen) plays the episode this profile is up to.
                if !shuffle, let item = try? await client.item(id).item, item.kind == .series || item.kind == .season {
                    if let next = try? await client.nextUp(id) {
                        player.play(itemId: next.id, preview: next, startAt: start, client: client, subtitleKey: subtitles)
                    } else {
                        pendingRoute = .item(item)
                    }
                    return
                }
                player.play(itemId: id, preview: nil, startAt: start, client: client, shuffle: shuffle, subtitleKey: subtitles)
            }
        default: break
        }
    }

    /// Opens `url` as the profile `wanted` (its id, or its name in any case): switched to straight away without a PIN,
    /// else "Who's watching?" asks for it and the link goes ahead once someone is chosen. An unknown profile, or one
    /// already watching, opens the link as things are.
    private func open(_ url: URL, as wanted: String) async {
        // Opening the app with a link can arrive before the profiles have.
        for _ in 0..<50 where !profilesLoaded { try? await Task.sleep(for: .milliseconds(200)) }
        guard let profile = profiles.first(where: { $0.id == wanted }) ?? profiles.first(where: { $0.name.caseInsensitiveCompare(wanted) == .orderedSame })
        else { return open(url: url) }
        if profile.isCurrent && !needsProfileChoice { return open(url: url) }
        if !profile.needsPinToChoose, (try? await switchProfile(to: profile)) != nil { return open(url: url) }
        afterProfileChoice = url
        linkProfile = profile
        player.stop()
        needsProfileChoice = true
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

    /// Tells the server what this is, for its lists of devices.
    static func identify() {
        FlexarrClient.deviceType = "AppleTv"
    }
    #else
    /// Set once at launch (`UIDevice` is main-actor only, and messages are made anywhere); iPhone until then.
    nonisolated(unsafe) static var name = "iPhone"

    /// Also tells the server what this is, for its lists of devices.
    @MainActor static func identify() {
        let pad = UIDevice.current.userInterfaceIdiom == .pad
        name = pad ? "iPad" : "iPhone"
        FlexarrClient.deviceType = pad ? "IPad" : "IPhone"
    }
    #endif
}

/// Navigation destinations shared by every tab.
enum Route: Hashable {
    /// An item we already have a summary for, so the header can render before detail loads.
    case item(ItemSummary)
    case itemID(String)
    case person(id: String, name: String)
    /// Every title in a library, sorted and optionally narrowed to one genre.
    case library(Library, LibrarySort, genre: String? = nil)
    case playlist(PlaylistSummary)
    /// The shows followed and their newest episodes (iPhone and iPad).
    case podcasts
    /// Finding podcasts in Apple's directory (iPhone and iPad).
    case podcastBrowse
    /// The channels and what's on them (iPhone and iPad; the Apple TV has a tab).
    case liveTv
    /// A film or show from outside the library, found on Discover (Apple TV).
    case external(ExternalTitle)
    /// What the library has in a genre (Apple TV's Discover).
    case genre(String)
    /// The profile's watchlist.
    case watchlist
}

/// One navigation stack per tab.
@MainActor
@Observable
final class Router {
    var path: [Route] = []
    /// Now Playing is open over this tab (the iPhone's music and podcast player).
    var showsNowPlaying = false

    func push(_ route: Route) {
        path.append(route)
    }
}

private extension Profile {
    func marked(current: Bool) -> Profile {
        Profile(id: id, name: name, color: color, hasPin: hasPin, isMain: isMain, isCurrent: current,
                hiddenLibraryIds: hiddenLibraryIds, image: image, libraryOrder: libraryOrder, asksPin: asksPin,
                maxRatingAge: maxRatingAge, isKids: isKids)
    }
}
