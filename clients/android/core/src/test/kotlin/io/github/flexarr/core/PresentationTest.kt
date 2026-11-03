package io.github.flexarr.core

import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.serializer
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** The same checks as FlexarrKit's PresentationTests, against the same fixtures, so both apps word things alike. */
class PresentationTest {
    private inline fun <reified T> fixture(name: String): T {
        val text = javaClass.classLoader!!.getResource("$name.json")!!.readText()
        return FlexarrClient.json.decodeFromString(serializer<T>(), text)
    }

    @Test
    fun runtimes() {
        assertEquals("1h 56m", Format.runtime(116))
        assertEquals("47m", Format.runtime(47))
        assertEquals("2h", Format.runtime(120))
        assertNull(Format.runtime(0))
    }

    @Test
    fun timestamps() {
        assertEquals("32:10", Format.timestamp(1930.0))
        assertEquals("1:02:05", Format.timestamp(3725.0))
        assertEquals("0:00", Format.timestamp(-4.0))
    }

    @Test
    fun remaining() {
        assertEquals("42m left", Format.remaining(42 * 60.0))
        assertNull(Format.remaining(30.0))
    }

    @Test
    fun movieFacts() {
        val movie = fixture<ItemDetail>("item-movie").item
        assertEquals(movie.releaseYear?.toString(), movie.facts().first())
        assertTrue(movie.facts().any { it.endsWith("m") || it.endsWith("h") })
    }

    @Test
    fun episodeTitles() {
        val episode = fixture<Home>("home").rows[0].items[0]
        assertEquals("Breaking Bad", episode.displayTitle)
        assertEquals("S2E3  Bit by a Dead Bee", episode.episodeSubtitle)
        assertEquals("S2E3", episode.episodeCode)
    }

    @Test
    fun theServersAnswersDecode() {
        fixture<List<Library>>("libraries")
        fixture<Paged<ItemSummary>>("library-items")
        fixture<ItemDetail>("item-series")
        fixture<ItemDetail>("item-season")
        fixture<SystemStatus>("system-status")
    }

    @Test
    fun `a server before 0_53 says what only the first featured title resumes`() {
        // Home as a server before 0.53 sends it: one `resume`, for the first featured title, and no `resumes`.
        val captured = FlexarrClient.json.parseToJsonElement(javaClass.classLoader!!.getResource("home.json")!!.readText()).jsonObject
        val featured = captured.getValue("featured").jsonArray
        val episode = captured.getValue("rows").jsonArray[0].jsonObject.getValue("items").jsonArray[0]
        val older = JsonObject(captured - "resumes" + ("resume" to episode))
        val home = FlexarrClient.json.decodeFromJsonElement(serializer<Home>(), older)
        assertNull(home.resumes)
        val first = home.featured.first()
        assertEquals(FlexarrClient.json.decodeFromJsonElement(serializer<ItemSummary>(), episode), home.resuming(first))
        if (featured.size > 1) assertNull(home.resuming(home.featured[1]))
        // A newer server's list is what counts, even for the first.
        assertNull(home.copy(resumes = emptyMap()).resuming(first))
    }

    private val markers = listOf(
        Marker(MarkerType.Recap, 0.0, 45.0),
        Marker(MarkerType.Intro, 215.26, 234.34),
        Marker(MarkerType.Credits, 2811.72, 2831.26),
    )

    @Test
    fun `offers skip inside intro and recap`() {
        assertEquals(PlaybackPrompt.Skip(MarkerType.Recap, 45.0), PlaybackPrompt.at(10.0, markers, 2831.0, hasNext = true))
        assertEquals(PlaybackPrompt.Skip(MarkerType.Intro, 234.34), PlaybackPrompt.at(220.0, markers, 2831.0, hasNext = true))
        assertEquals("not in the final second", PlaybackPrompt.None, PlaybackPrompt.at(234.0, markers, 2831.0, hasNext = true))
        assertEquals(PlaybackPrompt.None, PlaybackPrompt.at(1000.0, markers, 2831.0, hasNext = true))
    }

    @Test
    fun `offers next episode once credits start`() {
        assertEquals(PlaybackPrompt.None, PlaybackPrompt.at(2811.0, markers, 2831.0, hasNext = true))
        assertEquals(PlaybackPrompt.NextEpisode, PlaybackPrompt.at(2812.0, markers, 2831.0, hasNext = true))
        assertEquals(PlaybackPrompt.None, PlaybackPrompt.at(2812.0, markers, 2831.0, hasNext = false))
        // Skip credits with auto-play: 8 seconds before them.
        assertEquals(PlaybackPrompt.None, PlaybackPrompt.at(2803.0, markers, 2831.0, hasNext = true, skipCredits = true, autoPlayNext = true))
        assertEquals(PlaybackPrompt.NextEpisode, PlaybackPrompt.at(2804.0, markers, 2831.0, hasNext = true, skipCredits = true, autoPlayNext = true))
    }

    @Test
    fun `falls back to a minute before the end without a credits marker`() {
        assertEquals(PlaybackPrompt.None, PlaybackPrompt.at(2770.0, emptyList(), 2831.0, hasNext = true))
        assertEquals(PlaybackPrompt.NextEpisode, PlaybackPrompt.at(2771.0, emptyList(), 2831.0, hasNext = true))
    }

    private val credits = listOf(Marker(MarkerType.Credits, 1300.0, 1400.0))

    @Test
    fun `credits, skip credits and auto-play`() {
        // Shows 8s before the credits and counts down to 2s into them, then the next episode plays.
        assertEquals(NextEpisodeTiming(1292.0, 1292.0, 1302.0, null), nextEpisodeTiming(credits, 1400.0, skipCredits = true, autoPlayNext = true))
    }

    @Test
    fun `credits and auto-play without skip credits`() {
        // Shows at the credits; the last 10s count down to the end.
        assertEquals(NextEpisodeTiming(1300.0, 1390.0, 1400.0, null), nextEpisodeTiming(credits, 1400.0, skipCredits = false, autoPlayNext = true))
    }

    @Test
    fun `credits and skip credits without auto-play`() {
        // Shows at the credits, no countdown, and the player closes there.
        assertEquals(NextEpisodeTiming(1300.0, null, null, 1300.0), nextEpisodeTiming(credits, 1400.0, skipCredits = true, autoPlayNext = false))
    }

    @Test
    fun `credits with neither`() {
        // Shows at the credits; the player closes at the end.
        assertEquals(NextEpisodeTiming(1300.0, null, null, null), nextEpisodeTiming(credits, 1400.0, skipCredits = false, autoPlayNext = false))
    }

    @Test
    fun `no credits with auto-play`() {
        // A minute before the end, either way Skip credits is set; the last 10s count down.
        for (skip in listOf(true, false)) {
            assertEquals(NextEpisodeTiming(1340.0, 1390.0, 1400.0, null), nextEpisodeTiming(emptyList(), 1400.0, skipCredits = skip, autoPlayNext = true))
        }
    }

    @Test
    fun `no credits without auto-play`() {
        for (skip in listOf(true, false)) {
            assertEquals(NextEpisodeTiming(1340.0, null, null, null), nextEpisodeTiming(emptyList(), 1400.0, skipCredits = skip, autoPlayNext = false))
        }
    }

    @Test
    fun `credits starting under 8 seconds in`() {
        val early = listOf(Marker(MarkerType.Credits, 5.0, 60.0))
        assertEquals(NextEpisodeTiming(0.0, 0.0, 7.0, null), nextEpisodeTiming(early, 60.0, skipCredits = true, autoPlayNext = true))
        assertEquals(7, nextEpisodeTiming(early, 60.0, skipCredits = true, autoPlayNext = true)!!.secondsLeft(0.0))
    }

    @Test
    fun `short episode without credits shows no button`() {
        assertNull(nextEpisodeTiming(emptyList(), 120.0, skipCredits = false, autoPlayNext = true))
        assertNull(nextEpisodeTiming(emptyList(), 90.0, skipCredits = true, autoPlayNext = false))
        assertEquals(PlaybackPrompt.None, PlaybackPrompt.at(119.0, emptyList(), 120.0, hasNext = true))
        assertEquals(NextEpisodeTiming(61.0, 111.0, 121.0, null), nextEpisodeTiming(emptyList(), 121.0, skipCredits = false, autoPlayNext = true))
    }

    @Test
    fun `countdown seconds follow the position`() {
        val timing = nextEpisodeTiming(credits, 1400.0, skipCredits = true, autoPlayNext = true)!!
        assertNull(timing.secondsLeft(1291.9))
        assertEquals(10, timing.secondsLeft(1292.0))
        assertEquals(10, timing.secondsLeft(1292.5))
        assertEquals(9, timing.secondsLeft(1293.0))
        assertEquals(1, timing.secondsLeft(1301.5))
        assertEquals(0, timing.secondsLeft(1302.0))
        assertEquals(0, timing.secondsLeft(1310.0))
        assertNull(nextEpisodeTiming(credits, 1400.0, skipCredits = true, autoPlayNext = false)!!.secondsLeft(1350.0))
    }

    @Test
    fun `real playback fixture`() {
        val info = fixture<PlaybackInfo>("playback")
        assertEquals(PlaybackPrompt.Skip(MarkerType.Intro, 234.34), info.prompt(216.0))
        assertEquals(PlaybackPrompt.NextEpisode, info.prompt(2820.0))
    }

    @Test
    fun `compares server versions`() {
        assertTrue(ServerVersion.atLeast("0.53.0", 0, 53))
        assertTrue(ServerVersion.atLeast("0.54.0-dev.1a2b3c4", 0, 53))
        assertTrue(ServerVersion.atLeast("1.0.0+d4c892c6190c", 0, 53))
        assertFalse(ServerVersion.atLeast("0.52.9", 0, 53))
        assertFalse(ServerVersion.atLeast("dev", 0, 53))
    }
}
