package io.github.flexarr.tv

import android.annotation.SuppressLint
import android.content.ContentUris
import android.content.Context
import android.net.Uri
import androidx.tvprovider.media.tv.TvContractCompat
import androidx.tvprovider.media.tv.WatchNextProgram
import androidx.work.Constraints
import androidx.work.CoroutineWorker
import androidx.work.ExistingPeriodicWorkPolicy
import androidx.work.NetworkType
import androidx.work.PeriodicWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.WorkerParameters
import io.github.flexarr.AppLink
import io.github.flexarr.appModel
import io.github.flexarr.core.FlexarrClient
import io.github.flexarr.core.FlexarrException
import io.github.flexarr.core.ItemSummary
import io.github.flexarr.core.MediaKind
import io.github.flexarr.core.Profile
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
        /** [lastEngagement] is when it was really played, not a time made up to keep the server's order. */
        val played: Boolean = false,
        /** An album's artist (Google TV shows an episode title only for episodes). */
        val author: String? = null,
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
        // its own or, if that's later, just before the card before. Counted from the newest play rather than now, so a
        // card's time stays the same from one refresh to the next and the row isn't rewritten for nothing.
        var previous = (watching.mapNotNull(::playedAt).maxOrNull() ?: now) + 2 * SECOND
        val videos = watching.mapNotNull { item ->
            val played = playedAt(item)
            val engaged = minOf(played ?: Long.MAX_VALUE, previous - SECOND).also { previous = it }
            val position = item.userData.positionSeconds.takeIf { it > 0 && !item.userData.watched }
            val duration = item.durationSeconds?.takeIf { it > 0 } ?: item.runtimeMinutes?.takeIf { it > 0 }?.let { it * 60.0 }
            when (item.kind) {
                MediaKind.Movie -> position?.let {
                    Card(
                        item.id, TvContractCompat.WatchNextPrograms.WATCH_NEXT_TYPE_CONTINUE, TvContractCompat.PreviewPrograms.TYPE_MOVIE, item.title,
                        durationMillis = duration?.let(::millis), positionMillis = millis(it), lastEngagement = engaged, played = played == engaged,
                        image = image(item),
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
                    lastEngagement = engaged, played = played == engaged, image = image(item), link = AppLink.Play(item.id),
                )
                else -> null
            }
        }
        val oldest = videos.minOfOrNull { it.lastEngagement } ?: now
        val music = albums.filter { it.kind == MediaKind.Album }.take(MAX_ALBUMS).mapIndexed { index, album ->
            Card(
                album.id, TvContractCompat.WatchNextPrograms.WATCH_NEXT_TYPE_CONTINUE, TvContractCompat.PreviewPrograms.TYPE_ALBUM, album.title,
                author = album.seriesTitle,
                lastEngagement = playedAt(album) ?: (oldest - (index + 1) * MINUTE), played = playedAt(album) != null,
                image = image(album), link = AppLink.Album(album.id),
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
            "watch-next", ExistingPeriodicWorkPolicy.UPDATE,
            PeriodicWorkRequestBuilder<Worker>(6, TimeUnit.HOURS)
                // Offline it could only fail.
                .setConstraints(Constraints.Builder().setRequiredNetworkType(NetworkType.CONNECTED).build())
                .build(),
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
        try {
            // Asked every time, never the app's own list: a PIN added elsewhere has to empty the row, the 6-hourly refresh's included.
            val watching = watching(client.profiles(), credentials.profileToken) ?: return write(context, emptyList())
            val videos = client.continueWatching(take = MAX_VIDEOS)
            val cards = cards(videos, recentAlbums(client, watching.id), System.currentTimeMillis()) { item -> picture(item, client) }
            write(context, cards, watching.id)
        } catch (e: FlexarrException) {
            // Offline keeps the row; refused, its titles are no longer for this TV.
            if (!isRefused(e)) throw e
            write(context, emptyList())
        }
    }

    /**
     * Whose titles the row shows, of [profiles] as the server just listed them for this sign-in; null keeps it empty.
     * [profileToken] is the choice this TV holds ("{profile id}.{signature}"), and null before anyone's chosen.
     */
    internal fun watching(profiles: List<Profile>, profileToken: String?): Profile? {
        // Before anyone's chosen the server acts as the main profile, whose titles aren't for whoever's at "Who's
        // watching?" in a house with several.
        if (profileToken == null && profiles.size > 1) return null
        val current = profiles.firstOrNull { it.isCurrent } ?: return null
        // Adding a PIN retires a token, and on a server-key or home-network sign-in a retired one answers as the main
        // profile: so it's only trusted to be who it names.
        if (profileToken != null && profileToken.substringBeforeLast('.') != current.id) return null
        // Its PIN, or the admin PIN that choosing the main profile can take, guards it: so the row stays empty.
        return current.takeUnless { it.takesPin }
    }

    /**
     * The server answered and won't have this sign-in or profile, rather than couldn't be asked. A 403 only when it says
     * why: a bare one can be a reverse proxy during an outage, which is no reason to forget the row and the cards taken
     * out of it.
     */
    internal fun isRefused(error: FlexarrException): Boolean = when (error) {
        is FlexarrException.Unauthorized, is FlexarrException.ParentPinNeeded, is FlexarrException.OwnPinWrong, is FlexarrException.KidsLocked -> true
        is FlexarrException.Http -> error.status == 401 || error.guestRemoved || error.chooseProfile
        else -> false
    }

    /** The last answer for recently played albums: whose they were, when they were asked for, and them. */
    private var albums: Triple<String, Long, List<ItemSummary>>? = null

    /**
     * [profile]'s recently played albums. Only the whole home page has them, so they're asked for at most every few
     * minutes rather than with every change in what's watched.
     */
    private suspend fun recentAlbums(client: FlexarrClient, profile: String): List<ItemSummary> {
        val now = System.currentTimeMillis()
        albums?.let { (owner, at, list) -> if (owner == profile && now - at < ALBUMS_FRESH) return list }
        val list = runCatching { client.home().rows.firstOrNull { it.id == RECENT_MUSIC }?.items.orEmpty() }.getOrNull() ?: return emptyList()
        albums = Triple(profile, now, list)
        return list
    }

    /**
     * Makes this app's programs in the row match [cards], [owner]'s (a profile id): changed ones updated, new ones
     * added, the rest removed. One someone took out of the row (no longer browsable) is left out until it leaves
     * Continue Watching, so an older server that can't forget it ([WatchNextReceiver]) doesn't put it straight back,
     * or until it's played again (an album, which never leaves).
     */
    @SuppressLint("RestrictedApi")
    private fun write(context: Context, cards: List<Card>, owner: String = "") {
        val resolver = context.contentResolver
        val existing = mutableMapOf<String, WatchNextProgram>()
        resolver.query(TvContractCompat.WatchNextPrograms.CONTENT_URI, WatchNextProgram.PROJECTION, null, null, null)?.use { cursor ->
            while (cursor.moveToNext()) {
                val program = WatchNextProgram.fromCursor(cursor)
                program.internalProviderId?.let { existing[it] = program }
            }
        }
        val wanted = cards.associateBy { key(owner, it.id) }
        var removed = 0
        for ((key, row) in existing.entries.toList()) {
            val card = wanted[key]
            // Taken out of the row, and played again since: it comes back as a new card.
            val playedAgain = card != null && !row.isBrowsable && card.played && card.lastEngagement > row.lastEngagementTimeUtcMillis
            if (card == null || playedAgain) {
                resolver.delete(TvContractCompat.buildWatchNextProgramUri(row.id), null, null)
                removed++
                existing.remove(key)
            }
        }
        var added = 0
        var updated = 0
        for ((key, card) in wanted) {
            val row = existing[key]
            if (row != null && !row.isBrowsable) continue
            val values = program(context, card, key).toContentValues()
            if (row != null) {
                // Only what's changed: each write has the launcher look at the row again.
                val stored = row.toContentValues()
                if (values.keySet().all { values.get(it)?.toString() == stored.get(it)?.toString() }) continue
                resolver.update(TvContractCompat.buildWatchNextProgramUri(row.id), values, null, null)
                updated++
            } else if (resolver.insert(TvContractCompat.WatchNextPrograms.CONTENT_URI, values) != null) {
                added++
            }
        }
        log.info("Watch Next: $added added, $updated updated, $removed removed")
    }

    /** A row's own id: the profile it's for and the title, so a removal acts only for the profile whose card it was. */
    private fun key(owner: String, id: String) = "$owner/$id"


    /**
     * A card as a row. Position, length and art are always set, to 0 or null when there's none: an update leaves alone
     * any column it isn't given, so an episode that's gone back to Next would keep its progress bar.
     */
    @SuppressLint("RestrictedApi")
    internal fun program(context: Context, card: Card, key: String): WatchNextProgram = WatchNextProgram.Builder()
        .setWatchNextType(card.watchNextType)
        .setType(card.programType)
        .setTitle(card.title)
        .setEpisodeTitle(card.episodeTitle)
        .apply {
            card.season?.let { setSeasonNumber(it) }
            card.episode?.let { setEpisodeNumber(it) }
            card.author?.let { setAuthor(it) }
            card.image?.let { setPosterArtAspectRatio(it.aspect) }
        }
        .setDurationMillis(card.durationMillis ?: 0)
        .setLastPlaybackPositionMillis(card.positionMillis ?: 0)
        .setPosterArtUri(card.image?.let { Uri.parse(it.url) })
        .setLastEngagementTimeUtcMillis(card.lastEngagement)
        .setInternalProviderId(key)
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

    /** A card someone took out of the row: the profile it was for, the title, and whether it's an album. */
    data class Removed(val owner: String, val id: String, val isAlbum: Boolean)

    /**
     * The card a [TvContractCompat.ACTION_WATCH_NEXT_PROGRAM_BROWSABLE_DISABLED] broadcast names: only once the system
     * has really taken it out of the row (no longer browsable), which no other app can do, so a made-up broadcast
     * changes nothing.
     */
    @SuppressLint("RestrictedApi")
    internal fun removed(context: Context, programId: Long): Removed? =
        context.contentResolver.query(ContentUris.withAppendedId(TvContractCompat.WatchNextPrograms.CONTENT_URI, programId), WatchNextProgram.PROJECTION, null, null, null)
            ?.use { cursor ->
                if (!cursor.moveToFirst()) return@use null
                val program = WatchNextProgram.fromCursor(cursor).takeIf { !it.isBrowsable } ?: return@use null
                val key = program.internalProviderId ?: return@use null
                Removed(key.substringBefore('/', ""), key.substringAfter('/'), program.type == TvContractCompat.PreviewPrograms.TYPE_ALBUM)
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
    private const val ALBUMS_FRESH = 5 * MINUTE
}
