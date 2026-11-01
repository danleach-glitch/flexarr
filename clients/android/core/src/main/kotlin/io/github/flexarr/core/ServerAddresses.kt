package io.github.flexarr.core

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
 */
class ServerAddresses(val home: HttpUrl, remote: HttpUrl?) {
    val remote: HttpUrl? = remote?.takeIf { it != home }

    @Volatile
    private var _current: HttpUrl = home
    private var homeCheckedAt: Long? = null

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
        if (url != home && _current == home) homeCheckedAt = System.currentTimeMillis()
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

        private val registry = mutableMapOf<String, ServerAddresses>()

        /** The one instance for this pair of addresses, so clients made at different times share what they've learnt. */
        @Synchronized
        fun shared(home: HttpUrl, remote: HttpUrl?): ServerAddresses =
            registry.getOrPut("$home|${remote ?: ""}") { ServerAddresses(home, remote) }
    }
}
