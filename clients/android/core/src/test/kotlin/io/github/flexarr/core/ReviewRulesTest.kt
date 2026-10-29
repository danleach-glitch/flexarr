package io.github.flexarr.core

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.time.ZoneOffset

/** The iPhone app's ReviewPrompt rules: three watches to the end, on two days, a week after first opening, once a version. */
class ReviewRulesTest {
    private class MemoryStore : ReviewRules.Store {
        override var firstOpened: Long? = null
        override var watches = 0
        override var days: List<String> = emptyList()
        override var askedVersion: String? = null
    }

    private val day = 24L * 60 * 60 * 1000
    private val start = 1_800_000_000_000L
    private val store = MemoryStore()
    private fun rules(version: String = "0.55.0") = ReviewRules(store, version, ZoneOffset.UTC)

    @Test
    fun `asks after three watches on two days, a week after first opening`() {
        val rules = rules()
        rules.opened(start)
        assertFalse(rules.watchedToEnd(closing = true, now = start + 8 * day))
        assertFalse(rules.watchedToEnd(closing = true, now = start + 8 * day + 1000))
        assertTrue(rules.watchedToEnd(closing = true, now = start + 9 * day))
    }

    @Test
    fun `not on one day alone`() {
        val rules = rules()
        rules.opened(start)
        repeat(5) { assertFalse(rules.watchedToEnd(closing = true, now = start + 8 * day + it)) }
    }

    @Test
    fun `not within a week of first opening`() {
        val rules = rules()
        rules.opened(start)
        rules.watchedToEnd(closing = true, now = start + day)
        rules.watchedToEnd(closing = true, now = start + 2 * day)
        assertFalse(rules.watchedToEnd(closing = true, now = start + 3 * day))
        // Opening again doesn't move the first opening on.
        rules.opened(start + 6 * day)
        assertTrue(rules.watchedToEnd(closing = true, now = start + 7 * day))
    }

    @Test
    fun `not while the player carries on to the next episode`() {
        val rules = rules()
        rules.opened(start)
        rules.watchedToEnd(closing = false, now = start + 8 * day)
        rules.watchedToEnd(closing = false, now = start + 9 * day)
        assertFalse(rules.watchedToEnd(closing = false, now = start + 9 * day + 1))
        assertTrue(rules.watchedToEnd(closing = true, now = start + 9 * day + 2))
    }

    @Test
    fun `once a version`() {
        rules().opened(start)
        rules().watchedToEnd(closing = true, now = start + 8 * day)
        rules().watchedToEnd(closing = true, now = start + 9 * day)
        assertTrue(rules().watchedToEnd(closing = true, now = start + 9 * day + 1))
        rules().asked()
        repeat(3) { assertFalse(rules().watchedToEnd(closing = true, now = start + (10 + it) * day)) }
        // The next version may ask again, once it's been watched enough since.
        assertTrue(rules("0.56.0").watchedToEnd(closing = true, now = start + 14 * day))
    }
}
