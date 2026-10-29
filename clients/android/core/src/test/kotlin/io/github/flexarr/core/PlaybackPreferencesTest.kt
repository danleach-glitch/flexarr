package io.github.flexarr.core

import kotlinx.coroutines.test.runTest
import kotlinx.serialization.serializer
import mockwebserver3.MockResponse
import mockwebserver3.MockWebServer
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Before
import org.junit.Test

class PlaybackPreferencesTest {
    private lateinit var server: MockWebServer

    @Before
    fun start() {
        server = MockWebServer()
        server.start()
    }

    @After
    fun stop() {
        server.close()
    }

    private fun json(body: String) = MockResponse.Builder().addHeader("Content-Type", "application/json").body(body).build()

    @Test
    fun `reads the profile's preferences and saves them back`() = runTest {
        val client = FlexarrClient(server.url("/"))
        server.enqueue(
            json(
                """{"autoPlayNext":false,"skipIntros":true,"skipRecaps":false,"skipCredits":true,"subtitleSize":"ExtraLarge",""" +
                    """"subtitlePosition":"Raised","subtitlesInBlackBars":true}""",
            ),
        )

        val preferences = client.playbackPreferences()
        assertEquals("/api/v1/me/playback-preferences", server.takeRequest().target)
        assertEquals(
            PlaybackPreferences(
                autoPlayNext = false, skipIntros = true, skipCredits = true,
                subtitleSize = SubtitleSize.ExtraLarge, subtitlePosition = SubtitlePosition.Raised, subtitlesInBlackBars = true,
            ),
            preferences,
        )

        server.enqueue(MockResponse.Builder().code(204).build())
        client.setPlaybackPreferences(preferences.copy(skipRecaps = true).forSaving())
        val saved = server.takeRequest()
        assertEquals("PUT", saved.method)
        assertEquals(
            """{"autoPlayNext":false,"skipIntros":true,"skipRecaps":true,"skipCredits":true,"subtitleSize":"ExtraLarge",""" +
                """"subtitlePosition":"Raised","subtitlesInBlackBars":true}""",
            saved.body?.utf8(),
        )
    }

    @Test
    fun `an older server's preferences leave the subtitle ones out, and they stay out when saved`() {
        val preferences = decode("""{"autoPlayNext":true,"skipIntros":false,"skipRecaps":false,"skipCredits":false}""")
        assertNull(preferences.subtitleSize)
        assertEquals(SubtitleSize.Standard, preferences.shownSubtitleSize)
        assertEquals(SubtitlePosition.Bottom, preferences.shownSubtitlePosition)
        assertEquals(
            """{"autoPlayNext":true,"skipIntros":false,"skipRecaps":false,"skipCredits":false}""",
            FlexarrClient.json.encodeToString(serializer<PlaybackPreferences>(), preferences.forSaving()),
        )
    }

    @Test
    fun `a newer server's size and position show as the defaults and aren't sent back`() {
        val preferences = decode(
            """{"autoPlayNext":true,"skipIntros":false,"skipRecaps":false,"skipCredits":false,"subtitleSize":"Huge","subtitlePosition":"Middle","extra":1}""",
        )
        assertEquals(SubtitleSize.Unknown, preferences.subtitleSize)
        assertEquals(SubtitleSize.Standard, preferences.shownSubtitleSize)
        assertEquals(SubtitlePosition.Bottom, preferences.shownSubtitlePosition)
        val saving = preferences.forSaving()
        assertNull(saving.subtitleSize)
        assertNull(saving.subtitlePosition)
    }

    @Test
    fun `the playback fixture's preferences are optional`() {
        val text = javaClass.classLoader!!.getResource("playback.json")!!.readText()
        val info = FlexarrClient.json.decodeFromString(serializer<PlaybackInfo>(), text)
        assertEquals(PlaybackPreferences(), info.preferences ?: PlaybackPreferences())
    }

    private fun decode(text: String) = FlexarrClient.json.decodeFromString(serializer<PlaybackPreferences>(), text)
}
