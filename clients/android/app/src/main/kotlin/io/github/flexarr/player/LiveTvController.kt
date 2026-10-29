package io.github.flexarr.player

import android.content.Context
import android.util.Log
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableDoubleStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.media3.common.AudioAttributes
import androidx.media3.common.C
import androidx.media3.common.MediaItem
import androidx.media3.common.MediaMetadata
import androidx.media3.common.MimeTypes
import androidx.media3.common.PlaybackException
import androidx.media3.common.Player
import androidx.media3.exoplayer.ExoPlayer
import io.github.flexarr.AppModel
import io.github.flexarr.core.FlexarrClient
import io.github.flexarr.core.FlexarrException
import io.github.flexarr.core.LiveChannel
import io.github.flexarr.core.LiveProgramme
import io.github.flexarr.core.LiveWatch
import io.github.flexarr.core.RecordingInTheWay
import io.github.flexarr.core.liveChannels
import io.github.flexarr.core.liveHeartbeat
import io.github.flexarr.core.removeRecording
import io.github.flexarr.core.stopLiveChannel
import io.github.flexarr.core.watchLiveChannel
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.time.format.FormatStyle

/**
 * Watching a live channel: asks the server for it, plays it with the rewind window as the timeline, keeps the server
 * told the viewer is there, and changes channel (Previous and Next go through [channels] in the order they were
 * listed). "Back to live" appears once the picture is well behind. The iPhone app's LiveTvController.
 */
class LiveTvController(context: Context, private val model: AppModel, val client: FlexarrClient, private val onClose: () -> Unit) {
    val player: ExoPlayer = ExoPlayer.Builder(context)
        .setAudioAttributes(AudioAttributes.Builder().setUsage(C.USAGE_MEDIA).setContentType(C.AUDIO_CONTENT_TYPE_MOVIE).build(), true)
        .setHandleAudioBecomingNoisy(true)
        .setSeekBackIncrementMs(10_000)
        .setSeekForwardIncrementMs(10_000)
        .build()
    private val scope = model.scope
    /** This device takes Dolby Digital, so a channel's 5.1 can come as it is. */
    private val surround = "ac3" in DeviceCapabilities.profile(context).audioCodecs

    /** The order Previous and Next go through: favourites first, then the rest by number, as listed. */
    var channels: List<LiveChannel> = emptyList()
        private set
    var watching: LiveWatch? by mutableStateOf(null)
        private set
    /** The channel being started, for a spinner. */
    var startingChannel: LiveChannel? by mutableStateOf(null)
        private set
    /** What's on, or the channel's name when there are no listings. */
    var title by mutableStateOf("")
        private set
    /** "101 BBC One · 4:00 – 4:30". */
    var subtitle by mutableStateOf("")
        private set
    /** How the channel reaches the player, when it isn't as it is ("Converted to 720p, deinterlaced"). */
    var detail: String? by mutableStateOf(null)
        private set
    var error: String? by mutableStateOf(null)
        private set
    /** Every stream is recording: the choice between watching that channel and stopping the recording. */
    var recordingInTheWay: Pair<RecordingInTheWay, LiveChannel>? by mutableStateOf(null)
        private set
    var isPlaying by mutableStateOf(false)
        private set
    var isBuffering by mutableStateOf(true)
        private set
    /** Where the picture is in the rewind window, and how long the window is. */
    var position by mutableDoubleStateOf(0.0)
        private set
    var duration by mutableDoubleStateOf(0.0)
        private set
    /** Well behind live: "Back to live" shows. */
    var isBehind by mutableStateOf(false)
        private set

    /** The channel before this one, to flip between two (Last watched in the channel list). */
    var previousChannel: LiveChannel? = null
        private set
    private var lastChannel: LiveChannel? = null
    private var starting: Job? = null
    private var heartbeat: Job? = null
    private var ticker: Job? = null

    init {
        player.addListener(object : Player.Listener {
            override fun onIsPlayingChanged(playing: Boolean) {
                isPlaying = playing
            }

            override fun onPlaybackStateChanged(state: Int) {
                isBuffering = state == Player.STATE_BUFFERING
            }

            override fun onPlayerError(e: PlaybackException) {
                // Fell out of the window (paused for longer than it reaches back): back to live.
                if (e.errorCode == PlaybackException.ERROR_CODE_BEHIND_LIVE_WINDOW) {
                    player.seekToDefaultPosition()
                    player.prepare()
                    return
                }
                Log.w("Flexarr", "Live channel failed", e)
                close("This channel couldn’t be played. ${e.errorCodeName.removePrefix("ERROR_CODE_").replace('_', ' ').lowercase()}.")
            }
        })
        ticker = scope.launch {
            while (isActive) {
                tick()
                delay(500)
            }
        }
    }

    /** Plays [channel]; [among] is the order Previous and Next go through. */
    fun watch(channel: LiveChannel, among: List<LiveChannel>) {
        channels = among.ifEmpty { listOf(channel) }
        start(channel)
    }

    fun start(channel: LiveChannel) {
        // A podcast playing stops for the channel.
        model.podcasts.pause()
        starting?.cancel()
        startingChannel = channel
        model.liveStartingChannel = channel.id
        error = null
        // The old channel's picture goes first: the server lets it go as this one starts.
        val previous = watching?.viewer
        heartbeat?.cancel()
        player.stop()
        showTitle(channel, channel.now)
        starting = scope.launch {
            try {
                val started = client.watchLiveChannel(channel.id, replacing = previous, surround = surround)
                present(started)
            } catch (e: FlexarrException.RecordingInTheWayException) {
                watching = null
                recordingInTheWay = e.recording to channel
            } catch (e: FlexarrException) {
                Log.w("Flexarr", "Couldn't start ${channel.name}", e)
                watching = null
                close(e.message)
            } finally {
                if (startingChannel?.id == channel.id) {
                    startingChannel = null
                    model.liveStartingChannel = null
                }
            }
        }
    }

    private fun present(started: LiveWatch) {
        Log.i("Flexarr", "Watching ${started.channel.name} (${started.method})")
        watching = started
        lastChannel?.let { if (it.id != started.channel.id) previousChannel = it }
        lastChannel = started.channel
        detail = started.method.takeIf { it != "Original" }
        showTitle(started.channel, started.channel.now)
        val metadata = MediaMetadata.Builder()
            .setTitle(title)
            .setArtist(started.channel.name)
            .setArtworkUri(client.imageUrl(started.channel.logoUrl, 600)?.let(android.net.Uri::parse))
            .build()
        player.setMediaItem(
            MediaItem.Builder().setUri(client.url(started.playlist)).setMimeType(MimeTypes.APPLICATION_M3U8).setMediaMetadata(metadata).build(),
        )
        player.prepare()
        player.play()
        keepAlive(started.viewer)
    }

    private fun showTitle(channel: LiveChannel, programme: LiveProgramme?) {
        title = programme?.displayTitle ?: channel.name
        subtitle = "${channel.numberText} ${channel.name}" + (programme?.let { " · " + timeRange(it) } ?: "")
    }

    /** Paused players fetch nothing, so the server is told the viewer is still here. What's on is refreshed with it. */
    private fun keepAlive(viewer: String) {
        heartbeat?.cancel()
        heartbeat = scope.launch {
            var beats = 0
            while (isActive) {
                delay(HEARTBEAT_MS)
                if (watching?.viewer != viewer) return@launch
                try {
                    client.liveHeartbeat(viewer)
                } catch (e: FlexarrException.Http) {
                    // A recording needed the stream: say so, rather than the picture just stopping.
                    if (e.status == 410) return@launch close(e.message ?: "A recording needed the stream.")
                } catch (_: FlexarrException) {
                    // A missed beat; the next one will do.
                }
                beats++
                if (beats % 4 == 0) {
                    val id = watching?.channel?.id ?: continue
                    runCatching { client.liveChannels() }.getOrNull()?.firstOrNull { it.id == id }?.let { showTitle(it, it.now) }
                }
            }
        }
    }

    private fun tick() {
        if (watching == null) return
        val window = player.duration
        if (window <= 0 || window == C.TIME_UNSET) return
        duration = window / 1000.0
        position = player.currentPosition.coerceAtLeast(0) / 1000.0
        isBehind = duration - position > LIVE_SLACK_SECONDS
    }

    fun togglePlayPause() = if (player.isPlaying) player.pause() else player.play()

    fun skip(seconds: Int) = seekTo((position + seconds).coerceIn(0.0, duration))

    fun seekTo(seconds: Double) {
        player.seekTo((seconds * 1000).toLong())
        position = seconds
    }

    fun goLive() {
        player.seekToDefaultPosition()
        player.play()
    }

    /** The next channel up or down, in the order the screen listed them. */
    fun step(by: Int) {
        val current = watching?.channel ?: startingChannel ?: return
        val index = channels.indexOfFirst { it.id == current.id }
        if (index < 0 || channels.size < 2) return
        start(channels[(index + by + channels.size) % channels.size])
    }

    /** Every stream is recording: watch the channel being recorded instead. */
    fun watchRecorded() {
        val (recording, _) = recordingInTheWay ?: return
        recordingInTheWay = null
        channels.firstOrNull { it.id == recording.channelId }?.let { return start(it) }
        scope.launch {
            runCatching { client.liveChannels() }.getOrNull()?.firstOrNull { it.id == recording.channelId }?.let {
                channels = channels + it
                start(it)
            }
        }
    }

    /** Stop the recording and watch the channel that was wanted. */
    fun stopRecordingAndWatch() {
        val (recording, wanted) = recordingInTheWay ?: return
        recordingInTheWay = null
        scope.launch {
            runCatching { client.removeRecording(recording.recordingId) }
            // The recording takes a moment to let its stream go.
            delay(2_500)
            start(wanted)
        }
    }

    fun dismissRecordingChoice() {
        recordingInTheWay = null
        if (watching == null) onClose()
    }

    private fun close(message: String?) {
        error = message
        stopWatching()
        if (message == null) onClose()
    }

    private fun stopWatching() {
        starting?.cancel()
        heartbeat?.cancel()
        player.stop()
        val viewer = watching?.viewer
        watching = null
        if (viewer != null) scope.launch { runCatching { client.stopLiveChannel(viewer) } }
    }

    /** The player closed: the channel is let go. */
    fun release() {
        ticker?.cancel()
        stopWatching()
        model.liveStartingChannel = null
        player.release()
    }

    companion object {
        private const val HEARTBEAT_MS = 15_000L
        /** How far behind live counts as behind (the player sits a few segments back from the edge anyway). */
        private const val LIVE_SLACK_SECONDS = 30.0

        private val clock = DateTimeFormatter.ofLocalizedTime(FormatStyle.SHORT)

        fun startTime(programme: LiveProgramme): String = clock.format(programme.start.atZone(ZoneId.systemDefault()))

        fun timeRange(programme: LiveProgramme): String =
            "${clock.format(programme.start.atZone(ZoneId.systemDefault()))} – ${clock.format(programme.end.atZone(ZoneId.systemDefault()))}"
    }
}
