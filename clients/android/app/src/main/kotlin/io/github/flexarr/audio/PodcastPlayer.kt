package io.github.flexarr.audio

import io.github.flexarr.logging.FlexarrLog
import android.content.Context
import android.net.Uri
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableDoubleStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.core.content.edit
import androidx.media3.common.AudioAttributes
import androidx.media3.common.C
import androidx.media3.common.MediaItem
import androidx.media3.common.MediaMetadata
import androidx.media3.common.PlaybackException
import androidx.media3.common.Player
import androidx.media3.exoplayer.ExoPlayer
import io.github.flexarr.AppModel
import io.github.flexarr.core.FlexarrClient
import io.github.flexarr.core.PlaybackRequest
import io.github.flexarr.core.PodcastEpisode
import io.github.flexarr.core.PodcastPreferencesUpdate
import io.github.flexarr.core.asPodcastEpisode
import io.github.flexarr.core.nextEpisode
import io.github.flexarr.core.setPodcastPreferences
import io.github.flexarr.player.DeviceCapabilities
import io.github.flexarr.player.SleepTimer
import io.github.flexarr.widget.PodcastWidgets
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch

private val flexarrLog = FlexarrLog("podcasts")

/**
 * Podcast episodes, playing on under every screen and with the screen off: they resume, play faster or slower, skip
 * back 15 seconds and forward 30, can stop at a sleep timer, and go on by themselves to the next episode the server
 * suggests (Up Next first). The iPhone app's MusicPlayer, as far as podcasts need it. [AudioService] puts its player
 * on the lock screen and in the notification shade.
 */
class PodcastPlayer(private val context: Context, private val model: AppModel) {
    val player: ExoPlayer by lazy {
        ExoPlayer.Builder(context)
            .setAudioAttributes(AudioAttributes.Builder().setUsage(C.USAGE_MEDIA).setContentType(C.AUDIO_CONTENT_TYPE_SPEECH).build(), true)
            .setHandleAudioBecomingNoisy(true)
            .setSeekBackIncrementMs(BACK_MS)
            .setSeekForwardIncrementMs(FORWARD_MS)
            // Heard with the screen off: the phone (and its Wi-Fi, for a stream) stays awake while it plays.
            .setWakeMode(C.WAKE_MODE_NETWORK)
            .build()
            .also { it.addListener(listener) }
    }
    private val scope = model.scope
    private val saved = context.getSharedPreferences("podcasts", Context.MODE_PRIVATE)

    /** The episode playing (or paused); null once the bar is closed. */
    var current: PodcastEpisode? by mutableStateOf(null)
        private set
    var isPlaying by mutableStateOf(false)
        private set
    /** A session is being started for the current episode. */
    var isLoading by mutableStateOf(false)
        private set
    var position by mutableDoubleStateOf(0.0)
        private set
    var duration by mutableDoubleStateOf(0.0)
        private set
    var speed by mutableDoubleStateOf(1.0)
        private set
    var errorMessage: String? by mutableStateOf(null)
        private set

    /** When the episode should stop by itself: after so many minutes (it pauses), or at the end of the episode. */
    val sleep = SleepTimer(scope).also { it.onExpire = { player.pause() } }

    private var client: FlexarrClient? = null
    private var sessionId: String? = null
    /** Playing from a download on the phone, with no session: what's heard goes to the downloads, for the server later. */
    private var playingDownload = false
    /** Bumped by every episode change, so a session that arrives for one skipped past is ended rather than played. */
    private var generation = 0
    private var lastReport = 0L
    private var ticker: Job? = null

    private val listener = object : Player.Listener {
        override fun onIsPlayingChanged(playing: Boolean) {
            isPlaying = playing
            if (playing) startTicking()
            if (sessionId != null && player.playbackState == Player.STATE_READY) report(paused = !playing)
            // The home screen's play button follows.
            scope.launch { PodcastWidgets.refresh(context, askServer = false) }
        }

        override fun onPlaybackStateChanged(state: Int) {
            if (state == Player.STATE_ENDED) episodeEnded()
        }

        override fun onPlayerError(error: PlaybackException) {
            flexarrLog.warning("Podcast episode failed", error)
            errorMessage = "This episode couldn’t be played."
            endSession()
        }
    }

    /** Plays [episode] from where it was left, at the profile's speed (or the last one chosen here). */
    fun play(episode: PodcastEpisode) {
        val client = model.client ?: return
        // Already the one playing (or paused): carry on from where it is.
        if (current?.id == episode.id && (sessionId != null || playingDownload)) {
            if (!player.isPlaying) player.play()
            return
        }
        this.client = client
        speed = model.podcastPreferences?.speed ?: saved.getFloat(SPEED_KEY, 1f).toDouble()
        start(episode)
    }

    private fun start(episode: PodcastEpisode) {
        val client = client ?: return
        generation++
        val mine = generation
        endSession()
        current = episode
        position = episode.userData.positionSeconds
        duration = episode.durationSeconds ?: 0.0
        isLoading = true
        errorMessage = null
        // One thing plays at a time: music stops for the episode.
        model.music.stop()
        AudioService.start(context)
        val download = model.downloads.entry(episode.id)?.takeIf { it.isDownloaded }
        val file = download?.let(model.downloads::file)
        if (download != null && file != null) return startDownloaded(episode, download, file)
        scope.launch {
            try {
                // Podcasts resume: the server picks the place.
                val info = client.startPlayback(PlaybackRequest(itemId = episode.id, profile = profile))
                if (mine != generation) {
                    runCatching { client.stopPlayback(info.sessionId) } // Skipped past before it arrived.
                    return@launch
                }
                sessionId = info.sessionId
                if (info.durationSeconds > 0) duration = info.durationSeconds
                val artwork = client.imageUrl(episode.artwork.thumb ?: episode.artwork.poster, 600)
                val item = MediaItem.Builder()
                    .setUri(client.url(info.url))
                    .setMediaId(episode.id)
                    .setMediaMetadata(
                        MediaMetadata.Builder()
                            .setTitle(episode.title)
                            .setArtist(episode.podcastTitle)
                            .setArtworkUri(artwork?.let(Uri::parse))
                            .setMediaType(MediaMetadata.MEDIA_TYPE_PODCAST_EPISODE)
                            .build(),
                    )
                    .build()
                player.setMediaItem(item, (info.startPositionSeconds * 1000).toLong())
                player.setWakeMode(C.WAKE_MODE_NETWORK)
                player.setPlaybackSpeed(speed.toFloat())
                player.prepare()
                player.play()
                lastReport = System.currentTimeMillis()
                startTicking()
                // Starting it took it off Up Next on the server.
                model.loadUpNext()
            } catch (e: Exception) {
                if (mine != generation) return@launch
                flexarrLog.warning("Couldn't start ${episode.title}", e)
                errorMessage = e.message
            } finally {
                if (mine == generation) isLoading = false
            }
        }
    }

    /** From the phone's own copy, where it was left (here, or on another device if the server said so since). */
    private fun startDownloaded(episode: PodcastEpisode, download: io.github.flexarr.core.LocalDownload, file: java.io.File) {
        playingDownload = true
        val start = if (download.unsent != null || download.finishedAtMillis != null) download.startPosition else maxOf(download.startPosition, episode.userData.positionSeconds)
        position = start
        if (download.durationSeconds > 0) duration = download.durationSeconds
        val item = MediaItem.Builder()
            .setUri(Uri.fromFile(file))
            .setMediaId(episode.id)
            .setMediaMetadata(
                MediaMetadata.Builder()
                    .setTitle(episode.title)
                    .setArtist(episode.podcastTitle)
                    .setArtworkUri(model.downloads.image(download)?.let(Uri::fromFile))
                    .setMediaType(MediaMetadata.MEDIA_TYPE_PODCAST_EPISODE)
                    .build(),
            )
            .build()
        player.setMediaItem(item, (start * 1000).toLong())
        // The phone's own copy: no Wi-Fi to keep awake.
        player.setWakeMode(C.WAKE_MODE_LOCAL)
        player.setPlaybackSpeed(speed.toFloat())
        player.prepare()
        player.play()
        isLoading = false
        lastReport = System.currentTimeMillis()
        startTicking()
    }

    fun togglePlayPause() {
        if (player.isPlaying) return player.pause()
        val episode = current ?: return
        // Stopped (it ended, or failed): ask for it again.
        if ((sessionId == null && !playingDownload) || player.playbackState == Player.STATE_IDLE || player.playbackState == Player.STATE_ENDED) start(episode) else player.play()
    }

    fun pause() {
        if (current != null) player.pause()
    }

    /** Back or forward a few seconds (15 and 30). */
    fun skip(seconds: Int) = seek(position + seconds)

    fun seek(seconds: Double) {
        val target = seconds.coerceIn(0.0, if (duration > 0) duration else seconds)
        player.seekTo((target * 1000).toLong())
        position = target
    }

    /** A podcast's speed, remembered as the one to use from now on, here and in the profile's preferences. */
    fun chooseSpeed(speed: Double) {
        this.speed = speed
        player.setPlaybackSpeed(speed.toFloat())
        saved.edit { putFloat(SPEED_KEY, speed.toFloat()) }
        val client = client ?: return
        scope.launch {
            runCatching { client.setPodcastPreferences(PodcastPreferencesUpdate(speed = speed)) }
                .onSuccess { model.podcastPreferences = it }
        }
    }

    /** The bar's close: stops, reports where it got to, and puts the controls away. */
    fun stop() {
        generation++
        sleep.set(null)
        endSession()
        player.stop()
        player.clearMediaItems()
        current = null
        isPlaying = false
        isLoading = false
        AudioService.stopIfIdle(context)
    }

    /**
     * Follows the episode while it plays: a few times a second with the app in front, once a second behind it (only the
     * reports need it then). Paused, nothing moves but a seek, which sets the place itself.
     */
    private fun startTicking() {
        if (ticker?.isActive == true) return
        ticker = scope.launch {
            while (isActive) {
                if ((sessionId != null || playingDownload) && player.playbackState != Player.STATE_IDLE) {
                    position = player.currentPosition / 1000.0
                    if (player.duration > 0) duration = player.duration / 1000.0
                }
                if (!player.isPlaying) break
                if (System.currentTimeMillis() - lastReport >= REPORT_INTERVAL_MS) report(paused = false)
                delay(if (appInFront) 250 else 1_000)
            }
        }
    }

    private fun report(paused: Boolean, ended: Boolean = false) {
        if (playingDownload) {
            lastReport = System.currentTimeMillis()
            val episode = current ?: return
            if (model.recordsHistory) model.downloads.record(episode.id, if (ended) duration else player.currentPosition / 1000.0, ended)
            return
        }
        val client = client ?: return
        val session = sessionId ?: return
        lastReport = System.currentTimeMillis()
        if (!model.recordsHistory) return
        val at = if (ended) duration else player.currentPosition / 1000.0
        scope.launch { runCatching { client.reportProgress(session, at, paused, ended) } }
    }

    /** Reports where it got to and ends the session (best effort; the server times abandoned ones out). */
    private fun endSession() {
        if (playingDownload) {
            playingDownload = false
            ticker?.cancel()
            val episode = current
            if (episode != null && model.recordsHistory && player.playbackState != Player.STATE_ENDED && player.currentPosition > 0) {
                model.downloads.record(episode.id, player.currentPosition / 1000.0, finished = false)
            }
            model.watchStateChanged()
            return
        }
        val client = client ?: return
        val session = sessionId ?: return
        sessionId = null
        ticker?.cancel()
        val at = player.currentPosition / 1000.0
        val records = model.recordsHistory
        scope.launch {
            if (records) runCatching { client.reportProgress(session, at, paused = true) }
            runCatching { client.stopPlayback(session) }
            model.watchStateChanged()
        }
    }

    /** Playing this downloaded episode from its file now. */
    fun isPlayingFile(episodeId: String): Boolean = playingDownload && current?.id == episodeId

    /** Heard through: stop if the sleep timer says so, else on to the next episode the server suggests. */
    private fun episodeEnded() {
        val episode = current ?: return
        report(paused = true, ended = true)
        endSession()
        position = 0.0
        if (sleep.setting == SleepTimer.Setting.EndOfEpisode) {
            sleep.set(null)
            return
        }
        val client = client ?: return
        generation++
        val mine = generation
        isLoading = true
        scope.launch {
            // With no connection, the next downloaded episode of the same show.
            val next = runCatching { client.nextEpisode(episode.id) }.getOrNull()
                ?: model.downloads.entry(episode.id)?.let { model.downloads.nextDownloadedEpisode(it.item) }?.item?.asPodcastEpisode()
            if (mine != generation) return@launch
            isLoading = false
            // Nothing left: stay on this one, ready to play again.
            if (next != null) start(next)
        }
    }

    private val profile by lazy {
        // Episodes come as they are: MP3 and AAC files, which ExoPlayer reads itself.
        DeviceCapabilities.profile(context).let { it.copy(containers = it.containers + listOf("mp3", "m4a", "aac", "flac", "ogg", "wav")) }
    }

    companion object {
        /** The speeds a podcast can play at. */
        val speeds = listOf(0.8, 1.0, 1.2, 1.5, 1.75, 2.0, 2.5, 3.0)
        private const val BACK_MS = 15_000L
        private const val FORWARD_MS = 30_000L
        private const val REPORT_INTERVAL_MS = 10_000L
        private const val SPEED_KEY = "speed"

        /** "1×", "1.5×", "1.75×". */
        fun speedLabel(speed: Double): String =
            (if (speed == Math.floor(speed)) speed.toInt().toString() else speed.toString().trimEnd('0')) + "×"
    }
}
