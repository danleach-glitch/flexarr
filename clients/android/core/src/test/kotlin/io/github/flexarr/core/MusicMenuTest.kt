package io.github.flexarr.core

import io.github.flexarr.core.MusicActions.Platform.PHONE
import io.github.flexarr.core.MusicActions.Platform.TV
import io.github.flexarr.core.MusicMenu.Item.ADD_TO_PLAYLIST
import io.github.flexarr.core.MusicMenu.Item.ADD_TO_QUEUE
import io.github.flexarr.core.MusicMenu.Item.DOWNLOAD
import io.github.flexarr.core.MusicMenu.Item.GO_TO_ARTIST
import io.github.flexarr.core.MusicMenu.Item.PLAY
import io.github.flexarr.core.MusicMenu.Item.PLAY_NEXT
import io.github.flexarr.core.MusicMenu.Item.SHUFFLE
import io.github.flexarr.core.MusicMenu.Place.CARD
import io.github.flexarr.core.MusicMenu.Place.ROW
import io.github.flexarr.core.MusicMenu.Subject
import org.junit.Test
import org.junit.Assert.assertEquals

class MusicMenuTest {
    @Test
    fun albumCardOnAPhoneHasEverything() =
        assertEquals(listOf(PLAY, SHUFFLE, PLAY_NEXT, ADD_TO_QUEUE, ADD_TO_PLAYLIST, GO_TO_ARTIST, DOWNLOAD), MusicMenu.of(Subject.ALBUM, CARD, PHONE, hasArtist = true, canDownload = true))

    @Test
    fun theTvLeavesOutPlaylistsAndDownloads() =
        assertEquals(listOf(PLAY, SHUFFLE, PLAY_NEXT, ADD_TO_QUEUE, GO_TO_ARTIST), MusicMenu.of(Subject.ALBUM, CARD, TV, hasArtist = true, canDownload = true))

    @Test
    fun aTrackRowDoesntShuffle() =
        assertEquals(listOf(PLAY, PLAY_NEXT, ADD_TO_QUEUE, ADD_TO_PLAYLIST, GO_TO_ARTIST), MusicMenu.of(Subject.TRACK, ROW, PHONE, hasArtist = true))

    @Test
    fun anArtistHasNoArtistToGoTo() =
        assertEquals(listOf(PLAY, SHUFFLE, PLAY_NEXT, ADD_TO_QUEUE, ADD_TO_PLAYLIST), MusicMenu.of(Subject.ARTIST, CARD, PHONE, hasArtist = true))

    @Test
    fun aPlaylistIsntAddedToAnother() =
        assertEquals(listOf(PLAY, SHUFFLE, PLAY_NEXT, ADD_TO_QUEUE, DOWNLOAD), MusicMenu.of(Subject.PLAYLIST, CARD, PHONE, canDownload = true))

    @Test
    fun pagesQueueFromTheirMenu() {
        val phone = MusicActions.Input(MusicActions.Kind.ALBUM, hasMusic = true, canRequest = false, platform = PHONE, canQueue = true)
        assertEquals(listOf(MusicActions.MenuItem.PLAY_NEXT, MusicActions.MenuItem.ADD_TO_QUEUE, MusicActions.MenuItem.ADD_TO_PLAYLIST), MusicActions.menu(phone))
        assertEquals(MusicActions.Button.MORE, MusicActions.of(phone).last())
        val tv = phone.copy(platform = TV)
        assertEquals(listOf(MusicActions.MenuItem.PLAY_NEXT, MusicActions.MenuItem.ADD_TO_QUEUE), MusicActions.menu(tv))
        assertEquals(false, MusicActions.Button.MORE in MusicActions.of(phone.copy(hasMusic = false)))
    }
}
