package io.github.flexarr.core

import java.io.File
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.boolean
import kotlinx.serialization.json.double
import kotlinx.serialization.json.doubleOrNull
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
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

    /**
     * Every case in docs/next-episode-timing.json, the cases FlexarrKit and the web test against too, so the three
     * can't drift. Found from the test's working directory (the module's), up to the repo root.
     */
    @Test
    fun `next episode timing matches the shared cases`() {
        val file = generateSequence(File("").absoluteFile) { it.parentFile }
            .map { File(it, "docs/next-episode-timing.json") }
            .firstOrNull { it.isFile } ?: error("docs/next-episode-timing.json not found above ${File("").absolutePath}")
        val cases = FlexarrClient.json.parseToJsonElement(file.readText()).jsonObject.getValue("cases").jsonArray
        assertTrue(cases.isNotEmpty())
        for (element in cases) {
            val case = element.jsonObject
            val name = case.getValue("name").jsonPrimitive.content
            val duration = case.getValue("duration").jsonPrimitive.double
            val markers = case["credits"]?.jsonPrimitive?.doubleOrNull?.let { listOf(Marker(MarkerType.Credits, it, duration)) } ?: emptyList()
            val actual = nextEpisodeTiming(
                markers, duration,
                skipCredits = case.getValue("skipCredits").jsonPrimitive.boolean,
                autoPlayNext = case.getValue("autoPlayNext").jsonPrimitive.boolean,
            )
            val expect = case["expect"]?.takeIf { it !is JsonNull }?.jsonObject
            if (expect == null) {
                assertNull(name, actual)
                continue
            }
            fun field(key: String) = expect[key]?.jsonPrimitive?.double
            val expected = NextEpisodeTiming(field("showAt")!!, field("countdownFrom"), field("nextAt"), field("closeAt"))
            assertEquals(name, expected, actual)
        }
    }

    /** Every case in docs/skip-timing.json, the cases FlexarrKit and the web test against too. */
    @Test
    fun `skip timing matches the shared cases`() {
        val file = generateSequence(File("").absoluteFile) { it.parentFile }
            .map { File(it, "docs/skip-timing.json") }
            .firstOrNull { it.isFile } ?: error("docs/skip-timing.json not found above ${File("").absolutePath}")
        val root = FlexarrClient.json.parseToJsonElement(file.readText()).jsonObject
        fun JsonObject.marker() = Marker(
            MarkerType.entries.firstOrNull { it.raw == getValue("type").jsonPrimitive.content } ?: MarkerType.Unknown,
            getValue("start").jsonPrimitive.double, getValue("end").jsonPrimitive.double,
        )
        fun JsonObject.expected() = this["expect"]?.takeIf { it !is JsonNull }?.jsonObject?.let {
            SkipTiming(it.getValue("countdownFrom").jsonPrimitive.double, it.getValue("skipAt").jsonPrimitive.double)
        }
        fun JsonObject.name() = getValue("name").jsonPrimitive.content
        val timing = root.getValue("timing").jsonArray.map { it.jsonObject }
        assertTrue(timing.size >= 5)
        for (case in timing) assertEquals(case.name(), case.expected(), SkipTiming.of(case.marker()))
        for (case in root.getValue("landing").jsonArray.map { it.jsonObject }) {
            assertEquals(case.name(), case.expected(), SkipTiming.landing(case.marker(), case.getValue("at").jsonPrimitive.double))
        }
        for (case in root.getValue("countdown").jsonArray.map { it.jsonObject }) {
            val expected = case["expect"]?.takeIf { it !is JsonNull }?.jsonPrimitive?.double?.toInt()
            assertEquals(case.name(), expected, SkipTiming.of(case.marker())?.secondsLeft(case.getValue("at").jsonPrimitive.double))
        }
        for (case in root.getValue("chain").jsonArray.map { it.jsonObject }) {
            val markers = case.getValue("markers").jsonArray.map { it.jsonObject.marker() }
            val actual = SkipTiming.skipEnd(
                markers[case.getValue("skipping").jsonPrimitive.double.toInt()], markers,
                case.getValue("skipIntros").jsonPrimitive.boolean, case.getValue("skipRecaps").jsonPrimitive.boolean,
            )
            assertEquals(case.name(), case.getValue("expect").jsonPrimitive.double, actual, 0.0)
        }
    }

    @Test
    fun `skip fill follows the position`() {
        val t = SkipTiming(117.0, 120.0)
        assertEquals(0f, t.progress(116.0))
        assertEquals(0.5f, t.progress(118.5))
        assertEquals(1f, t.progress(121.0))
    }

    private val credits = listOf(Marker(MarkerType.Credits, 1300.0, 1400.0))

    @Test
    fun `no button before the end of a short episode without credits`() {
        assertEquals(PlaybackPrompt.None, PlaybackPrompt.at(119.0, emptyList(), 120.0, hasNext = true))
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
        // Credits under 8 seconds in: a shorter countdown from the start.
        assertEquals(7, nextEpisodeTiming(listOf(Marker(MarkerType.Credits, 5.0, 60.0)), 60.0, skipCredits = true, autoPlayNext = true)!!.secondsLeft(0.0))
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
