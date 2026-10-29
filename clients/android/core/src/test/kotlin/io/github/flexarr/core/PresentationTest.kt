package io.github.flexarr.core

import kotlinx.serialization.serializer
import org.junit.Assert.assertEquals
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
        assertEquals(movie.releaseYear?.toString(), movie.facts.first())
        assertTrue(movie.facts.any { it.endsWith("m") || it.endsWith("h") })
    }

    @Test
    fun episodeTitles() {
        val episode = fixture<Home>("home").rows[0].items[0]
        assertEquals("Breaking Bad", episode.displayTitle)
        assertEquals("S2 E3  Bit by a Dead Bee", episode.episodeSubtitle)
        assertEquals("S2 E3", episode.episodeCode)
    }

    @Test
    fun theServersAnswersDecode() {
        fixture<List<Library>>("libraries")
        fixture<Paged<ItemSummary>>("library-items")
        fixture<ItemDetail>("item-series")
        fixture<ItemDetail>("item-season")
        fixture<SystemStatus>("system-status")
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
    }

    @Test
    fun `falls back to the final seconds without a credits marker`() {
        assertEquals(PlaybackPrompt.None, PlaybackPrompt.at(2790.0, emptyList(), 2831.0, hasNext = true))
        assertEquals(PlaybackPrompt.NextEpisode, PlaybackPrompt.at(2805.0, emptyList(), 2831.0, hasNext = true))
    }

    @Test
    fun `real playback fixture`() {
        val info = fixture<PlaybackInfo>("playback")
        assertEquals(PlaybackPrompt.Skip(MarkerType.Intro, 234.34), info.prompt(216.0))
        assertEquals(PlaybackPrompt.NextEpisode, info.prompt(2820.0))
    }
}
