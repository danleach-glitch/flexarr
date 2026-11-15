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
 */
class ServerAddresses(val home: HttpUrl, remote: HttpUrl?) {
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
    val homeTrustedNow: Boolean get() = homeVerified || serverId == null || home.isHttps

    /**
     * Whether home may be sent the sign-in, asking it with [verify] (once at a time, however many requests wait) when
     * that's not yet known on this network.
     */
    internal suspend fun checkHome(verify: suspend () -> Boolean): Boolean {
        if (homeTrustedNow) return true
        val check = synchronized(this) {
            homeCheck?.takeIf { it.isActive } ?: checks.async { verify() }.also { homeCheck = it }
        }
        val ok = runCatching { check.await() }.getOrDefault(false)
        if (ok) homeVerified = true
        return ok
    }

    /** Home answered with the server's id just now. */
    internal fun homeVerified() {
        homeVerified = true
    }

    /** Another network: whatever answers at home there has to show it's this server again. */
    fun networkChanged() {
        homeVerified = false
        // Tailscale may have come up with it.
        remoteMissedAt = null
        if (remote?.isHttps == false) {
            remoteVerified = false
            remoteAskAgain = true
        }
    }

    /** The address requests go to now. */
    val current: HttpUrl get() = _current

    /** Away from home: reaching the server by its remote address. */
    val isAway: Boolean get() = _current != home

    /** The other address, if there is one. */
    fun alternate(to: HttpUrl): HttpUrl? {
        val remote = remote ?: return null
        return if (to == remote) home else remote
    }

    /** [url] answered: send everything there from now on. */
    @Synchronized
    fun reached(url: HttpUrl) {
        // Leaving home starts the clock for trying it again.
        if (url != home && _current == home) {
            homeCheckedAt = System.currentTimeMillis()
            homeVerified = false
        }
        _current = url
    }

    /** True once per interval while away: time to see whether home answers again. */
    @Synchronized
    internal fun shouldCheckHome(now: Long = System.currentTimeMillis()): Boolean {
        if (_current == home) return false
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
        fun shared(home: HttpUrl, remote: HttpUrl?, serverId: String? = null, remoteConfirmed: Boolean = true): ServerAddresses =
            registry.getOrPut("$home|${remote ?: ""}|${serverId ?: ""}") {
                ServerAddresses(home, remote).also {
                    it.serverId = serverId
                    it.remoteConfirmed = remoteConfirmed
                }
            }.also { if (remoteConfirmed) it.remoteConfirmed = true }
    }
}
