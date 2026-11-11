package io.github.flexarr.core

import org.junit.Assert.assertEquals
import org.junit.Test

class EpisodeDownloadsTest {
    private fun episode(id: String, season: Int? = 1, number: Int = 1, seriesId: String? = "show", seriesTitle: String? = "Show") = ItemSummary(
        id = id,
        libraryId = "tv",
        kind = MediaKind.Episode,
        title = id,
        seasonNumber = season,
        episodeNumber = number,
        childCount = 0,
        artwork = Artwork(),
        userData = UserData(0.0, false, 0),
        seriesId = seriesId,
        seriesTitle = seriesTitle,
    )

    private fun download(item: ItemSummary, phase: LocalPhase, progress: Double = 0.0, failure: String? = null) = LocalDownload(
        id = "d-${item.id}",
        profileId = "p",
        quality = DownloadQuality.Medium,
        item = item,
        phase = phase,
        progress = progress,
        failure = failure,
        fileName = if (phase == LocalPhase.Downloaded) "d-${item.id}.mp4" else null,
    )

    private fun download(id: String, phase: LocalPhase, progress: Double = 0.0, failure: String? = null) = download(episode(id), phase, progress, failure)

    @Test
    fun nothingAskedForIsNotDownloadedWithEveryEpisodeMissing() {
        assertEquals(EpisodeGroupState.NotDownloaded(3), EpisodeGroupRules.state(listOf(null, null, null)))
    }

    @Test
    fun someHereAndTheRestNotStartedIsNotDownloadedCountingOnlyTheMissing() {
        assertEquals(EpisodeGroupState.NotDownloaded(2), EpisodeGroupRules.state(listOf(download("a", LocalPhase.Downloaded), null, null)))
    }

    @Test
    fun allHereIsDownloaded() {
        assertEquals(EpisodeGroupState.Downloaded, EpisodeGroupRules.state(listOf(download("a", LocalPhase.Downloaded), download("b", LocalPhase.Downloaded))))
    }

    @Test
    fun allHereIsNotDownloadedWhileTheFullListIsUnknown() {
        assertEquals(EpisodeGroupState.NotDownloaded(0), EpisodeGroupRules.state(listOf(download("a", LocalPhase.Downloaded)), known = false))
    }

    @Test
    fun anyOnTheirWayIsDownloadingOutOfTheWholeGroup() {
        val episodes = listOf(download("a", LocalPhase.Downloaded), download("b", LocalPhase.Downloading, 0.5), download("c", LocalPhase.Preparing), null)
        assertEquals(EpisodeGroupState.Downloading(1, 4, 1.5 / 4), EpisodeGroupRules.state(episodes))
        // A transfer under way counts as far as it's really got.
        assertEquals(EpisodeGroupState.Downloading(1, 4, 1.9 / 4), EpisodeGroupRules.state(episodes) { if (it.id == "d-b") 0.9 else it.progress })
    }

    @Test
    fun aFailureWinsAsOnIPhone() {
        val episodes = listOf(download("a", LocalPhase.Downloaded), download("b", LocalPhase.Failed, failure = "No room"), download("c", LocalPhase.Preparing))
        assertEquals(EpisodeGroupState.Failed("No room"), EpisodeGroupRules.state(episodes))
    }

    @Test
    fun queueingIsDownloadingBeforeAnythingIsAskedFor() {
        assertEquals(EpisodeGroupState.Downloading(0, 2, 0.0), EpisodeGroupRules.state(listOf(null, null), queueing = true))
    }

    @Test
    fun aGroupNobodyStartedSaysOnlyDownloadOrDownloaded() {
        val coming = listOf(download("a", LocalPhase.Downloading, 0.5), download("b", LocalPhase.Failed, failure = "No room"), null)
        assertEquals(EpisodeGroupState.NotDownloaded(3), EpisodeGroupRules.state(coming, started = false))
        // All here, however they came, is downloaded.
        assertEquals(EpisodeGroupState.Downloaded, EpisodeGroupRules.state(listOf(download("a", LocalPhase.Downloaded)), started = false))
        // While it's being asked for, it's on its way.
        assertEquals(EpisodeGroupState.Downloading(0, 1, 0.0), EpisodeGroupRules.state(listOf(null), started = false, queueing = true))
    }

    @Test
    fun allHereWinsOverAStartedGroupsState() {
        assertEquals(EpisodeGroupState.Downloaded, EpisodeGroupRules.state(listOf(download("a", LocalPhase.Downloaded)), queueing = true))
    }

    @Test
    fun forgetsEveryGroupHoldingTheEpisodes() {
        val episodes = listOf(episode("a").copy(parentId = "s1"), episode("b").copy(parentId = "s2"), episode("c", seriesId = null))
        assertEquals(setOf("show", "s1", "s2"), EpisodeGroupRules.groupsHolding(episodes))
    }

    @Test
    fun queuesWhatIsNeitherHereNorOnItsWayAndRetriesFailures() {
        val episodes = listOf(episode("here"), episode("coming"), episode("failed"), episode("new"))
        val downloads = listOf(download("here", LocalPhase.Downloaded), download("coming", LocalPhase.Downloading), download("failed", LocalPhase.Failed)).associateBy { it.item.id }
        assertEquals(listOf("failed", "new"), EpisodeGroupRules.toQueue(episodes) { downloads[it.id] }.map { it.id })
    }

    @Test
    fun findsAnEpisodeDownloadedThroughALinkedCopyOfTheShow() {
        // The 4K copy's S1E2 was downloaded; the 1080p copy's page asks after its own S1E2.
        val held = listOf(download(episode("4k-s1e2", number = 2, seriesId = "show-4k"), LocalPhase.Downloading))
        val wanted = episode("hd-s1e2", number = 2, seriesId = "show-hd")
        assertEquals("d-4k-s1e2", EpisodeGroupRules.throughCopies(wanted, held, setOf("show-hd", "show-4k"))?.id)
        // Another episode, a show that isn't a copy, or no linked copies at all: none.
        assertEquals(null, EpisodeGroupRules.throughCopies(episode("hd-s1e3", number = 3, seriesId = "show-hd"), held, setOf("show-hd", "show-4k")))
        assertEquals(null, EpisodeGroupRules.throughCopies(wanted, held, setOf("show-hd", "other")))
        assertEquals(null, EpisodeGroupRules.throughCopies(wanted, held, setOf("show-hd")))
    }

    @Test
    fun findsAnEpisodeDownloadedThroughALinkedCopyOfTheSeason() {
        val held = listOf(download(episode("4k-s1e2", number = 2, seriesId = "show-4k").copy(parentId = "season-4k"), LocalPhase.Downloaded))
        assertEquals("d-4k-s1e2", EpisodeGroupRules.throughCopies(episode("hd-s1e2", number = 2, seriesId = "show-hd"), held, setOf("season-hd", "season-4k"))?.id)
    }

    @Test
    fun aDownloadTheServerHandsBackIsKeptUnlessItFailedHereOrIsAnotherProfiles() {
        assertEquals(true, DownloadRules.keepsHeld(download("a", LocalPhase.Downloading), "p"))
        assertEquals(true, DownloadRules.keepsHeld(download("a", LocalPhase.Downloaded), "p"))
        assertEquals(false, DownloadRules.keepsHeld(download("a", LocalPhase.Failed), "p"))
        assertEquals(false, DownloadRules.keepsHeld(download("a", LocalPhase.Downloading), "someone else"))
        assertEquals(false, DownloadRules.keepsHeld(null, "p"))
    }

    @Test
    fun asksTheSizesOfEveryEpisodeOrAnEvenSpreadOfALongShow() {
        val few = (1..5).map { episode("e$it", number = it) }
        assertEquals(few, EpisodeGroupRules.sizeSample(few, most = 10))
        val many = (1..100).map { episode("e$it", number = it) }
        val sample = EpisodeGroupRules.sizeSample(many, most = 10)
        assertEquals(listOf("e1", "e11", "e21", "e31", "e41", "e51", "e61", "e71", "e81", "e91"), sample.map { it.id })
    }

    @Test
    fun estimatesTheWholeGroupFromTheSizesKnown() {
        assertEquals(3_000L, EpisodeGroupRules.estimatedBytes(listOf(100L, 200L), 20))
        assertEquals(null, EpisodeGroupRules.estimatedBytes(emptyList(), 20))
    }

    @Test
    fun warnsWhenTheEstimateLeavesThePhoneShort() {
        val gb = 1_000_000_000L
        assertEquals(false, EpisodeGroupRules.outgrows(10 * gb, 20 * gb))
        assertEquals(true, EpisodeGroupRules.outgrows(20 * gb, 20 * gb))
        assertEquals(true, EpisodeGroupRules.outgrows(30 * gb, 20 * gb))
    }

    @Test
    fun aTitlesEstimateIsAtTheSizeItWouldComeAt() {
        val options = listOf(
            DownloadOption(DownloadQuality.Original, 9_000, available = false),
            DownloadOption(DownloadQuality.High, 4_000, available = true),
            DownloadOption(DownloadQuality.Medium, 2_000, available = true),
        )
        assertEquals(2_000L, DownloadRules.estimatedBytes(DownloadQuality.Medium, options))
        // An Original the phone can't play comes at High.
        assertEquals(4_000L, DownloadRules.estimatedBytes(DownloadQuality.Original, options))
        assertEquals(null, DownloadRules.estimatedBytes(DownloadQuality.Small, options))
        assertEquals(null, DownloadRules.estimatedBytes(DownloadQuality.Medium, null))
    }

    @Test
    fun stoppingKeepsWhatIsHere() {
        val downloads = listOf(download("here", LocalPhase.Downloaded), download("coming", LocalPhase.Downloading), download("ready", LocalPhase.Preparing), download("failed", LocalPhase.Failed))
        assertEquals(listOf("coming", "ready", "failed"), EpisodeGroupRules.toStop(downloads).map { it.item.id })
        assertEquals(listOf("failed"), EpisodeGroupRules.failed(downloads).map { it.item.id })
    }

    @Test
    fun groupsEpisodesByShowInSeasonThenEpisodeOrder() {
        val downloads = listOf(
            download(episode("b2", season = 2, number = 1, seriesId = "b", seriesTitle = "Bravo"), LocalPhase.Downloaded),
            download(episode("b1", season = 1, number = 3, seriesId = "b", seriesTitle = "Bravo"), LocalPhase.Downloaded),
            download(episode("a1", seriesId = "a", seriesTitle = "alpha"), LocalPhase.Downloading),
            // No id from an older server: grouped by name.
            download(episode("c1", seriesId = null, seriesTitle = "Charlie"), LocalPhase.Downloaded),
            download(episode("c2", number = 2, seriesId = null, seriesTitle = "Charlie"), LocalPhase.Downloaded),
            // No season number: with the specials, first.
            download(episode("b0", season = null, number = 1, seriesId = "b", seriesTitle = "Bravo"), LocalPhase.Downloaded),
        )
        val shows = DownloadedShows.group(downloads, untitled = "Episodes")
        assertEquals(listOf("alpha", "Bravo", "Charlie"), shows.map { it.title })
        assertEquals(listOf("a", "b", "Charlie"), shows.map { it.key })
        assertEquals(listOf("b0", "b1", "b2"), shows[1].episodes.map { it.item.id })
        assertEquals(listOf(null, 1, 2), shows[1].seasons.map { it.first })
        assertEquals(listOf("c1", "c2"), shows[2].episodes.map { it.item.id })
    }

    @Test
    fun `a batch gives up after three requests in a row can't reach the server, not after refusals`() {
        val unreachable = FlexarrException.Unreachable(java.io.IOException("timeout"))
        val refused = FlexarrException.Http(404, "Gone")

        val refusals = EpisodeBatchFailures()
        repeat(10) { assertEquals(false, refusals.failed(refused, "x")) }
        assertEquals("Gone", refusals.message)

        val broken = EpisodeBatchFailures()
        assertEquals(false, broken.failed(unreachable, "x"))
        assertEquals(false, broken.failed(unreachable, "x"))
        broken.succeeded()
        assertEquals(false, broken.failed(unreachable, "x"))
        assertEquals(false, broken.failed(FlexarrException.Http(503, null), "x"))
        assertEquals(true, broken.failed(unreachable, "x"))
        assertEquals(true, broken.gaveUp)

        val mixed = EpisodeBatchFailures()
        mixed.failed(unreachable, "x")
        mixed.failed(unreachable, "x")
        mixed.failed(refused, "x")
        assertEquals(false, mixed.failed(unreachable, "x"))
    }
}
