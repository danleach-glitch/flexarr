package io.github.flexarr.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** Reports of watching done offline, kept through signing out until they reach the server and profile they're for. */
class UnsentReportsTest {
    private val home = "http://192.168.1.10:8383"
    private val at = 1_800_000_000_000L

    private fun report(item: String, server: String = home, profile: String = "p", offset: Long = 0, position: Double = 100.0) =
        UnsentReport(server, profile, item, position, finished = false, playedAtMillis = at + offset)

    @Test
    fun `a server is known by its address however it's written`() {
        assertEquals("http://mini.local:8383", UnsentReports.serverKey("HTTP://Mini.Local:8383/"))
        assertEquals("http://mini.local:8383", UnsentReports.serverKey("http://mini.local:8383"))
        assertEquals("https://example.com/flexarr", UnsentReports.serverKey("https://example.com/flexarr/"))
    }

    @Test
    fun `only the server and profile it was watched as send it`() {
        val kept = listOf(report("a"), report("b", profile = "kid"), report("c", server = "http://other:8383"))

        assertEquals(listOf("a"), UnsentReports.due(kept, home, "p").map { it.itemId })
        assertEquals(listOf("b"), UnsentReports.due(kept, home, "kid").map { it.itemId })
        // The main profile has the same id on every server: another server's play isn't this one's.
        assertEquals(listOf("c"), UnsentReports.due(kept, "http://other:8383", "p").map { it.itemId })
        // Not known yet (signed in, the profile not chosen, or a family guest): nothing goes.
        assertTrue(UnsentReports.due(kept, home, null).isEmpty())
        assertTrue(UnsentReports.due(kept, null, "p").isEmpty())
    }

    @Test
    fun `a title keeps only its newest report`() {
        val kept = UnsentReports.adding(listOf(report("a", offset = 10, position = 200.0)), listOf(report("a"), report("b")))
        assertEquals(2, kept.size)
        assertEquals(200.0, kept.first { it.itemId == "a" }.position, 0.0)

        val older = UnsentReports.adding(listOf(report("a", offset = -10, position = 50.0)), kept)
        assertEquals(200.0, older.first { it.itemId == "a" }.position, 0.0)

        // Another profile's play of the same title is its own.
        assertEquals(3, UnsentReports.adding(listOf(report("a", profile = "kid")), kept).size)
    }

    @Test
    fun `sending removes only what was sent`() {
        val sent = report("a")
        assertEquals(listOf("b"), UnsentReports.removing(sent, listOf(sent, report("b"))).map { it.itemId })
        // Watched again while it was on its way: the newer place is still to send.
        val newer = report("a", offset = 30, position = 400.0)
        assertEquals(listOf(newer), UnsentReports.removing(sent, listOf(newer)))
    }

    @Test
    fun `a title the server hasn't is tried a few times, then dropped`() {
        var count = 0
        var tries = 1
        while (true) {
            count = UnsentReports.notFoundAgain(count) ?: break
            tries++
        }
        assertEquals(UnsentReports.NOT_FOUND_LIMIT, tries)
        assertEquals(1, UnsentReports.notFoundAgain(0))
        assertNull(UnsentReports.notFoundAgain(UnsentReports.NOT_FOUND_LIMIT - 1))
    }

    @Test
    fun `a kept list reads back, and one from before the count still reads`() {
        val kept = listOf(UnsentReport(home, "p", "a", 12.5, finished = true, playedAtMillis = at, notFound = 1))
        val serializer = kotlinx.serialization.builtins.ListSerializer(UnsentReport.serializer())
        assertEquals(kept, FlexarrClient.json.decodeFromString(serializer, FlexarrClient.json.encodeToString(serializer, kept)))
        val old = """{"position":10.0,"finished":false,"playedAtMillis":1}"""
        assertEquals(0, FlexarrClient.json.decodeFromString(UnsentProgress.serializer(), old).notFound)
    }
}
