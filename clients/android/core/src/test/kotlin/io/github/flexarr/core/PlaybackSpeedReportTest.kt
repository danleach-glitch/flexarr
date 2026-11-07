package io.github.flexarr.core

import org.junit.Test
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue

class PlaybackSpeedReportTest {
    private fun tells(
        kind: MediaKind = MediaKind.Movie,
        rate: Float = 1.5f,
        known: Double = 1.0,
        converted: Boolean = true,
        controls: Boolean = true,
        offline: Boolean = false,
        history: Boolean = true,
    ) = PlaybackSpeedReport.shouldTell(kind, rate, known, converted, controls, offline, history)

    @Test
    fun aConvertedFilmTellsTheServerItsSpeed() {
        assertTrue(tells())
        assertTrue(tells(rate = 2f))
        // Back to normal from a faster speed: the server slows down again.
        assertTrue(tells(rate = 1f, known = 2.0))
        // Telling without history keeps nothing: a film's speed isn't kept.
        assertTrue(tells(history = false))
    }

    @Test
    fun aFilmPlayedAsItIsOrDownloadedDoesNot() {
        assertFalse(tells(converted = false))
        assertFalse(tells(offline = true))
    }

    @Test
    fun nothingIsSentWhenTheServerAlreadyHasIt() {
        assertFalse(tells(rate = 1f, known = 1.0))
        assertFalse(tells(kind = MediaKind.Episode, rate = 1.5f, known = 1.5))
    }

    @Test
    fun anEpisodeKeepsItsSpeedForTheShowAsBefore() {
        assertTrue(tells(kind = MediaKind.Episode, converted = false))
        assertTrue(tells(kind = MediaKind.Episode, converted = true))
        assertFalse(tells(kind = MediaKind.Episode, history = false))
    }

    @Test
    fun serversWithoutPlayerControlsAreNeverAsked() {
        assertFalse(tells(controls = false))
        assertFalse(tells(kind = MediaKind.Episode, controls = false))
    }

    @Test
    fun titlesThatKeepTheirOwnSpeedAreLeftAlone() {
        assertFalse(tells(kind = MediaKind.Audiobook))
        assertFalse(tells(kind = MediaKind.Series))
        assertFalse(tells(kind = MediaKind.Season))
    }
}
