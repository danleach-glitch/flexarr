package io.github.flexarr.core

import io.github.flexarr.core.BookActions.Button.DOWNLOAD
import io.github.flexarr.core.BookActions.Button.FINISHED
import io.github.flexarr.core.BookActions.Button.MORE
import io.github.flexarr.core.BookActions.Button.PLAY
import io.github.flexarr.core.BookActions.Button.START_OVER
import io.github.flexarr.core.BookActions.Input
import io.github.flexarr.core.BookActions.Platform.PHONE
import io.github.flexarr.core.BookActions.Platform.TV
import org.junit.Test
import org.junit.Assert.assertEquals

class BookActionsTest {
    @Test fun aNewBookPlaysAndCanBeFinishedAndDownloaded() =
        assertEquals(listOf(PLAY, FINISHED, DOWNLOAD), BookActions.of(Input(hasProgress = false, finished = false, isKids = false, platform = PHONE, canDownload = true)))

    @Test fun partWayThroughOffersStartOverSecond() =
        assertEquals(listOf(PLAY, START_OVER, FINISHED, DOWNLOAD), BookActions.of(Input(hasProgress = true, finished = false, isKids = false, platform = PHONE, canDownload = true)))

    @Test fun aFinishedBookCanStartOver() =
        assertEquals(listOf(PLAY, START_OVER, FINISHED), BookActions.of(Input(hasProgress = false, finished = true, isKids = false, platform = TV)))

    @Test fun theTvNeverDownloads() =
        assertEquals(listOf(PLAY, FINISHED), BookActions.of(Input(hasProgress = false, finished = false, isKids = false, platform = TV, canDownload = true)))

    @Test fun childrenHaveNoFinishedButton() =
        assertEquals(listOf(PLAY, START_OVER), BookActions.of(Input(hasProgress = true, finished = false, isKids = true, platform = PHONE)))

    @Test fun moreComesLast() =
        assertEquals(MORE, BookActions.of(Input(hasProgress = false, finished = false, isKids = false, platform = PHONE, canDownload = true, hasMenu = true)).last())
}
