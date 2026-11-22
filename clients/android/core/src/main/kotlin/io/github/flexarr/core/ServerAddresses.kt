package io.github.flexarr.core

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Deferred
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.async
import okhttp3.HttpUrl

/**
 * Where a server is reached: the address it was connected with (usually on the home network) and, once the server
 * has remote access turned on, its address for away from home (over Tailscale). Requests go to whichever answered
 * last. A GET that can't connect there, or hasn't heard back after a moment, is also sent to the other address and
 * the first answer wins; a write only moves over once the first address has failed to connect, so it's never sent
 * twice. While away, home is tried again every few minutes so the app goes back to it once it's reachable.
 *
 * Shared by every client made for the same sign-in (see [shared]), so what one request learns, the next one uses. As
 * FlexarrKit's ServerAddresses.
 *
 * A plain-http home address is only a private address, which another network (a hotel's, say) may use too. So once the
 * server's id is known ([serverId]), the sign-in goes to home only after home has answered, without the sign-in, with
 * that id on the network in use now ([checkHome]); a new network ([networkChanged]) or leaving home asks again.
 *
 * The away address is asked the same once, when the app kept it without hearing from it ([remoteConfirmed] false:
 * Tailscale wasn't connected on the device when the server gave it). Until it answers with the server's id it gets
 * nothing signed in ([checkRemote]); once it has, it stays trusted for this launch. The id isn't secret (anyone may
 * ask for it), so this catches a different server at the address (a Tailscale address handed to another machine, a
 * name that now leads elsewhere), not one set up to pretend.
 *
 * [awayIsTunnel]: the away address is the tunnel inside the app (a household device that joined by invite, whose server
 * said where home is). Then GETs at home aren't raced to it (that would start the tunnel at home on every slow
 * request): it's only used once home can't be reached or hasn't shown it's this server. A refusal of the sign-in at
 * home ([homeRefused]) moves to it rather than signing out, and a new network asks home again at the next request.
 */
class ServerAddresses(val home: HttpUrl, remote: HttpUrl?, val awayIsTunnel: Boolean = false) {
    val remote: HttpUrl? = remote?.takeIf { it != home }

    @Volatile
    private var _current: HttpUrl = home
    private var homeCheckedAt: Long? = null

    /**
     * The server's own id ([SystemStatus.serverId]), once known: what home must answer with before it's sent the
     * sign-in. Null (a server before 0.55, or a sign-in that hasn't learnt it yet) sends it as before.
     */
    @Volatile
    var serverId: String? = null

    @Volatile
    private var homeVerified = false
    private var homeCheck: Deferred<Boolean>? = null

    /**
     * False while [remote] hasn't yet answered this device with the server's id: it's asked before the sign-in. Set
     * when made; [shared] only ever moves it to true.
     */
    @Volatile
    var remoteConfirmed: Boolean = true

    @Volatile
    private var remoteVerified = false
    private var remoteCheck: Deferred<Boolean>? = null

    /** When the away address last failed to answer with the server's id (see [REMOTE_MISS_MS]). */
    @Volatile
    private var remoteMissedAt: Long? = null

    /**
     * Set by a new network while the away address is plain http (a 100.x Tailscale address): that range is also used
     * by some mobile networks for their own machines, so even a confirmed one is asked again there.
     */
    @Volatile
    private var remoteAskAgain = false

    /** Whether the away address may be sent the sign-in now without asking it first. */
    val remoteTrustedNow: Boolean get() = serverId == null || remoteVerified || (remoteConfirmed && !remoteAskAgain)

    /** The away address has answered with the server's id since this was made (it needn't be asked again). */
    val remoteHasProven: Boolean get() = remoteVerified

    /**
     * Whether the away address may be sent the sign-in, asking it with [verify] (once at a time, however many requests
     * wait) when it isn't confirmed yet.
     */
    internal suspend fun checkRemote(now: Long = System.currentTimeMillis(), verify: suspend () -> Boolean): Boolean {
        if (remoteTrustedNow) return true
        // Asked lately and it didn't answer: still not there, rather than every request waiting on it afresh.
        remoteMissedAt?.let { if (now - it < REMOTE_MISS_MS) return false }
        val check = synchronized(this) {
            remoteCheck?.takeIf { it.isActive } ?: checks.async { verify() }.also { remoteCheck = it }
        }
        val ok = runCatching { check.await() }.getOrDefault(false)
        if (ok) {
            val first = synchronized(this) { !remoteVerified.also { remoteVerified = true } }
            remoteAskAgain = false
            remoteMissedAt = null
            if (first) onRemoteProven?.invoke(this)
        } else {
            remoteMissedAt = System.currentTimeMillis()
        }
        return ok
    }

    /** Whether home may be sent the sign-in now without asking it first. */
    val homeTrustedNow: Boolean get() = !homeRefusedHere && (homeVerified || serverId == null || home.isHttps)

    /**
     * Home refused the sign-in on this network ([homeRefused]): it's left alone until the next one, the away address (the
     * tunnel) carrying on.
     */
    @Volatile
    private var homeRefusedHere = false

    /** Home has shown it's this server on the network in use, and nothing has made it ask again since. */
    val homeProven: Boolean get() = homeVerified && !homeRefusedHere

    /**
     * Whether home may be sent the sign-in, asking it with [verify] (once at a time, however many requests wait) when
     * that's not yet known on this network.
     */
    internal suspend fun checkHome(verify: suspend () -> Boolean): Boolean {
        if (homeTrustedNow) return true
        if (homeRefusedHere) return false
        val check = synchronized(this) {
            homeCheck?.takeIf { it.isActive } ?: checks.async { verify() }.also { homeCheck = it }
        }
        val ok = runCatching { check.await() }.getOrDefault(false)
        if (ok) homeVerified()
        return ok
    }

    /** Home answered with the server's id just now (announced as a change of route the first time on this network). */
    internal fun homeVerified() {
        val first = synchronized(this) { !homeVerified.also { homeVerified = true } }
        if (first && awayIsTunnel) onRouteChanged?.invoke(this)
    }

    /**
     * [url], a request at home, was refused the sign-in (401). With the tunnel as the away address that's a server that
     * doesn't take this sign-in from home (before 0.67) or a stranger: the tunnel takes over until another network, and
     * true says to send the request again (through it). Otherwise false: a refusal is the server's word.
     */
    @Synchronized
    fun homeRefused(url: HttpUrl): Boolean {
        if (!awayIsTunnel || remote == null || !url.isAt(home)) return false
        homeRefusedHere = true
        homeVerified = false
        reached(remote)
        return true
    }

    /** Another network: whatever answers at home there has to show it's this server again. */
    fun networkChanged() {
        homeVerified = false
        // Tailscale may have come up with it.
        remoteMissedAt = null
        if (awayIsTunnel) {
            // The tunnel is the app's own (nothing else on any network answers it), and home may be here: asked at once.
            homeRefusedHere = false
            synchronized(this) { homeCheckedAt = null }
            return
        }
        if (remote?.isHttps == false) {
            remoteVerified = false
            remoteAskAgain = true
        }
    }

    /** The address requests go to now. */
    val current: HttpUrl get() = _current

    /** Away from home: reaching the server by its remote address. */
    val isAway: Boolean get() = _current != home

    /**
     * [url] as it reads at home: one resolved against the away address has that address swapped for home's, and any
     * other is left as it is. What artwork is cached by, so a poster fetched away from home is the same one at home
     * (as FlexarrKit keys artwork).
     */
    fun homeForm(url: String): String {
        val remote = remote?.toString() ?: return url
        return if (url.startsWith(remote)) home.toString() + url.substring(remote.length) else url
    }

    /** The other address, if there is one. */
    fun alternate(to: HttpUrl): HttpUrl? {
        val remote = remote ?: return null
        return if (to == remote) home else remote
    }

    /** Whether a GET at [primary] that's slow to answer is also sent to the other address: never from home to the tunnel. */
    internal fun racesFrom(primary: HttpUrl): Boolean = !(awayIsTunnel && primary == home)

    /** On the tunnel inside the app now: it's the away address, and the one in use. */
    val onTunnel: Boolean get() = awayIsTunnel && isAway

    /** [url] answered: send everything there from now on. */
    fun reached(url: HttpUrl) {
        val moved = synchronized(this) {
            // Leaving home starts the clock for trying it again.
            if (url != home && _current == home) {
                homeCheckedAt = System.currentTimeMillis()
                homeVerified = false
            }
            (_current != url).also { _current = url }
        }
        if (moved) onRouteChanged?.invoke(this)
    }

    /**
     * Starts on the away address (where the last launch left it), with home asked about at the first request rather than
     * after the usual few minutes. Only before any request has been sent.
     */
    @Synchronized
    internal fun startAway() {
        val remote = remote ?: return
        _current = remote
        homeCheckedAt = null
    }

    /** True once per interval while away: time to see whether home answers again. */
    @Synchronized
    internal fun shouldCheckHome(now: Long = System.currentTimeMillis()): Boolean {
        if (_current == home || homeRefusedHere) return false
        homeCheckedAt?.let { if (now - it < HOME_CHECK_INTERVAL_MS) return false }
        homeCheckedAt = now
        return true
    }

    companion object {
        /** How long to wait for the current address before also asking the other one (GETs only). */
        const val ALTERNATE_DELAY_MS = 2_500L

        /** How long the reachability check gives the address in use before asking the other one too. */
        const val PROBE_HEAD_START_MS = 1_500L

        /** While away, how often home is tried again. */
        const val HOME_CHECK_INTERVAL_MS = 5 * 60_000L

        /** How long home has to answer with the server's id before the sign-in goes elsewhere. */
        const val HOME_PROBE_MS = 2_000L

        /**
         * How long an away address that didn't answer with the server's id counts as not there before it's asked again
         * (sooner on a new network), so while Tailscale is off each request isn't held up asking it.
         */
        const val REMOTE_MISS_MS = 30_000L

        /**
         * Called when an away address kept unconfirmed first answers with the server's id, so the app can keep it as
         * confirmed straight away. Set once at launch.
         */
        @Volatile
        var onRemoteProven: ((ServerAddresses) -> Unit)? = null

        /**
         * Called whenever the address in use moves (home to away or back), or home first proves it's the server on a
         * network with the tunnel as the away address, off whatever thread found out, so the app can follow the route
         * (a household device's tunnel stopping at home). Set once at launch.
         */
        @Volatile
        var onRouteChanged: ((ServerAddresses) -> Unit)? = null

        private val registry = mutableMapOf<String, ServerAddresses>()

        /** Where [checkHome]'s questions run, so one caller giving up doesn't cancel it for the others waiting. */
        private val checks = CoroutineScope(SupervisorJob() + Dispatchers.IO)

        /** The device moved to another network: every server's home has to show it's that server again. */
        @Synchronized
        fun networkChangedEverywhere() {
            registry.values.forEach { it.networkChanged() }
        }

        /**
         * The one instance for this pair of addresses and the server expected there, so clients made at different times
         * share what they've learnt. The away address being confirmed carries over to it, and is never taken back: the
         * same away address of the same server can't stop being that server's.
         */
        @Synchronized
        fun shared(
            home: HttpUrl,
            remote: HttpUrl?,
            serverId: String? = null,
            remoteConfirmed: Boolean = true,
            awayIsTunnel: Boolean = false,
            startAway: Boolean = false,
        ): ServerAddresses =
            registry.getOrPut("$home|${remote ?: ""}|${serverId ?: ""}|$awayIsTunnel") {
                ServerAddresses(home, remote, awayIsTunnel).also {
                    it.serverId = serverId
                    it.remoteConfirmed = remoteConfirmed
                    if (startAway) it.startAway()
                }
            }.also { if (remoteConfirmed) it.remoteConfirmed = true }
    }
}

/** The same server and port as [base] (whatever path follows). */
internal fun HttpUrl.isAt(base: HttpUrl): Boolean = scheme == base.scheme && host == base.host && port == base.port
