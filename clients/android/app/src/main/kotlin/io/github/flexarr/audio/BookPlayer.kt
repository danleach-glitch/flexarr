package io.github.flexarr.audio

import android.content.Context
import android.hardware.Sensor
import android.hardware.SensorManager
import android.net.Uri
import androidx.annotation.OptIn
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableDoubleStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.media3.common.AudioAttributes
import androidx.media3.common.C
import androidx.media3.common.ForwardingPlayer
import androidx.media3.common.MediaItem
import androidx.media3.common.MediaMetadata
import androidx.media3.common.PlaybackException
import androidx.media3.common.Player
import androidx.media3.common.util.UnstableApi
import androidx.media3.datasource.HttpDataSource
import androidx.media3.exoplayer.ExoPlayer
import io.github.flexarr.AppModel
import io.github.flexarr.core.BookChapter
import io.github.flexarr.core.BookFile
import io.github.flexarr.core.BookListening
import io.github.flexarr.core.BookManifest
import io.github.flexarr.core.BookPause
import io.github.flexarr.core.BookSessionClock
import io.github.flexarr.core.BookSpeed
import io.github.flexarr.core.ChapterSleep
import io.github.flexarr.core.endBookSession
import io.github.flexarr.core.reportBookSession
import io.github.flexarr.core.BookTimeline
import io.github.flexarr.core.Bookmark
import io.github.flexarr.core.FlexarrClient
import io.github.flexarr.core.ItemSummary
import io.github.flexarr.core.JumpOffer
import io.github.flexarr.core.PlaybackRequest
import io.github.flexarr.core.SmartRewind
import io.github.flexarr.core.addBookmark
import io.github.flexarr.core.bookManifest
import io.github.flexarr.core.chapterAt
import io.github.flexarr.core.deleteBookmark
import io.github.flexarr.core.isBookFinished
import io.github.flexarr.core.jumpOffer
import io.github.flexarr.core.nextChapterStart
import io.github.flexarr.core.previousChapterStart
import io.github.flexarr.logging.FlexarrLog
import io.github.flexarr.player.DeviceCapabilities
import io.github.flexarr.player.ShakeListener
import io.github.flexarr.player.SleepTimer
import io.github.flexarr.player.mediaSources
import io.github.flexarr.widget.PodcastWidgets
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch

private val flexarrLog = FlexarrLog("books")

/**
 * Audiobooks: a book's files play back to back as one timeline (a Media3 playlist, mapped to and from the timeline with
 * [BookTimeline]), so the scrubber, chapters, bookmarks and progress cover the whole book. Speeds from 0.5× to 3.5× kept
 * per book, skips by the profile's steps, smart rewind after a pause, a sleep timer that can stop at the end of the
 * chapter (and a shake of the phone adds time), and at the end the next book in its series offered, never started.
 * The lock screen and the car show the chapter, with chapter skips. [AudioService] publishes it while it's the one
 * playing; music and podcasts stop for it, and it for them.
 */
@OptIn(UnstableApi::class)
class BookPlayer(private val context: Context, private val model: AppModel) {
    private var created = false

    val player: ExoPlayer by lazy {
        created = true
        ExoPlayer.Builder(context)
            .setMediaSourceFactory(mediaSources(context))
            .setAudioAttributes(AudioAttributes.Builder().setUsage(C.USAGE_MEDIA).setContentType(C.AUDIO_CONTENT_TYPE_SPEECH).build(), true)
            .setHandleAudioBecomingNoisy(true)
            .setWakeMode(C.WAKE_MODE_NETWORK)
            .build()
            .also { it.addListener(listener) }
    }

    /**
     * What the lock screen, headphones and the car drive: skips go by the profile's steps, next and previous go by
     * chapter, and the place and length shown are the chapter's (a book's files can be one twenty-hour file or forty
     * short ones; neither means much on a lock screen).
     */
    val sessionPlayer: Player by lazy {
        object : ForwardingPlayer(player) {
            override fun getAvailableCommands(): Player.Commands =
                super.getAvailableCommands().buildUpon().addAll(*CHAPTER_COMMANDS.toIntArray()).build()

            override fun isCommandAvailable(command: Int): Boolean = command in CHAPTER_COMMANDS || super.isCommandAvailable(command)

            override fun seekBack() = skip(-listening.skipBackSeconds.toDouble())
            override fun seekForward() = skip(listening.skipForwardSeconds.toDouble())
            override fun getSeekBackIncrement(): Long = listening.skipBackSeconds * 1000L
            override fun getSeekForwardIncrement(): Long = listening.skipForwardSeconds * 1000L
            override fun seekToNext() = nextChapter()
            override fun seekToNextMediaItem() = nextChapter()
            override fun seekToPrevious() = previousChapter()
            override fun seekToPreviousMediaItem() = previousChapter()

            override fun getDuration(): Long = chapterSpan()?.let { ((it.second - it.first) * 1000).toLong() } ?: (this@BookPlayer.duration * 1000).toLong()
            override fun getContentDuration(): Long = getDuration()
            override fun getCurrentPosition(): Long = ((timelinePosition() - (chapterSpan()?.first ?: 0.0)) * 1000).toLong().coerceAtLeast(0)
            override fun getContentPosition(): Long = getCurrentPosition()
            override fun getBufferedPosition(): Long = getCurrentPosition()
            override fun seekTo(positionMs: Long) = seek((chapterSpan()?.first ?: 0.0) + positionMs / 1000.0)

            // Play with nothing loaded (it failed, a video stopped it): ask for the book again.
            override fun play() = resume()
        }
    }

    private val scope = model.scope

    /** The book playing (or paused); null once the bar is closed. */
    var current: ItemSummary? by mutableStateOf(null)
        private set
    var manifest: BookManifest? by mutableStateOf(null)
        private set
    var isPlaying by mutableStateOf(false)
        private set
    var isLoading by mutableStateOf(false)
        private set
    /** On the whole book's timeline. */
    var position by mutableDoubleStateOf(0.0)
        private set
    var duration by mutableDoubleStateOf(0.0)
        private set
    var speed by mutableDoubleStateOf(1.0)
        private set
    var errorMessage: String? by mutableStateOf(null)
        private set
    var bookmarks: List<Bookmark> by mutableStateOf(emptyList())
        private set
    /** The profile's skip steps, default speed and smart rewind. */
    var listening: BookListening by mutableStateOf(BookListening())
    /** Another device got further: offered, never jumped to by itself. */
    var jumpOffer: JumpOffer? by mutableStateOf(null)
        private set
    /** The book ended, and it's in a series with a next book: offered with a Play button. */
    var nextOffer: ItemSummary? by mutableStateOf(null)
        private set

    val chapters: List<BookChapter> get() = manifest?.chapters.orEmpty()
    val chapterIndex: Int? get() = chapters.chapterAt(position)
    val chapter: BookChapter? get() = chapterIndex?.let(chapters::getOrNull)

    /** Playing, or about to: what a guest's tunnel stays up for. */
    val wantsToPlay: Boolean
        get() = isPlaying || isLoading ||
            (created && player.playWhenReady && player.playbackState.let { it == Player.STATE_BUFFERING || it == Player.STATE_READY })

    /** The sleep timer; End of chapter stops where the chapter playing ends (the one gone to, after a seek). */
    val sleep = SleepTimer(scope).also { it.onExpire = { fadeOutThenPause() } }
    private val chapterSleep = ChapterSleep()

    private var client: FlexarrClient? = null
    private var sessionId: String? = null
    /** The server let the session go (twelve hours idle): places go to the book itself, and Play asks for a new one. */
    private var sessionGone = false
    /** Playing from the phone's own copy, with no session: what's heard goes to the downloads, for the server later. */
    private var playingDownload = false
    private var generation = 0
    private var lastReport = 0L
    /** When this device last recorded its place in the book (to tell whether another device's is newer). */
    private var recordedAt: Long? = null
    private var ticker: Job? = null
    /** When it was paused, for smart rewind and for asking the server whether another device moved on meanwhile. */
    private val pause = BookPause()
    /** When the session was last heard from (a report, or playing): apart from [pause], which a seek forgets. */
    private val sessionClock = BookSessionClock()
    /**
     * When a file request last 404'd and a new session was asked for. Another within a minute means the file itself is
     * missing, and it fails; later on (the next night's lapsed session) it restarts again.
     */
    private var restartedForGoneFileAt = 0L
    /** Whether [load] plays: a session restarted while paused stays paused. */
    private var playWhenLoaded = true
    private var shownChapter: Int? = null
    private var files: List<BookFile> = emptyList()

    private val listener = object : Player.Listener {
        override fun onIsPlayingChanged(playing: Boolean) {
            isPlaying = playing
            if (playing) startTicking()
            if (player.playbackState == Player.STATE_READY) report(paused = !playing)
            // The home screen's Continue listening widget follows.
            scope.launch { PodcastWidgets.refresh(context, askServer = false) }
        }

        override fun onPlayWhenReadyChanged(playWhenReady: Boolean, reason: Int) {
            if (!playWhenReady) {
                if (player.playbackState != Player.STATE_ENDED) pause.paused(System.currentTimeMillis())
                return
            }
            val away = pause.resumed(System.currentTimeMillis()) ?: return
            if (listening.smartRewind) {
                val back = SmartRewind.seconds(away)
                // Not a place the listener chose: the end of chapter the sleep timer waits for stays as it was.
                if (back > 0) moveTo(timelinePosition() - back)
            }
            if (away > 60_000) checkElsewhere()
        }

        override fun onPlaybackStateChanged(state: Int) {
            if (state == Player.STATE_ENDED) bookEnded()
        }

        override fun onMediaItemTransition(mediaItem: MediaItem?, reason: Int) {
            // The next file: the chapter shown may be the same, but the item's metadata is the file's own until it's set.
            shownChapter = null
            showChapter()
        }

        override fun onPlayerError(error: PlaybackException) {
            val book = current
            // The session's file addresses went with it (let go on the server): a new session, from the same place.
            val now = System.currentTimeMillis()
            if (book != null && sessionId != null && !playingDownload && now - restartedForGoneFileAt > 60_000 && error.isNotFound()) {
                flexarrLog.info("Book session gone; starting a new one")
                val at = timelinePosition()
                // A skip or a scrub while paused got here: the book stays paused.
                val wasPlaying = player.playWhenReady
                start(book, at.takeIf { it > 0 && it < duration - 1 }, playing = wasPlaying)
                restartedForGoneFileAt = now
                return
            }
            flexarrLog.warning("Book failed", error)
            errorMessage = "This book couldn’t be played."
            endSession()
        }
    }

    // Starting

    /**
     * Plays [book] from where this profile left it, or from [from] (on the timeline: a chapter, a bookmark), at its own
     * speed. Already loaded: it carries on (or goes to [from]).
     */
    fun play(book: ItemSummary, from: Double? = null) {
        val client = model.client
        if (current?.id == book.id && player.playbackState != Player.STATE_IDLE) {
            if (playingDownload || (sessionId != null && !sessionLapsed())) {
                from?.let(::seek)
                if (!player.playWhenReady) player.play()
                return
            }
            // Paused for most of a day: the server has likely let the session go. A new one, from the same place.
            if (sessionId != null) {
                this.client = client
                return start(book, from ?: timelinePosition().takeIf { it > 0 && it < duration - 1 })
            }
        }
        this.client = client
        start(book, from)
    }

    /** Starts [book] over from the beginning (Start over). */
    fun playFromStart(book: ItemSummary) = play(book, from = 0.0)

    private fun start(book: ItemSummary, from: Double?, playing: Boolean = true) {
        generation++
        playWhenLoaded = playing
        val mine = generation
        endSession()
        current = book
        manifest = null
        bookmarks = emptyList()
        jumpOffer = null
        nextOffer = null
        position = from ?: book.userData.positionSeconds
        duration = book.durationSeconds ?: book.book?.durationSeconds ?: 0.0
        isLoading = true
        errorMessage = null
        shownChapter = null
        // One thing plays at a time.
        model.music.stop()
        if (model.podcasts.current != null) model.podcasts.stop()
        // The Continue listening widget shows this book, until an episode is played.
        PodcastWidgets.heard(context, bookId = book.id)
        AudioService.start(context)
        if (startDownloaded(book, from)) return
        val client = client ?: run {
            isLoading = false
            errorMessage = "Connect to your server to play this book."
            return
        }
        scope.launch {
            try {
                val info = client.startPlayback(PlaybackRequest(itemId = book.id, profile = profile, startPositionSeconds = from, deviceName = model.deviceName))
                if (mine != generation) {
                    runCatching { client.stopPlayback(info.sessionId) }
                    return@launch
                }
                val manifest = info.book ?: run {
                    runCatching { client.stopPlayback(info.sessionId) }
                    errorMessage = "This server can’t play books yet."
                    return@launch
                }
                sessionId = info.sessionId
                load(book, manifest, info.startPositionSeconds, manifest.files.map { client.url(it.url) }, artwork = client.imageUrl(book.artwork.poster, 600))
                player.setWakeMode(C.WAKE_MODE_NETWORK)
            } catch (e: Exception) {
                if (mine != generation) return@launch
                flexarrLog.warning("Couldn't start ${book.title}", e)
                errorMessage = e.message
            } finally {
                if (mine == generation) isLoading = false
            }
        }
    }

    /**
     * From the phone's own copy, when the whole book is here: where it was left here, or where the server said when it
     * was last asked. True when it's playing that way.
     */
    private fun startDownloaded(book: ItemSummary, from: Double?): Boolean {
        val download = model.bookDownloads.entry(book.id)?.takeIf { it.isDownloaded } ?: return false
        val files = model.bookDownloads.files(download) ?: return false
        playingDownload = true
        // Where it was left here; or, with nothing heard here since, where the server last said (it may be newer).
        val start = from ?: if (download.unsent != null || download.finishedAtMillis != null) download.position else maxOf(download.position, book.userData.positionSeconds.takeIf { !book.isBookFinished } ?: 0.0)
        val manifest = download.manifest.copy(progress = download.manifest.progress.copy(positionSeconds = start))
        load(book, manifest, start, files.map { Uri.fromFile(it).toString() }, artwork = model.bookDownloads.image(download)?.absolutePath ?: client?.imageUrl(book.artwork.poster, 600))
        // The phone's own copy: no Wi-Fi to keep awake.
        player.setWakeMode(C.WAKE_MODE_LOCAL)
        isLoading = false
        recordedAt = download.unsent?.playedAtMillis
        // Somewhere else may have got further meanwhile.
        checkElsewhere()
        return true
    }

    /** Loads [manifest]'s files as one playlist at [start] on the timeline, and plays. */
    internal fun load(book: ItemSummary, manifest: BookManifest, start: Double, uris: List<String>, artwork: String?) {
        this.manifest = manifest
        files = manifest.files
        bookmarks = manifest.bookmarks
        listening = manifest.listening
        speed = BookSpeed.snapped(manifest.speed)
        duration = BookTimeline.duration(manifest)
        position = start.coerceIn(0.0, duration.takeIf { it > 0 } ?: start)
        val artworkUri = artwork?.let { if (it.startsWith("/")) Uri.fromFile(java.io.File(it)) else Uri.parse(it) }
        val items = manifest.files.mapIndexed { index, file ->
            MediaItem.Builder()
                .setUri(uris[index])
                .setMediaId("book:${book.id}:${file.index}")
                .setMediaMetadata(metadata(book, chapterTitle = null, artworkUri))
                .build()
        }
        val place = BookTimeline.place(files, position)
        player.setMediaItems(items, place.index, (place.offsetSeconds * 1000).toLong())
        player.setPlaybackSpeed(speed.toFloat())
        player.volume = 1f
        player.prepare()
        if (playWhenLoaded) player.play() else player.pause()
        playWhenLoaded = true
        this.artworkUri = artworkUri
        lastReport = System.currentTimeMillis()
        sessionClock.active(lastReport)
        recordedAt = System.currentTimeMillis()
        showChapter()
        startTicking()
    }

    private var artworkUri: Uri? = null

    private fun metadata(book: ItemSummary, chapterTitle: String?, artwork: Uri?): MediaMetadata =
        MediaMetadata.Builder()
            // The lock screen and the car show the chapter, with the book beneath it.
            .setTitle(chapterTitle ?: book.title)
            .setArtist(if (chapterTitle != null) book.title else book.book?.authors)
            .setAlbumTitle(book.title)
            .setAlbumArtist(book.book?.authors)
            .setArtworkUri(artwork)
            .setMediaType(MediaMetadata.MEDIA_TYPE_AUDIO_BOOK_CHAPTER)
            .build()

    /** Puts the chapter playing on the lock screen (the file's item gets new metadata; playback isn't interrupted). */
    private fun showChapter() {
        val book = current ?: return
        val index = chapterIndex
        if (index == shownChapter || player.mediaItemCount == 0) return
        shownChapter = index
        val item = player.currentMediaItem ?: return
        val updated = item.buildUpon().setMediaMetadata(metadata(book, index?.let { chapters[it].title }, artworkUri)).build()
        player.replaceMediaItem(player.currentMediaItemIndex, updated)
    }

    // Controls

    fun togglePlayPause() {
        if (player.isPlaying) return player.pause()
        resume()
    }

    fun pause() {
        if (current != null) player.pause()
    }

    /** Play again: stopped (it ended, or failed), it's asked for again; at the end, it starts over. */
    fun resume() {
        val book = current ?: return
        // A session paused for most of a day has likely been let go by the server: a new one, from the same place.
        if ((sessionId == null && !playingDownload) || sessionLapsed() || player.playbackState == Player.STATE_IDLE) return start(book, position.takeIf { it > 0 && it < duration - 1 })
        if (player.playbackState == Player.STATE_ENDED) return start(book, 0.0)
        player.play()
    }

    /** Session-based, and gone or idle longer than the server keeps one (a seek while paused doesn't count). */
    private fun sessionLapsed(): Boolean =
        sessionId != null && !playingDownload && (sessionGone || sessionClock.lapsed(System.currentTimeMillis()))

    /** Back or forward by [seconds] (the profile's steps). */
    fun skip(seconds: Double) = seek(timelinePosition() + seconds)

    fun skipBack() = skip(-listening.skipBackSeconds.toDouble())

    fun skipForward() = skip(listening.skipForwardSeconds.toDouble())

    /**
     * Goes to [seconds] on the book's timeline, as the listener asked (a chapter, a bookmark, Jump to, the scrubber, a
     * skip): playing after a pause starts right there, with no smart rewind, and End of chapter becomes this chapter's.
     */
    fun seek(seconds: Double) {
        if (current == null) return
        pause.seeked()
        moveTo(seconds)
        chapterSleep.seeked(chapters, position, duration)
    }

    private fun moveTo(seconds: Double) {
        if (current == null) return
        val target = seconds.coerceIn(0.0, if (duration > 0) (duration - 0.5).coerceAtLeast(0.0) else seconds.coerceAtLeast(0.0))
        position = target
        if (files.isEmpty()) return
        val place = BookTimeline.place(files, target)
        player.seekTo(place.index, (place.offsetSeconds * 1000).toLong())
        showChapter()
    }

    fun nextChapter() {
        chapters.nextChapterStart(timelinePosition())?.let(::seek)
    }

    fun previousChapter() = seek(chapters.previousChapterStart(timelinePosition()))

    /** A book's own speed, kept on the server for it (0.5× to 3.5× in 0.05 steps). */
    fun chooseSpeed(speed: Double) {
        val snapped = BookSpeed.snapped(speed)
        this.speed = snapped
        player.setPlaybackSpeed(snapped.toFloat())
        val book = current ?: return
        // From the phone's copy: kept with it (it plays at it next time), and sent when the server can be reached.
        if (playingDownload) return model.bookDownloads.speedChosen(book.id, snapped)
        val client = client ?: return
        scope.launch { runCatching { client.setPlaybackSpeed(book.id, snapped) } }
    }

    /** Sets the sleep timer; End of chapter remembers which chapter's end. */
    fun setSleep(setting: SleepTimer.Setting?) {
        player.volume = 1f
        if (setting == SleepTimer.Setting.EndOfChapter) chapterSleep.set(chapters, timelinePosition(), duration) else chapterSleep.clear()
        sleep.set(setting)
        listenForShakes()
    }

    /** Marks the place playing now, with an optional note. */
    fun addBookmark(note: String? = null, onDone: (Boolean) -> Unit = {}) {
        val book = current ?: return
        val client = client ?: return onDone(false)
        val at = timelinePosition()
        scope.launch {
            val added = runCatching { client.addBookmark(book.id, at, note) }.getOrNull()
            if (added != null) bookmarks = (bookmarks + added).sortedBy { it.positionSeconds }
            onDone(added != null)
        }
    }

    fun deleteBookmark(bookmark: Bookmark) {
        val client = client ?: return
        bookmarks = bookmarks.filterNot { it.id == bookmark.id }
        scope.launch { runCatching { client.deleteBookmark(bookmark.id) } }
    }

    fun acceptJump() {
        val offer = jumpOffer ?: return
        jumpOffer = null
        seek(offer.positionSeconds)
    }

    fun dismissJump() {
        jumpOffer = null
        // Kept here: this place is the newest now.
        recordedAt = System.currentTimeMillis()
        report(paused = !player.isPlaying)
    }

    /** Plays the next book in the series, as the end of a book offered. */
    fun playNext() {
        val next = nextOffer ?: return
        nextOffer = null
        start(next, null)
    }

    fun dismissNext() {
        nextOffer = null
    }

    /** The bar's close: stops, reports where it got to, and puts the controls away. */
    fun stop() {
        generation++
        sleep.set(null)
        chapterSleep.clear()
        listenForShakes()
        endSession()
        if (created) {
            player.stop()
            player.clearMediaItems()
        }
        current = null
        manifest = null
        isPlaying = false
        isLoading = false
        jumpOffer = null
        nextOffer = null
        AudioService.stopIfIdle(context)
        scope.launch { PodcastWidgets.refresh(context, askServer = false) }
    }

    /** Playing this book from its downloaded files now. */
    fun isPlayingFile(itemId: String): Boolean = playingDownload && current?.id == itemId

    // Following it

    /** The place on the book's timeline the player is at now. */
    fun timelinePosition(): Double {
        if (!created || files.isEmpty() || player.playbackState == Player.STATE_IDLE) return position
        return BookTimeline.position(files, player.currentMediaItemIndex, player.currentPosition / 1000.0)
    }

    /** Where the chapter playing starts and ends, if the book has chapters. */
    private fun chapterSpan(): Pair<Double, Double>? {
        val at = timelinePosition()
        val index = chapters.chapterAt(at) ?: return null
        val chapter = chapters[index]
        return chapter.startSeconds to chapter.endSeconds.takeIf { it > chapter.startSeconds }.let { it ?: chapters.getOrNull(index + 1)?.startSeconds ?: duration }
    }

    private fun startTicking() {
        if (ticker?.isActive == true) return
        ticker = scope.launch {
            while (isActive) {
                if ((sessionId != null || playingDownload) && player.playbackState != Player.STATE_IDLE) {
                    position = timelinePosition()
                    showChapter()
                    checkSleep()
                }
                if (!player.isPlaying) break
                sessionClock.active(System.currentTimeMillis())
                if (System.currentTimeMillis() - lastReport >= REPORT_INTERVAL_MS) report(paused = false)
                delay(if (appInFront) 250 else 1_000)
            }
        }
    }

    /** End of chapter, and the last seconds of a timer of minutes fading out. */
    private fun checkSleep() {
        val left = chapterSleep.left(position, speed)
        if (left != null && sleep.setting == SleepTimer.Setting.EndOfChapter) {
            if (left <= 0.3) {
                sleep.set(null)
                chapterSleep.clear()
                player.pause()
                player.volume = 1f
                expiredAt = System.currentTimeMillis()
                listenForShakes()
            } else if (left < FADE_SECONDS) {
                player.volume = (left / FADE_SECONDS).toFloat()
            }
            return
        }
        val remaining = sleep.remaining ?: return
        if (remaining < FADE_SECONDS) player.volume = (remaining / FADE_SECONDS).toFloat().coerceIn(0f, 1f)
    }

    private fun fadeOutThenPause() {
        player.pause()
        player.volume = 1f
        expiredAt = System.currentTimeMillis()
        listenForShakes()
    }

    // Shake to add time: while a timer runs, and for a while after it stopped the book.

    private var expiredAt: Long? = null
    private var shakes: ShakeListener? = null

    private fun listenForShakes() {
        val sensors = context.getSystemService(SensorManager::class.java)
        val accelerometer = sensors?.getDefaultSensor(Sensor.TYPE_ACCELEROMETER)
        val wanted = sleep.setting != null || expiredAt?.let { System.currentTimeMillis() - it < SHAKE_AFTER_MS } == true
        if (wanted && shakes == null && sensors != null && accelerometer != null) {
            shakes = ShakeListener(::shaken).also { sensors.registerListener(it, accelerometer, SensorManager.SENSOR_DELAY_UI) }
            if (sleep.setting == null) scope.launch {
                delay(SHAKE_AFTER_MS)
                listenForShakes()
            }
        } else if (!wanted) {
            shakes?.let { sensors?.unregisterListener(it) }
            shakes = null
        }
    }

    /** A shake: a running timer gets more time; one that just stopped the book starts again for a while and plays. */
    private fun shaken() {
        player.volume = 1f
        when {
            sleep.endsAt != null -> sleep.extend(SHAKE_MINUTES)
            sleep.setting == SleepTimer.Setting.EndOfChapter -> Unit
            expiredAt != null -> {
                expiredAt = null
                sleep.set(SleepTimer.Setting.Minutes(SHAKE_MINUTES))
                resume()
            }
        }
        listenForShakes()
    }

    // The server

    private fun report(paused: Boolean, ended: Boolean = false) {
        val book = current ?: return
        lastReport = System.currentTimeMillis()
        sessionClock.active(lastReport)
        if (!model.recordsHistory) return
        val at = if (ended) duration else timelinePosition()
        recordedAt = System.currentTimeMillis()
        if (playingDownload) {
            model.bookDownloads.record(book.id, at, ended)
            return
        }
        val client = client ?: return
        val session = sessionId ?: return
        val gone = sessionGone
        scope.launch {
            runCatching { client.reportBookSession(session, book.id, at, paused, ended, model.deviceName, gone) }
                .onSuccess { alive -> if (!alive && sessionId == session) sessionGone = true }
        }
    }

    /**
     * Back after a while away: if another device got further meanwhile, offer to jump there. Playing from the phone's
     * copy, the server's bookmarks, listening settings and speed (unless one chosen here is still to be sent) are taken
     * in too, as the copy's manifest is from when it was downloaded.
     */
    private fun checkElsewhere() {
        val book = current ?: return
        val client = client ?: return
        val since = recordedAt
        scope.launch {
            val fresh = runCatching { client.bookManifest(book.id, profile.audioCodecs) }.getOrNull() ?: return@launch
            if (current?.id != book.id) return@launch
            jumpOffer = fresh.progress.jumpOffer(timelinePosition(), since, model.deviceName)
            if (playingDownload) {
                val kept = model.bookDownloads.refreshed(book.id, fresh)?.manifest ?: return@launch
                manifest = manifest?.copy(bookmarks = kept.bookmarks, listening = kept.listening, speed = kept.speed, nextInSeries = kept.nextInSeries)
                bookmarks = kept.bookmarks
                listening = kept.listening
                val snapped = BookSpeed.snapped(kept.speed)
                if (snapped != speed) {
                    speed = snapped
                    player.setPlaybackSpeed(snapped.toFloat())
                }
            }
        }
    }

    /** Reports the last place (the end, with [ended]) and stops the session, one after the other. */
    private fun endSession(ended: Boolean = false) {
        ticker?.cancel()
        sessionClock.clear()
        if (playingDownload) {
            playingDownload = false
            val book = current
            if (book != null && model.recordsHistory && created && player.playbackState != Player.STATE_ENDED && timelinePosition() > 0) {
                model.bookDownloads.record(book.id, timelinePosition(), finished = false)
            }
            model.watchStateChanged()
            return
        }
        val client = client ?: return
        val session = sessionId ?: return
        val book = current ?: return
        sessionId = null
        val at = if (ended) duration else timelinePosition()
        val records = model.recordsHistory
        val gone = sessionGone
        sessionGone = false
        if (records) recordedAt = System.currentTimeMillis()
        scope.launch {
            client.endBookSession(session, book.id, at, ended, records, model.deviceName, gone)
            model.watchStateChanged()
        }
    }

    /** The end of the book: finished (the server counts it), and the next in its series offered. */
    private fun bookEnded() {
        current ?: return
        // The phone's copy keeps it here; a session reports the end and then stops, in that order (endSession).
        if (playingDownload) report(paused = true, ended = true)
        val next = manifest?.nextInSeries
        endSession(ended = true)
        position = 0.0
        sleep.set(null)
        chapterSleep.clear()
        player.volume = 1f
        nextOffer = next
        // Continue listening drops it now: a moment later, once the server has it.
        scope.launch {
            delay(1_500)
            model.watchStateChanged()
        }
    }

    internal val profile by lazy {
        DeviceCapabilities.profile(context).let { it.copy(containers = it.containers + listOf("mp3", "m4a", "m4b", "aac", "flac", "ogg", "opus", "wav")) }
    }

    companion object {
        private const val REPORT_INTERVAL_MS = 15_000L
        private const val FADE_SECONDS = 10.0
        private const val SHAKE_MINUTES = 5
        private const val SHAKE_AFTER_MS = 5 * 60_000L

        /** A file request the server answered 404: what a lapsed session's file addresses get. */
        private fun PlaybackException.isNotFound(): Boolean =
            generateSequence<Throwable>(this) { it.cause }.take(8).any { (it as? HttpDataSource.InvalidResponseCodeException)?.responseCode == 404 }

        private val CHAPTER_COMMANDS = listOf(
            Player.COMMAND_SEEK_TO_NEXT,
            Player.COMMAND_SEEK_TO_NEXT_MEDIA_ITEM,
            Player.COMMAND_SEEK_TO_PREVIOUS,
            Player.COMMAND_SEEK_TO_PREVIOUS_MEDIA_ITEM,
            Player.COMMAND_SEEK_BACK,
            Player.COMMAND_SEEK_FORWARD,
        )
    }
}
