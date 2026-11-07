package io.github.flexarr.tv

import android.annotation.SuppressLint
import android.content.ContentUris
import android.content.Context
import android.net.Uri
import androidx.tvprovider.media.tv.TvContractCompat
import androidx.tvprovider.media.tv.WatchNextProgram
import androidx.work.CoroutineWorker
import androidx.work.ExistingPeriodicWorkPolicy
import androidx.work.PeriodicWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.WorkerParameters
import io.github.flexarr.AppLink
import io.github.flexarr.appModel
import io.github.flexarr.core.FlexarrClient
import io.github.flexarr.core.ItemSummary
import io.github.flexarr.core.MediaKind
import io.github.flexarr.data.CredentialStore
import io.github.flexarr.isTelevision
import io.github.flexarr.logging.FlexarrLog
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import java.time.Instant
import java.time.LocalDateTime
import java.time.ZoneOffset
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean

/**
 * Continue Watching in the Watch Next row on the Google TV (and Android TV) home screen, as the Apple TV's Top Shelf:
 * the films and episodes the last profile on this TV was part way through, a show's next episode, then its recently
 * played albums. Anyone at the TV sees the row, so it's empty for a profile with a PIN, a family guest, and once signed
 * out. Removing a card there takes the title out of Continue Watching ([WatchNextReceiver]). docs/android-tv.md, stage 4.
 */
object WatchNext {
    private val log = FlexarrLog("watch-next")
    private val writing = Mutex()

    /** A card as it goes in the row, worked out apart from the provider so the choice can be tested. */
    data class Card(
        val id: String,
        /** `TvContractCompat.WatchNextPrograms.WATCH_NEXT_TYPE_*`. */
        val watchNextType: Int,
        /** `TvContractCompat.PreviewPrograms.TYPE_*`. */
        val programType: Int,
        val title: String,
        val episodeTitle: String? = null,
        val season: Int? = null,
        val episode: Int? = null,
        val durationMillis: Int? = null,
        val positionMillis: Int? = null,
        val lastEngagement: Long,
        val image: Picture?,
        val link: AppLink,
    )

    /** A card's picture and its shape (`TvContractCompat.PreviewPrograms.ASPECT_RATIO_*`), so a cover isn't cropped to 16:9. */
    data class Picture(val url: String, val aspect: Int)

    /**
     * The row for [watching] (`home/continue`: films and episodes part way through, and shows' next episodes) and
     * [albums] (recently played), newest first. [image] is a card's picture.
     */
    fun cards(watching: List<ItemSummary>, albums: List<ItemSummary>, now: Long, image: (ItemSummary) -> Picture?): List<Card> {
        // Google TV orders the row by these times. The server's order (by when each film or show was last played)
        // holds: a show's next episode has never been played and so has no time of its own, so each card's time is
        // its own or, if that's later, just after the card before.
        var previous = now + SECOND
        val videos = watching.mapNotNull { item ->
            val engaged = minOf(playedAt(item) ?: Long.MAX_VALUE, previous - SECOND).also { previous = it }
            val position = item.userData.positionSeconds.takeIf { it > 0 && !item.userData.watched }
            val duration = item.durationSeconds?.takeIf { it > 0 } ?: item.runtimeMinutes?.takeIf { it > 0 }?.let { it * 60.0 }
            when (item.kind) {
                MediaKind.Movie -> position?.let {
                    Card(
                        item.id, TvContractCompat.WatchNextPrograms.WATCH_NEXT_TYPE_CONTINUE, TvContractCompat.PreviewPrograms.TYPE_MOVIE, item.title,
                        durationMillis = duration?.let(::millis), positionMillis = millis(it), lastEngagement = engaged, image = image(item),
                        link = AppLink.Play(item.id),
                    )
                }
                MediaKind.Episode -> Card(
                    item.id,
                    if (position != null) TvContractCompat.WatchNextPrograms.WATCH_NEXT_TYPE_CONTINUE else TvContractCompat.WatchNextPrograms.WATCH_NEXT_TYPE_NEXT,
                    TvContractCompat.PreviewPrograms.TYPE_TV_EPISODE,
                    item.seriesTitle ?: item.title,
                    episodeTitle = item.title.takeIf { item.seriesTitle != null },
                    season = item.seasonNumber, episode = item.episodeNumber,
                    durationMillis = duration?.let(::millis), positionMillis = position?.let(::millis),
                    lastEngagement = engaged, image = image(item), link = AppLink.Play(item.id),
                )
                else -> null
            }
        }
        val oldest = videos.minOfOrNull { it.lastEngagement } ?: now
        val music = albums.filter { it.kind == MediaKind.Album }.take(MAX_ALBUMS).mapIndexed { index, album ->
            Card(
                album.id, TvContractCompat.WatchNextPrograms.WATCH_NEXT_TYPE_CONTINUE, TvContractCompat.PreviewPrograms.TYPE_ALBUM, album.title,
                episodeTitle = album.seriesTitle,
                lastEngagement = playedAt(album) ?: (oldest - (index + 1) * MINUTE), image = image(album), link = AppLink.Album(album.id),
            )
        }
        return (videos.take(MAX_VIDEOS) + music).sortedByDescending { it.lastEngagement }
    }

    /** A refresh waiting its turn, which then reads everything afresh: so more asked for meanwhile add nothing. */
    private val waiting = AtomicBoolean(false)

    /**
     * Asks the server again and puts the row right (or clears it). Quietly does nothing off a TV. Asked for often
     * (whenever watch state changes), so asks while one is already waiting are dropped.
     */
    suspend fun refresh(context: Context) {
        if (!context.isTelevision || !waiting.compareAndSet(false, true)) return
        withContext(Dispatchers.IO) {
            runCatching {
                writing.withLock {
                    waiting.set(false)
                    sync(context.applicationContext)
                }
            }.onFailure { waiting.set(false); log.info("Couldn't update Watch Next", it) }
        }
    }

    /** Empties the row: signed out, or a guest. */
    suspend fun clear(context: Context) {
        if (!context.isTelevision) return
        withContext(Dispatchers.IO) { runCatching { writing.withLock { write(context.applicationContext, emptyList()) } } }
    }

    /** Keeps the row fresh while the app isn't open (progress made on another device), every 6 hours. */
    fun schedule(context: Context) {
        if (!context.isTelevision) return
        WorkManager.getInstance(context).enqueueUniquePeriodicWork(
            "watch-next", ExistingPeriodicWorkPolicy.KEEP, PeriodicWorkRequestBuilder<Worker>(6, TimeUnit.HOURS).build(),
        )
    }

    class Worker(context: Context, params: WorkerParameters) : CoroutineWorker(context, params) {
        override suspend fun doWork(): Result {
            refresh(applicationContext)
            return Result.success()
        }
    }

    private suspend fun sync(context: Context) {
        val credentials = context.appModel.credentials ?: CredentialStore(context).load()
        if (credentials == null || credentials.isGuest) return write(context, emptyList())
        val client = credentials.client()
        val profiles = client.profiles()
        // Before anyone's chosen the server acts as the main profile, whose titles aren't for whoever's at "Who's
        // watching?" in a house with several.
        if (credentials.profileToken == null && profiles.size > 1) return write(context, emptyList())
        val watching = profiles.firstOrNull { it.isCurrent } ?: profiles.firstOrNull { it.isMain }
        // Its PIN, or the admin PIN that choosing the main profile can take, guards it: so the row stays empty.
        if (watching?.takesPin == true) return write(context, emptyList())
        val videos = client.continueWatching(take = MAX_VIDEOS)
        val albums = runCatching { client.home().rows.firstOrNull { it.id == RECENT_MUSIC }?.items.orEmpty() }.getOrDefault(emptyList())
        val cards = cards(videos, albums, System.currentTimeMillis()) { item -> picture(item, client) }
        write(context, cards)
    }

    /**
     * Makes this app's programs in the row match [cards]: changed ones updated, new ones added, the rest removed. One
     * someone took out of the row (no longer browsable) is left out until it leaves Continue Watching, so an older
     * server that can't forget it ([WatchNextReceiver]) doesn't put it straight back.
     */
    @SuppressLint("RestrictedApi")
    private fun write(context: Context, cards: List<Card>) {
        val resolver = context.contentResolver
        val existing = mutableMapOf<String, Pair<Long, Boolean>>()
        resolver.query(TvContractCompat.WatchNextPrograms.CONTENT_URI, WatchNextProgram.PROJECTION, null, null, null)?.use { cursor ->
            while (cursor.moveToNext()) {
                val program = WatchNextProgram.fromCursor(cursor)
                program.internalProviderId?.let { existing[it] = program.id to program.isBrowsable }
            }
        }
        val wanted = cards.associateBy { it.id }
        for ((id, row) in existing) {
            if (id !in wanted) resolver.delete(TvContractCompat.buildWatchNextProgramUri(row.first), null, null)
        }
        var added = 0
        var kept = 0
        for (card in cards) {
            val row = existing[card.id]
            if (row != null && !row.second) continue
            val values = program(context, card).toContentValues()
            if (row != null) {
                resolver.update(TvContractCompat.buildWatchNextProgramUri(row.first), values, null, null)
                kept++
            } else if (resolver.insert(TvContractCompat.WatchNextPrograms.CONTENT_URI, values) != null) {
                added++
            }
        }
        log.info("Watch Next: $added added, $kept updated, ${existing.keys.count { it !in wanted }} removed")
    }

    @SuppressLint("RestrictedApi")
    private fun program(context: Context, card: Card): WatchNextProgram = WatchNextProgram.Builder()
        .setWatchNextType(card.watchNextType)
        .setType(card.programType)
        .setTitle(card.title)
        .setEpisodeTitle(card.episodeTitle)
        .apply {
            card.season?.let { setSeasonNumber(it) }
            card.episode?.let { setEpisodeNumber(it) }
            card.durationMillis?.let { setDurationMillis(it) }
            card.positionMillis?.let { setLastPlaybackPositionMillis(it) }
            card.image?.let { setPosterArtUri(Uri.parse(it.url)); setPosterArtAspectRatio(it.aspect) }
        }
        .setLastEngagementTimeUtcMillis(card.lastEngagement)
        .setInternalProviderId(card.id)
        .setIntent(card.link.launcherIntent(context))
        .build()

    /**
     * A card's picture: a film's or episode's wide picture (a still or the backdrop with its title), else its poster;
     * an album's square cover.
     */
    private fun picture(item: ItemSummary, client: FlexarrClient): Picture? {
        val art = item.artwork
        fun url(path: String?) = client.imageUrl(path, WIDTH)
        if (item.kind == MediaKind.Album) return url(art.poster ?: art.thumb)?.let { Picture(it, TvContractCompat.PreviewPrograms.ASPECT_RATIO_1_1) }
        val wide = client.wideImageUrl(art, WIDTH) ?: url(art.thumb)
        return wide?.let { Picture(it, TvContractCompat.PreviewPrograms.ASPECT_RATIO_16_9) }
            ?: url(art.poster)?.let { Picture(it, TvContractCompat.PreviewPrograms.ASPECT_RATIO_2_3) }
    }

    /**
     * The program a [TvContractCompat.ACTION_WATCH_NEXT_PROGRAM_BROWSABLE_DISABLED] broadcast names, as the title's
     * id: only once the system has really taken it out of the row (no longer browsable), which no other app can do,
     * so a made-up broadcast changes nothing.
     */
    @SuppressLint("RestrictedApi")
    internal fun titleOf(context: Context, programId: Long): String? =
        context.contentResolver.query(ContentUris.withAppendedId(TvContractCompat.WatchNextPrograms.CONTENT_URI, programId), WatchNextProgram.PROJECTION, null, null, null)
            ?.use { cursor ->
                if (!cursor.moveToFirst()) return@use null
                WatchNextProgram.fromCursor(cursor).takeIf { !it.isBrowsable }?.internalProviderId
            }

    /** When [item] was last played: the server's time (UTC, sometimes without its zone). */
    internal fun playedAt(item: ItemSummary): Long? {
        val text = item.userData.lastPlayedAt ?: return null
        return runCatching { Instant.parse(text).toEpochMilli() }.getOrNull()
            ?: runCatching { LocalDateTime.parse(text).toInstant(ZoneOffset.UTC).toEpochMilli() }.getOrNull()
    }

    private fun millis(seconds: Double): Int = (seconds * 1000).toLong().coerceIn(0, Int.MAX_VALUE.toLong()).toInt()

    private const val SECOND = 1_000L
    private const val MINUTE = 60_000L
    private const val MAX_VIDEOS = 12
    private const val MAX_ALBUMS = 4
    private const val WIDTH = 640
    private const val RECENT_MUSIC = "music-recent"
}
