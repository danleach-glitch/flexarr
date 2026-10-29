package io.github.flexarr.core

import kotlinx.serialization.serializer
import org.junit.Assert.assertEquals
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
}
