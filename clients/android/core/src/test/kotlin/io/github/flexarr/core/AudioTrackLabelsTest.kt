package io.github.flexarr.core

import org.junit.Assert.assertEquals
import org.junit.Test

class AudioTrackLabelsTest {
    private fun track(index: Int, label: String, codec: String = "aac", language: String? = "en", channels: Int? = 2) =
        AudioTrack(index = index, label = label, codec = codec, language = language, channels = channels)

    @Test
    fun `tracks with their own languages keep the server's labels`() {
        val tracks = listOf(track(0, "English"), track(1, "French", language = "fr"))
        assertEquals(listOf("English", "French"), audioTrackLabels(tracks))
    }

    @Test
    fun `two untagged tracks are told apart by codec and channels`() {
        val tracks = listOf(track(0, "Unknown", codec = "mp3", language = null, channels = 2), track(1, "Unknown", codec = "ac3", language = null, channels = 6))
        assertEquals(listOf("Unknown (MP3 Stereo)", "Unknown (AC3 5.1)"), audioTrackLabels(tracks))
    }

    @Test
    fun `matching labels in a known language get details too`() {
        val tracks = listOf(track(0, "English", codec = "eac3", channels = 6), track(1, "English", codec = "aac", channels = 2), track(2, "German", language = "de"))
        assertEquals(listOf("English (EAC3 5.1)", "English (AAC Stereo)", "German"), audioTrackLabels(tracks))
    }

    @Test
    fun `tracks still alike after their details are numbered`() {
        val tracks = listOf(track(0, "Unknown", language = null), track(1, "Unknown", language = null))
        assertEquals(listOf("Unknown (AAC Stereo), track 1", "Unknown (AAC Stereo), track 2"), audioTrackLabels(tracks))
    }

    @Test
    fun `a track with no details keeps its label`() {
        val tracks = listOf(track(0, "Unknown", codec = "", language = null, channels = null), track(1, "English"))
        assertEquals(listOf("Unknown", "English"), audioTrackLabels(tracks))
    }
}
