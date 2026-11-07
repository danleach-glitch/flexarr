package io.github.flexarr.core

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Deferred
import kotlinx.coroutines.async

/**
 * One answer for everyone who wants the same thing around the same time, so the server is asked once: whoever [get]s
 * it while it's being fetched waits for that fetch, and for a while afterwards its answer is given again without
 * asking. The server's status, say, which the server's name, its error-report setting, its address for away from home
 * and the version checks all want as the app starts.
 *
 * Kept per [K] (the sign-in it was asked as): another key asks afresh, and replaces what was kept. A fetch that fails
 * is never given again; everyone waiting on it gets what it threw. The fetch runs in [scope] (one that a failure doesn't
 * bring down), so it finishes for the others when whoever asked first has gone away.
 */
class SharedAnswer<K, V>(private val scope: CoroutineScope, private val now: () -> Long = System::currentTimeMillis) {
    private class Asked<K, V>(val key: K, val atMillis: Long, val answer: Deferred<V>)

    private var last: Asked<K, V>? = null

    /**
     * The answer for [key]: the one being fetched now, or one asked for within the last [freshForMillis]; else [fetch]'s,
     * asked now and kept for the next.
     */
    suspend fun get(key: K, freshForMillis: Long, fetch: suspend () -> V): V =
        synchronized(this) {
            last?.takeIf { it.key == key && (it.answer.isActive || (!it.answer.isCancelled && now() - it.atMillis <= freshForMillis)) }
                ?: asking(key, fetch)
        }.answer.await()

    /** Asks now, whatever is kept or under way, and shares the answer with whoever [get]s it meanwhile or soon after. */
    suspend fun ask(key: K, fetch: suspend () -> V): V = synchronized(this) { asking(key, fetch) }.answer.await()

    /** What's kept is out of date (something changed it here): the next [get] asks. */
    fun forget() {
        synchronized(this) { last = null }
    }

    private fun asking(key: K, fetch: suspend () -> V): Asked<K, V> = Asked(key, now(), scope.async { fetch() }).also { last = it }
}
