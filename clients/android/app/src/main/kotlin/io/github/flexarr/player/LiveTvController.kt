package io.github.flexarr.player

import androidx.annotation.OptIn
import androidx.media3.common.util.UnstableApi
import io.github.flexarr.logging.FlexarrLog
import android.content.Context
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
import androidx.media3.common.VideoSize
import androidx.media3.exoplayer.ExoPlayer
import io.github.flexarr.AppModel
import io.github.flexarr.data.PlayerSettings
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

private val flexarrLog = FlexarrLog("livetv")

/**
 * Watching a live channel: asks the server for it, plays it with the rewind window as the timeline, keeps the server
 * told the viewer is there, and changes channel (Previous and Next go through [channels] in the order they were
 * listed). "Back to live" appears once the picture is well behind. The iPhone app's LiveTvController.
 */
@OptIn(UnstableApi::class)
class LiveTvController(context: Context, private val model: AppModel, val client: FlexarrClient, private val onClose: () -> Unit) {
    /** Who's watching, for what's kept per profile (the TV's guide). */
    val profileId: String? get() = model.currentProfile?.id

    /** How far back and forward jump in the rewind window, in seconds (Settings › Playback). */
    val skipSeconds = PlayerSettings.skipSeconds(context)

    /** Dialogue boost, night mode and the speakers, as for films: the phone's sound settings. */
    private val shaper = SoundShaper()

    val player: ExoPlayer = ExoPlayer.Builder(context, shaper.renderers(context))
        .setMediaSourceFactory(mediaSources(context))
        .setAudioAttributes(AudioAttributes.Builder().setUsage(C.USAGE_MEDIA).setContentType(C.AUDIO_CONTENT_TYPE_MOVIE).build(), true)
        .setHandleAudioBecomingNoisy(true)
        .setSeekBackIncrementMs(skipSeconds * 1000L)
        .setSeekForwardIncrementMs(skipSeconds * 1000L)
        .build()
    private val scope = model.scope
    private val soundJob = shaper.follow(scope, context.applicationContext)

    /** The sleep timer, as over a film (minutes only: a channel has no end of episode). It carries on across channels. */
    val sleep = VideoSleep(scope, player)
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
    /** The picture's size, for the corner window's shape. */
    var videoSize: VideoSize by mutableStateOf(VideoSize.UNKNOWN)
        private set

    /** The channel before this one, to flip between two (Last watched in the channel list). */
    var previousChannel: LiveChannel? = null
        private set
    private var lastChannel: LiveChannel? = null
    private var starting: Job? = null
    private var heartbeat: Job? = null
    private var ticker: Job? = null
    /** The player has closed: nothing more starts, and a channel arriving late is let go. */
    private var released = false

    init {
        player.addListener(object : Player.Listener {
            override fun onIsPlayingChanged(playing: Boolean) {
                isPlaying = playing
                if (playing) keepTicking()
                // Played again from elsewhere (headphones, say) after the sleep timer ran out: awake.
                if (playing && sleep.phase == VideoSleep.Phase.Asleep) sleep.wake()
            }

            override fun onVideoSizeChanged(size: VideoSize) {
                videoSize = size
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
                flexarrLog.warning("Live channel failed", e)
                close(PlaybackController.playerErrorMessage(e, subject = "This channel"))
            }
        })
    }

    /** Plays [channel]; [among] is the order Previous and Next go through. */
    fun watch(channel: LiveChannel, among: List<LiveChannel>) {
        channels = among.ifEmpty { listOf(channel) }
        start(channel)
    }

    fun start(channel: LiveChannel) {
        if (released) return
        // Music or a podcast playing stops for the channel.
        model.podcasts.pause()
        model.music.pause()
        model.books.pause()
        starting?.cancel()
        startingChannel = channel
        model.liveStartingChannel = channel.id
        error = null
        // The old channel's picture goes first: the server lets it go as this one starts.
        val previous = watching?.viewer
        heartbeat?.cancel()
        player.stop()
        // The last channel's rewind ("Back to live", how far behind) isn't this one's: it starts at live.
        position = 0.0
        duration = 0.0
        isBehind = false
        showTitle(channel, channel.now)
        starting = scope.launch {
            try {
                val started = client.watchLiveChannel(channel.id, replacing = previous, surround = surround)
                present(started)
            } catch (e: FlexarrException.RecordingInTheWayException) {
                watching = null
                recordingInTheWay = e.recording to channel
            } catch (e: FlexarrException) {
                flexarrLog.warning("Couldn't start ${channel.name}", e)
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
        // Closed while it was on its way.
        if (released) {
            scope.launch { runCatching { client.stopLiveChannel(started.viewer) } }
            return
        }
        flexarrLog.info("Watching ${started.channel.name} (${started.method})")
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
        // Asleep (the sleep timer ran out): stay paused under the dim.
        if (sleep.phase != VideoSleep.Phase.Asleep) player.play()
        keepAlive(started.viewer)
        keepTicking()
    }

    private fun showTitle(channel: LiveChannel, programme: LiveProgramme?) {
        title = programme?.displayTitle ?: channel.name
        subtitle = "${channel.numberText} ${channel.name}" + (programme?.let { "   " + timeRange(it) } ?: "")
    }

    /** Paused players fetch nothing, so the server is told the viewer is still here. What's on is refreshed with it. */
    private fun keepAlive(viewer: String) {
        if (released) return
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

    /**
     * Follows the picture's place in the window twice a second while playing; paused, now and then, as live moves on
     * without it (for Back to live).
     */
    private fun keepTicking() {
        if (ticker?.isActive == true || released) return
        ticker = scope.launch {
            while (isActive && watching != null) {
                tick()
                // Quicker while "Still watching?" counts down and fades the sound.
                delay(if (sleep.phase == VideoSleep.Phase.Asking) 250 else if (player.isPlaying) 500 else 2_000)
            }
        }
    }

    private fun tick() {
        if (watching == null) return
        sleep.check()
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
        keepTicking()
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
        released = true
        ticker?.cancel()
        soundJob.cancel()
        sleep.set(null)
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
