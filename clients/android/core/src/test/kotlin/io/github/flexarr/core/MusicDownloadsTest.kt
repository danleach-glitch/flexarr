package io.github.flexarr.core

import kotlinx.serialization.builtins.ListSerializer
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class MusicDownloadsTest {
    private fun track(id: String, phase: LocalPhase, progress: Double = 0.0, failure: String? = null) = LocalDownload(
        id = "d-$id",
        profileId = "p",
        quality = DownloadQuality.Original,
        item = ItemSummary(id = id, libraryId = "music", kind = MediaKind.Track, title = id, childCount = 0, durationSeconds = 200.0, artwork = Artwork(), userData = UserData(0.0, false, 0)),
        phase = phase,
        progress = progress,
        failure = failure,
        fileName = if (phase == LocalPhase.Downloaded) "d-$id.flac" else null,
    )

    private fun collection(id: String, tracks: List<String>, profileId: String = "p") =
        LocalCollection(id = id, profileId = profileId, kind = CollectionKind.Album, title = id, trackIds = tracks)

    @Test
    fun allHereIsDownloaded() {
        val status = CollectionRules.status(listOf(track("a", LocalPhase.Downloaded), track("b", LocalPhase.Downloaded)), queueing = false)
        assertEquals(CollectionStatus.Downloaded, status)
    }

    @Test
    fun someHereIsDownloadingByHowMuchOfTheWholeHasCome() {
        val tracks = listOf(track("a", LocalPhase.Downloaded), track("b", LocalPhase.Downloading, 0.5), track("c", LocalPhase.Preparing), track("d", LocalPhase.Preparing))
        assertEquals(CollectionStatus.Downloading(1.5 / 4), CollectionRules.status(tracks, queueing = false))
        // A transfer under way counts as far as it's really got.
        assertEquals(CollectionStatus.Downloading(1.9 / 4), CollectionRules.status(tracks, queueing = false) { if (it.id == "d-b") 0.9 else it.progress })
    }

    @Test
    fun noneComingYetIsPreparing() {
        assertEquals(CollectionStatus.Preparing, CollectionRules.status(listOf(track("a", LocalPhase.Preparing, 0.4)), queueing = false))
        assertEquals(CollectionStatus.Preparing, CollectionRules.status(emptyList(), queueing = true))
    }

    @Test
    fun oneFailedFailsTheWhole() {
        val status = CollectionRules.status(listOf(track("a", LocalPhase.Downloaded), track("b", LocalPhase.Failed, failure = "No room")), queueing = false)
        assertEquals(CollectionStatus.Failed("No room"), status)
    }

    @Test
    fun aTrackNeverAskedForFailsOnlyOnceTheAskingHasStopped() {
        val tracks = listOf(track("a", LocalPhase.Downloaded), null)
        assertEquals(CollectionStatus.Downloading(0.5), CollectionRules.status(tracks, queueing = true))
        assertTrue(CollectionRules.status(tracks, queueing = false) is CollectionStatus.Failed)
    }

    @Test
    fun removingKeepsTracksAnotherCollectionHolds() {
        val album = collection("album", listOf("a", "b", "c"))
        val playlist = collection("playlist", listOf("b", "x"))
        val someoneElses = collection("theirs", listOf("c"), profileId = "q")
        assertEquals(listOf("a", "c"), CollectionRules.tracksToDelete(album, listOf(album, playlist, someoneElses)))
    }

    @Test
    fun factsSayHowManyAreHere() {
        val album = collection("album", listOf("a", "b", "c"))
        val here = listOf(track("a", LocalPhase.Downloaded).item, track("b", LocalPhase.Downloaded).item)
        assertEquals(listOf("3 tracks", "7m", "2 here"), CollectionRules.facts(album, here))
        assertEquals(listOf("1 track", "3m", "Downloaded"), CollectionRules.facts(collection("one", listOf("a")), here.take(1)))
    }

    @Test
    fun coverNamesStayInTheFolder() {
        assertEquals("cover-abc-1.jpg", CollectionRules.coverName("abc-1"))
        assertEquals("playlist-___x.jpg", CollectionRules.playlistCoverName("../x"))
    }

    @Test
    fun aSavedListFromAnOlderAppStillReads() {
        val text = """[{"id":"album","profileId":"p","kind":"Album","title":"Hounds of Love"}]"""
        val list = FlexarrClient.json.decodeFromString(ListSerializer(LocalCollection.serializer()), text)
        assertEquals(1, list.size)
        assertEquals(emptyList<String>(), list[0].trackIds)
        assertNull(list[0].album)
        val again = FlexarrClient.json.encodeToString(ListSerializer(LocalCollection.serializer()), list)
        assertEquals(list, FlexarrClient.json.decodeFromString(ListSerializer(LocalCollection.serializer()), again))
    }
}
