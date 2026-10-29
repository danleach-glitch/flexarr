package io.github.flexarr.core

import java.time.Instant
import java.time.ZoneId

/**
 * When to ask for a rating (Google Play's in-app review): after a film or episode is watched to the end and the player
 * has closed, once Flexarr has been watched this way a few times on more than one day, at least a week after it was
 * first opened, and at most once a version. Google decides whether the prompt actually appears. The iPhone app's
 * ReviewPrompt, rule for rule.
 */
class ReviewRules(private val store: Store, private val version: String, private val zone: ZoneId = ZoneId.systemDefault()) {
    /** Where the counts are kept between launches (the app's preferences). */
    interface Store {
        /** When Flexarr was first opened, in milliseconds since 1970. */
        var firstOpened: Long?
        var watches: Int
        /** The last few days ("2026-10-29") something was watched to the end. */
        var days: List<String>
        var askedVersion: String?
    }

    /** The app opened: the first time is remembered, for the week's wait. */
    fun opened(now: Long = System.currentTimeMillis()) {
        if (store.firstOpened == null) store.firstOpened = now
    }

    /**
     * Something was watched into its credits. [closing]: the player is going away, so now is a good time to ask.
     * Returns whether the prompt should come up now.
     */
    fun watchedToEnd(closing: Boolean, now: Long = System.currentTimeMillis()): Boolean {
        val day = Instant.ofEpochMilli(now).atZone(zone).toLocalDate().toString()
        val days = store.days
        if (day !in days) store.days = (days + day).takeLast(DAYS_NEEDED)
        store.watches += 1
        return closing && shouldAsk(now)
    }

    /** The prompt was asked for: not again this version. */
    fun asked() {
        store.askedVersion = version
        store.watches = 0
        store.days = emptyList()
    }

    private fun shouldAsk(now: Long): Boolean {
        val firstOpened = store.firstOpened ?: return false
        return store.askedVersion != version &&
            store.watches >= WATCHES_NEEDED &&
            store.days.size >= DAYS_NEEDED &&
            now - firstOpened >= WAIT_AFTER_FIRST_OPEN_MS
    }

    companion object {
        const val WATCHES_NEEDED = 3
        const val DAYS_NEEDED = 2
        const val WAIT_AFTER_FIRST_OPEN_MS = 7L * 24 * 60 * 60 * 1000
    }
}
