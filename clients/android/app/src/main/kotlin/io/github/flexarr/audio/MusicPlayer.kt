package io.github.flexarr.audio

import io.github.flexarr.logging.FlexarrLog
import android.content.Context
import android.content.Intent
import android.net.Uri
import androidx.annotation.OptIn
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableDoubleStateOf
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.media3.common.AudioAttributes
import androidx.media3.common.C
import androidx.media3.common.ForwardingPlayer
import androidx.media3.common.MediaItem
import androidx.media3.common.MediaMetadata
import androidx.media3.common.MimeTypes
import androidx.media3.common.PlaybackException
import androidx.media3.common.Player
import androidx.media3.common.util.UnstableApi
import androidx.media3.exoplayer.DefaultRenderersFactory
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.exoplayer.audio.AudioSink
import androidx.media3.exoplayer.audio.DefaultAudioSink
import androidx.media3.exoplayer.audio.TeeAudioProcessor
import io.github.flexarr.AppModel
import io.github.flexarr.core.FlexarrClient
import io.github.flexarr.core.ItemSummary
import io.github.flexarr.core.PlaybackMethod
import io.github.flexarr.core.PlaybackRequest
import io.github.flexarr.core.artistName
import io.github.flexarr.player.DeviceCapabilities
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch

private val flexarrLog = FlexarrLog("music")

/**
 * Plays a queue of tracks through one player: each track is its own server session (started as it comes up, reported
 * on as it plays, ended as it finishes), so the server's history and stream limits see music the way they see video.
 * Lives for the app's lifetime so music carries on while the listener browses, and [AudioService] puts it on the lock
 * screen, in the notification shade and on headphones. The iPhone app's MusicPlayer, for music (podcasts have their
 * own [PodcastPlayer] here).
 */
@OptIn(UnstableApi::class)
class MusicPlayer(private val context: Context, private val model: AppModel) {
    enum class RepeatMode {
        Off, All, One;

        val next: RepeatMode get() = when (this) {
            Off -> All
            All -> One
            One -> Off
        }
    }

    /** The music's bass, middle and treble as it plays, for the visualiser behind Now Playing. */
    val spectrum = AudioSpectrum()

    val player: ExoPlayer by lazy {
        // Every stretch of sound passes the spectrum on its way to the speaker.
        val renderers = object : DefaultRenderersFactory(context) {
            override fun buildAudioSink(context: Context, enableFloatOutput: Boolean, enableAudioTrackPlaybackParams: Boolean): AudioSink =
                DefaultAudioSink.Builder(context)
                    .setEnableFloatOutput(enableFloatOutput)
                    .setEnableAudioTrackPlaybackParams(enableAudioTrackPlaybackParams)
                    .setAudioProcessors(arrayOf(TeeAudioProcessor(spectrum)))
                    .build()
        }
        ExoPlayer.Builder(context, renderers)
            .setAudioAttributes(AudioAttributes.Builder().setUsage(C.USAGE_MEDIA).setContentType(C.AUDIO_CONTENT_TYPE_MUSIC).build(), true)
            .setHandleAudioBecomingNoisy(true)
            .build()
            .also { it.addListener(listener) }
    }

    /**
     * The player as the lock screen, notification and headphones see it: their next and previous move through the
     * queue here (the player itself only ever holds the one track).
     */
    val sessionPlayer: Player by lazy {
        object : ForwardingPlayer(player) {
            override fun getAvailableCommands(): Player.Commands =
                super.getAvailableCommands().buildUpon()
                    .addAll(Player.COMMAND_SEEK_TO_NEXT, Player.COMMAND_SEEK_TO_NEXT_MEDIA_ITEM, Player.COMMAND_SEEK_TO_PREVIOUS, Player.COMMAND_SEEK_TO_PREVIOUS_MEDIA_ITEM)
                    .build()

            override fun isCommandAvailable(command: Int): Boolean =
                command in QUEUE_COMMANDS || super.isCommandAvailable(command)

            override fun seekToNext() = next()
            override fun seekToNextMediaItem() = next()
            override fun seekToPrevious() = previous()
            override fun seekToPreviousMediaItem() = previous()

            // Play with nothing loaded (it failed, or a video stopped it): ask for the track again.
            override fun play() = resume()
        }
    }

    private val scope = model.scope

    /** The tracks in the order they'll play (shuffled when [isShuffled]). */
    var queue: List<ItemSummary> by mutableStateOf(emptyList())
        private set
    var index by mutableIntStateOf(0)
        private set
    var isPlaying by mutableStateOf(false)
        private set
    /** A session is being started for the current track. */
    var isLoading by mutableStateOf(false)
        private set
    var position by mutableDoubleStateOf(0.0)
        private set
    var duration by mutableDoubleStateOf(0.0)
        private set
    var isShuffled by mutableStateOf(false)
        private set
    var repeatMode by mutableStateOf(RepeatMode.Off)
        private set
    var errorMessage: String? by mutableStateOf(null)
        private set

    private var client: FlexarrClient? = null
    /** The queue as it was given, for turning shuffle back off. */
    private var original: List<ItemSummary> = emptyList()
    /**
     * Album names by album id: a track summary carries its artist but not its album, so the screen that queued the
     * tracks passes the albums it knows.
     */
    private var albumTitles: Map<String, String> = emptyMap()
    private var sessionId: String? = null
    /** Bumped by every track change, so a session that arrives for a track skipped past is ended rather than played. */
    private var generation = 0
    private var lastReport = 0L
    private var ticker: Job? = null

    val current: ItemSummary? get() = queue.getOrNull(index)
    /** What plays after the current track, in order. */
    val upNext: List<ItemSummary> get() = queue.drop(index + 1)
    val hasNext: Boolean get() = index + 1 < queue.size || (repeatMode == RepeatMode.All && queue.isNotEmpty())
    val hasPrevious: Boolean get() = index > 0 || repeatMode == RepeatMode.All

    /** The name of the album a queued track is from, when the screen that queued it said. */
    fun albumTitle(track: ItemSummary): String? = track.parentId?.let { albumTitles[it] }

    private val listener = object : Player.Listener {
        override fun onIsPlayingChanged(playing: Boolean) {
            isPlaying = playing
            if (!playing && sessionId != null && player.playbackState == Player.STATE_READY) report(paused = true)
        }

        override fun onPlaybackStateChanged(state: Int) {
            if (state == Player.STATE_ENDED && sessionId != null) trackEnded()
        }

        override fun onPlayerError(error: PlaybackException) {
            flexarrLog.warning("Track failed", error)
            errorMessage = "This track couldn’t be played."
            endSession()
            player.clearMediaItems()
            // Move on rather than fall silent, unless it was the last one.
            if (index + 1 < queue.size) next()
        }
    }

    // Playing

    /**
     * Replaces the queue with [tracks] and starts the one at [start]; with [shuffled], a fresh order starts from its
     * first track instead. [albums] lends the tracks their album names for Now Playing.
     */
    fun play(tracks: List<ItemSummary>, start: Int = 0, shuffled: Boolean = false, albums: List<ItemSummary> = emptyList()) {
        if (tracks.isEmpty()) return
        client = model.client ?: return
        original = tracks
        albumTitles = albums.associate { it.id to it.title }
        isShuffled = shuffled
        if (shuffled) {
            queue = tracks.shuffled()
            index = 0
        } else {
            queue = tracks
            index = start.coerceIn(0, tracks.size - 1)
        }
        startCurrent()
    }

    fun togglePlayPause() = if (player.isPlaying) pause() else resume()

    fun pause() {
        if (current != null) player.pause()
    }

    fun resume() {
        if (current == null) return
        // Stopped for a failure (or the end of the queue): ask for the track again.
        if (sessionId == null || player.playbackState == Player.STATE_IDLE || player.playbackState == Player.STATE_ENDED) startCurrent() else player.play()
    }

    fun next() {
        if (queue.isEmpty()) return
        index = when {
            index + 1 < queue.size -> index + 1
            repeatMode == RepeatMode.All -> 0
            else -> return
        }
        startCurrent()
    }

    /** Like every music player: back restarts the track unless it's just begun. */
    fun previous() {
        if (queue.isEmpty()) return
        if (position > 3) return seek(0.0)
        index = when {
            index > 0 -> index - 1
            repeatMode == RepeatMode.All -> queue.size - 1
            else -> return seek(0.0)
        }
        startCurrent()
    }

    /** Jumps to a track in the queue (from the Up next list). */
    fun skipTo(target: Int) {
        if (target !in queue.indices) return
        index = target
        startCurrent()
    }

    fun seek(seconds: Double) {
        if (current == null) return
        val target = seconds.coerceIn(0.0, if (duration > 0) duration else seconds.coerceAtLeast(0.0))
        position = target
        player.seekTo((target * 1000).toLong())
    }

    /** Shuffles what's left to play (the current track keeps playing), or restores the given order. */
    fun toggleShuffle() {
        val current = current ?: run { isShuffled = !isShuffled; return }
        if (isShuffled) {
            isShuffled = false
            queue = original
            index = original.indexOfFirst { it.id == current.id }.coerceAtLeast(0)
        } else {
            isShuffled = true
            queue = listOf(current) + original.filter { it.id != current.id }.shuffled()
            index = 0
        }
    }

    fun cycleRepeat() {
        repeatMode = repeatMode.next
    }

    /** Ends the session and empties the queue (the now-playing bar goes away). */
    fun stop() {
        if (queue.isEmpty()) return
        generation++
        endSession()
        player.stop()
        player.clearMediaItems()
        queue = emptyList()
        original = emptyList()
        albumTitles = emptyMap()
        index = 0
        position = 0.0
        duration = 0.0
        isPlaying = false
        isLoading = false
        spectrum.reset()
        AudioService.stopIfIdle(context)
    }

    // Sessions

    private fun startCurrent() {
        val track = current ?: return
        val client = client ?: return
        generation++
        val mine = generation
        endSession()
        // One thing plays at a time: a podcast stops (keeping its place) for the music.
        if (model.podcasts.current != null) model.podcasts.stop()
        player.stop()
        player.clearMediaItems()
        position = 0.0
        duration = track.durationSeconds ?: 0.0
        isLoading = true
        errorMessage = null
        spectrum.reset()
        AudioService.start(context)
        scope.launch {
            try {
                // Tracks always start over: a half-heard song isn't resumed the way a film is.
                val info = client.startPlayback(PlaybackRequest(itemId = track.id, profile = profile, startPositionSeconds = 0.0))
                if (mine != generation) {
                    runCatching { client.stopPlayback(info.sessionId) } // Skipped past before it arrived.
                    return@launch
                }
                sessionId = info.sessionId
                if (info.durationSeconds > 0) duration = info.durationSeconds
                val item = MediaItem.Builder()
                    .setUri(client.url(info.url))
                    .setMediaId(track.id)
                    .setMediaMetadata(
                        MediaMetadata.Builder()
                            .setTitle(track.title)
                            .setArtist(track.artistName)
                            .setAlbumTitle(albumTitle(track))
                            .setArtworkUri(client.imageUrl(track.artwork.poster, 600)?.let(Uri::parse))
                            .setMediaType(MediaMetadata.MEDIA_TYPE_MUSIC)
                            .build(),
                    )
                    .apply { if (info.method == PlaybackMethod.Transcode) setMimeType(MimeTypes.APPLICATION_M3U8) }
                    .build()
                player.setMediaItem(item)
                player.prepare()
                player.play()
                lastReport = System.currentTimeMillis()
                startTicking()
            } catch (e: Exception) {
                if (mine != generation) return@launch
                flexarrLog.warning("Couldn't start ${track.title}", e)
                errorMessage = e.message
            } finally {
                if (mine == generation) isLoading = false
            }
        }
    }

    private fun startTicking() {
        ticker?.cancel()
        ticker = scope.launch {
            while (isActive) {
                if (sessionId != null && player.playbackState != Player.STATE_IDLE) {
                    position = player.currentPosition / 1000.0
                    if (player.duration > 0) duration = player.duration / 1000.0
                }
                if (player.isPlaying && System.currentTimeMillis() - lastReport >= REPORT_INTERVAL_MS) report(paused = false)
                delay(250)
            }
        }
    }

    private fun report(paused: Boolean, ended: Boolean = false) {
        val client = client ?: return
        val session = sessionId ?: return
        lastReport = System.currentTimeMillis()
        if (!model.recordsHistory) return
        val at = if (ended) duration else player.currentPosition / 1000.0
        scope.launch { runCatching { client.reportProgress(session, at, paused, ended) } }
    }

    /** Reports where the track got to and ends its session (best effort; the server times abandoned ones out). */
    private fun endSession() {
        val client = client ?: return
        val session = sessionId ?: return
        sessionId = null
        ticker?.cancel()
        val at = player.currentPosition / 1000.0
        val records = model.recordsHistory
        scope.launch {
            if (records) runCatching { client.reportProgress(session, at, paused = true) }
            runCatching { client.stopPlayback(session) }
        }
    }

    private fun trackEnded() {
        report(paused = true, ended = true)
        when {
            repeatMode == RepeatMode.One -> startCurrent()
            index + 1 < queue.size -> {
                index++
                startCurrent()
            }
            repeatMode == RepeatMode.All -> {
                index = 0
                startCurrent()
            }
            else -> {
                // The end of the queue: stay on the last track, ready to play again.
                endSession()
                position = 0.0
                isPlaying = false
                spectrum.reset()
            }
        }
    }

    private val profile by lazy {
        // Music files come as they are where the phone reads them (FLAC, MP3, AAC, Ogg), and converted otherwise.
        DeviceCapabilities.profile(context).let { it.copy(containers = it.containers + listOf("flac", "mp3", "m4a", "aac", "ogg", "opus", "wav")) }
    }

    private companion object {
        const val REPORT_INTERVAL_MS = 10_000L
        val QUEUE_COMMANDS = setOf(
            Player.COMMAND_SEEK_TO_NEXT, Player.COMMAND_SEEK_TO_NEXT_MEDIA_ITEM,
            Player.COMMAND_SEEK_TO_PREVIOUS, Player.COMMAND_SEEK_TO_PREVIOUS_MEDIA_ITEM,
        )
    }
}
