package io.github.flexarr.core

import kotlinx.serialization.serializer
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.time.Instant

class LiveTvTest {
    private inline fun <reified T> fixture(name: String): T =
        FlexarrClient.json.decodeFromString(serializer<T>(), javaClass.classLoader!!.getResource("$name.json")!!.readText())

    @Test
    fun `reads the server's channels, guide and recordings`() {
        val channels = fixture<List<LiveChannel>>("live-channels")
        assertTrue(channels.isNotEmpty())
        assertNotNull(fixture<LiveGuide>("live-guide").channels.firstOrNull())
        fixture<Recordings>("recordings")
    }

    @Test
    fun `the live player asks what's on again once the programme ends, not every minute`() {
        val end = Instant.parse("2026-11-07T20:00:00Z")
        val asked = Instant.parse("2026-11-07T19:30:00Z")
        // Mid-programme, a minute or nine after asking: nothing to fetch.
        assertFalse(LiveNowRefresh.isDue(end, asked, asked.plusSeconds(60)))
        assertFalse(LiveNowRefresh.isDue(end, asked, asked.plusSeconds(540)))
        // It ended: once.
        assertTrue(LiveNowRefresh.isDue(end, asked, end))
        // Answered with it still (the server's clock a little behind): asked again each minute for a few minutes.
        assertTrue(LiveNowRefresh.isDue(end, end.plusSeconds(1), end.plusSeconds(61)))
        assertTrue(LiveNowRefresh.isDue(end, end.plusSeconds(120), end.plusSeconds(180)))
        assertFalse(LiveNowRefresh.isDue(end, end.plusSeconds(181), end.plusSeconds(241)))
        // Still the old programme after that (the guide ran out): every ten minutes, as with no guide at all.
        assertTrue(LiveNowRefresh.isDue(end, end.plusSeconds(181), end.plusSeconds(781)))
        assertFalse(LiveNowRefresh.isDue(null, asked, asked.plusSeconds(60)))
        assertTrue(LiveNowRefresh.isDue(null, asked, asked.plusSeconds(600)))
        // Shown from the channel list it was picked from: asked at the first chance if it's ended or there's none.
        assertFalse(LiveNowRefresh.isDue(end, null, asked))
        assertTrue(LiveNowRefresh.isDue(end, null, end))
        assertTrue(LiveNowRefresh.isDue(null, null, asked))
    }

    @Test
    fun `channel numbers and group names read as people know them`() {
        val channel = LiveChannel(id = "1", number = 101.0, name = "BBC One", group = "UK| NEWS ᴴᴰ/ᴿᴬᵂ")
        assertEquals("101", channel.numberText)
        assertEquals("101.5", channel.copy(number = 101.5).numberText)
        assertEquals("UK NEWS", channel.groupName)
    }

    @Test
    fun `a programme's title, and how far through it is`() {
        val start = Instant.parse("2026-10-29T04:00:00Z")
        val programme = LiveProgramme(1, start, start.plusSeconds(1800), "Doctor Who", subtitle = "The Star Beast")
        assertEquals("Doctor Who: The Star Beast", programme.displayTitle)
        assertEquals("Doctor Who", programme.copy(subtitle = "Doctor Who").displayTitle)
        assertEquals(0.5f, programme.progress(start.plusSeconds(900)), 0.001f)
    }

    @Test
    fun `a recording in the way is read from the server's problem`() {
        val problem = """{"title":"Recording","status":409,"detail":"News on BBC One is recording.","recordingId":"r","channelId":"c","channelName":"BBC One"}"""
        assertEquals(RecordingInTheWay("r", "c", "BBC One", "News on BBC One is recording."), RecordingInTheWay.parse(problem))
        assertNull(RecordingInTheWay.parse("""{"title":"Conflict"}"""))
    }

    @Test
    fun `times come with or without fractions and offsets`() {
        assertEquals(Instant.parse("2026-10-29T02:39:10.305557Z"), InstantSerializer.parseInstant("2026-10-29T02:39:10.305557Z"))
        assertEquals(Instant.parse("2026-10-29T03:00:00Z"), InstantSerializer.parseInstant("2026-10-29T04:00:00+01:00"))
    }

    @Test
    fun `a time with no zone is UTC, as on the Apple apps`() {
        assertEquals(Instant.parse("2026-10-29T04:00:00Z"), InstantSerializer.parseInstant("2026-10-29T04:00:00"))
        assertEquals(Instant.parse("2026-10-29T04:00:00.25Z"), InstantSerializer.parseInstant("2026-10-29T04:00:00.25"))
        val programme = FlexarrClient.json.decodeFromString(
            LiveProgramme.serializer(),
            """{"id":1,"start":"2026-10-29T04:00:00","end":"2026-10-29T05:00:00Z","title":"News"}""",
        )
        assertEquals(Instant.parse("2026-10-29T04:00:00Z"), programme.start)
    }
}
