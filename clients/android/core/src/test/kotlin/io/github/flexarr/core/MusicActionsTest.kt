package io.github.flexarr.core

import io.github.flexarr.core.MusicActions.Button.DOWNLOAD
import io.github.flexarr.core.MusicActions.Button.FOLLOW
import io.github.flexarr.core.MusicActions.Button.MORE
import io.github.flexarr.core.MusicActions.Button.PLAY
import io.github.flexarr.core.MusicActions.Button.RADIO
import io.github.flexarr.core.MusicActions.Button.REQUEST
import io.github.flexarr.core.MusicActions.Button.SHUFFLE
import io.github.flexarr.core.MusicActions.Kind
import io.github.flexarr.core.MusicActions.Platform
import org.junit.Assert.assertEquals
import org.junit.Test

/** A music artist's and album's page actions and their order. */
class MusicActionsTest {
    private fun buttons(
        kind: Kind = Kind.ARTIST,
        hasMusic: Boolean = true,
        canRequest: Boolean = true,
        platform: Platform = Platform.PHONE,
        canRadio: Boolean = false,
        canDownload: Boolean = false,
        hasMenu: Boolean = false,
    ) = MusicActions.of(MusicActions.Input(kind, hasMusic, canRequest, platform, canRadio, canDownload, hasMenu))

    @Test
    fun `everything an artist page can have, in order`() {
        assertEquals(listOf(PLAY, SHUFFLE, RADIO, FOLLOW, MORE), buttons(canRadio = true, hasMenu = true))
    }

    @Test
    fun `everything an album page can have, in order`() {
        assertEquals(listOf(PLAY, SHUFFLE, DOWNLOAD, REQUEST, MORE), buttons(kind = Kind.ALBUM, canDownload = true, hasMenu = true))
        // Radio is an artist's.
        assertEquals(listOf(PLAY, SHUFFLE, REQUEST), buttons(kind = Kind.ALBUM, canRadio = true))
    }

    @Test
    fun `a placeholder artist only follows`() {
        assertEquals(listOf(FOLLOW), buttons(hasMusic = false, canRadio = true))
    }

    @Test
    fun `an album from outside the library only requests`() {
        assertEquals(listOf(REQUEST), buttons(kind = Kind.ALBUM, hasMusic = false, canDownload = true))
    }

    @Test
    fun `without music requests there's nothing to follow or request`() {
        assertEquals(listOf(PLAY, SHUFFLE), buttons(canRequest = false))
        assertEquals(emptyList<MusicActions.Button>(), buttons(hasMusic = false, canRequest = false))
    }

    @Test
    fun `the TV keeps no downloads`() {
        assertEquals(listOf(PLAY, SHUFFLE, REQUEST), buttons(kind = Kind.ALBUM, platform = Platform.TV, canDownload = true))
    }
}
