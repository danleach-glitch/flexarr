package io.github.flexarr.core

import io.github.flexarr.core.i18n.t

// A show's or a season's episodes downloaded together, as the iPhone app's: each episode is a download of its own, and
// the show's Download button and a season's link say where they stand taken together.

/** Where a group of episodes (a whole show, or one season) stands on this phone. */
sealed interface EpisodeGroupState {
    /** None of them on their way, and not all here: Download fetches the [missing] ones (some may be here already). */
    data class NotDownloaded(val missing: Int) : EpisodeGroupState
    /** Some on their way: [done] of the [total] are here, [fraction] of the whole come (0 to 1). */
    data class Downloading(val done: Int, val total: Int, val fraction: Double) : EpisodeGroupState
    /** Every episode is here. */
    data object Downloaded : EpisodeGroupState
    /** One failed ([message] is the first one's reason). */
    data class Failed(val message: String) : EpisodeGroupState
}

object EpisodeGroupRules {
    /**
     * What a group's [episodes] (each one's download, null for one never asked for) add up to, as on iPhone: all here is
     * downloaded, however they came. Otherwise only a group someone [started] (or is [queueing]: its episodes still being
     * asked of the server, or the list of them fetched) says more, so downloading one episode doesn't turn its show or
     * season into "Downloading": a failure first (tapping tries again); then, with anything on its way, how many of the
     * whole group are here and how far it has got on average. [known] is false while only the downloads are known and
     * not the full list of episodes: then it can't be all here.
     */
    fun state(
        episodes: List<LocalDownload?>,
        started: Boolean = true,
        queueing: Boolean = false,
        known: Boolean = true,
        progress: (LocalDownload) -> Double = { it.progress },
    ): EpisodeGroupState {
        val done = episodes.count { it?.isDownloaded == true }
        if (known && episodes.isNotEmpty() && done == episodes.size) return EpisodeGroupState.Downloaded
        if (!started && !queueing) return EpisodeGroupState.NotDownloaded(episodes.size - done)
        episodes.firstOrNull { it?.phase == LocalPhase.Failed }?.let { return EpisodeGroupState.Failed(it.failure ?: t("Couldn’t download this")) }
        if (queueing || episodes.any { it?.isPending == true }) {
            val fractions = episodes.map {
                when {
                    it == null -> 0.0
                    it.isDownloaded -> 1.0
                    it.phase == LocalPhase.Downloading -> progress(it).coerceIn(0.0, 1.0)
                    else -> 0.0
                }
            }
            return EpisodeGroupState.Downloading(done, episodes.size, if (fractions.isEmpty()) 0.0 else fractions.average())
        }
        return EpisodeGroupState.NotDownloaded(episodes.size - done)
    }

    /** The episodes Download asks for: those not here or on their way (one that failed is asked for again). */
    fun toQueue(episodes: List<ItemSummary>, entry: (ItemSummary) -> LocalDownload?): List<ItemSummary> =
        episodes.filter { episode -> entry(episode).let { it == null || it.phase == LocalPhase.Failed } }

    /**
     * Of [downloads], the one of [episode] made through a linked copy of its show or season ([copies], their ids): the
     * same episode of another copy, as the server hands back the download it has through any copy of a title.
     */
    fun throughCopies(episode: ItemSummary, downloads: List<LocalDownload>, copies: Set<String>): LocalDownload? =
        if (copies.size < 2) null
        else downloads.firstOrNull { held ->
            held.item.kind == MediaKind.Episode && (held.item.seriesId in copies || held.item.parentId in copies) && held.item.sameEpisode(episode)
        }

    /** What Stop downloads deletes: everything not yet here (on its way, or failed); what's here stays. */
    fun toStop(downloads: List<LocalDownload>): List<LocalDownload> = downloads.filter { !it.isDownloaded }

    /** What Try again asks for again. */
    fun failed(downloads: List<LocalDownload>): List<LocalDownload> = downloads.filter { it.phase == LocalPhase.Failed }

    /**
     * The episodes whose sizes are asked of the server for a group's estimate: every one, or [most] spread evenly across
     * a long show, so a show of hundreds of episodes doesn't ask hundreds of times.
     */
    fun sizeSample(episodes: List<ItemSummary>, most: Int = SIZE_SAMPLE): List<ItemSummary> =
        if (episodes.size <= most) episodes else List(most) { episodes[it * episodes.size / most] }

    /** About how big [count] episodes come to, from the sizes the server gave for those asked about ([known]); null with none. */
    fun estimatedBytes(known: List<Long>, count: Int): Long? =
        if (known.isEmpty()) null else (known.sum().toDouble() / known.size * count).toLong()

    /** Whether [bytes] would leave the phone short of room, with [freeBytes] free (and a little kept spare, as a transfer does). */
    fun outgrows(bytes: Long, freeBytes: Long): Boolean = bytes + SPARE_BYTES > freeBytes

    private const val SIZE_SAMPLE = 40
    private const val SPARE_BYTES = 200L * 1024 * 1024

    /**
     * The started groups Stop or Remove on [episodes] forgets: every group holding one of them, its show and its season
     * (a show's Stop forgets its seasons; a season's forgets its show).
     */
    fun groupsHolding(episodes: List<ItemSummary>): Set<String> = episodes.flatMap { listOfNotNull(it.seriesId, it.parentId) }.toSet()
}

/** A show's episodes on the Downloads screen: its name, and its downloads by season then episode. */
data class DownloadedShow(
    /** The show's id, or its name when the server didn't say (servers before seriesId). */
    val key: String,
    val title: String,
    val episodes: List<LocalDownload>,
) {
    /** Its episodes under each season's number (null for episodes with none, shown as specials), in order. */
    val seasons: List<Pair<Int?, List<LocalDownload>>>
        get() = episodes.groupBy { it.item.seasonNumber }.toList().sortedBy { it.first ?: 0 }
}

object DownloadedShows {
    /**
     * Downloaded (or downloading) episodes, one entry per show: grouped by the show's id, or its name when there's no id;
     * episodes in season then episode order, shows by name.
     */
    fun group(episodes: List<LocalDownload>, untitled: String): List<DownloadedShow> =
        episodes.filter { it.item.kind == MediaKind.Episode }
            .groupBy { it.item.seriesId ?: it.item.seriesTitle ?: untitled }
            .map { (key, downloads) ->
                DownloadedShow(
                    key = key,
                    title = downloads.firstNotNullOfOrNull { it.item.seriesTitle } ?: untitled,
                    episodes = downloads.sortedWith(compareBy({ it.item.seasonNumber ?: 0 }, { it.item.episodeNumber ?: 0 })),
                )
            }
            .sortedBy { it.title.lowercase() }
}
