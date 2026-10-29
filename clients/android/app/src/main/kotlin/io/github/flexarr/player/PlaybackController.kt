package io.github.flexarr.player

import android.content.Context
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableDoubleStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.media3.common.AudioAttributes
import androidx.media3.common.C
import androidx.media3.common.MediaItem
import androidx.media3.common.MimeTypes
import androidx.media3.common.PlaybackException
import androidx.media3.common.Player
import androidx.media3.exoplayer.ExoPlayer
import io.github.flexarr.AppModel
import io.github.flexarr.core.FlexarrClient
import io.github.flexarr.core.MarkerType
import io.github.flexarr.core.PlaybackInfo
import io.github.flexarr.core.PlaybackMethod
import io.github.flexarr.core.PlaybackPreferences
import io.github.flexarr.core.PlaybackPrompt
import io.github.flexarr.core.PlaybackRequest
import io.github.flexarr.core.prompt
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch

/**
 * One title playing: asks the server for a session, plays its stream with ExoPlayer, reports progress, offers Skip
 * Intro and Next Episode, and moves on to the next episode. The iPhone app's PlaybackController, for the phone.
 */
class PlaybackController(context: Context, private val model: AppModel, private val client: FlexarrClient, private val onClose: () -> Unit) {
    val player: ExoPlayer = ExoPlayer.Builder(context)
        .setAudioAttributes(AudioAttributes.Builder().setUsage(C.USAGE_MEDIA).setContentType(C.AUDIO_CONTENT_TYPE_MOVIE).build(), true)
        .setHandleAudioBecomingNoisy(true)
        .setSeekBackIncrementMs(SKIP_MS)
        .setSeekForwardIncrementMs(SKIP_MS)
        .build()
    private val profile = DeviceCapabilities.profile(context)
    private val scope = model.scope

    var info: PlaybackInfo? by mutableStateOf(null)
        private set
    var error: String? by mutableStateOf(null)
        private set
    var isPlaying by mutableStateOf(false)
        private set
    var isBuffering by mutableStateOf(true)
        private set
    var position by mutableDoubleStateOf(0.0)
        private set
    var duration by mutableDoubleStateOf(0.0)
        private set
    /** What the pill above the timeline says (Skip Intro, Next Episode in 4), or null for nothing. */
    var promptTitle: String? by mutableStateOf(null)
        private set
    /** Counting down to the next episode: Cancel shows beside the pill. */
    var countingDown by mutableStateOf(false)
        private set

    private var prompt: PlaybackPrompt = PlaybackPrompt.None
    private var preferences = PlaybackPreferences()
    private val autoSkipped = mutableSetOf<String>()
    private var lastReport = 0L
    private var isFinishing = false
    private var nextCancelled = false
    private var countdown: Int? = null
    private var countdownJob: Job? = null
    private var ticker: Job? = null

    init {
        player.addListener(object : Player.Listener {
            override fun onIsPlayingChanged(playing: Boolean) {
                isPlaying = playing
                // Paused or playing again: the server hears straight away.
                if (info != null && player.playbackState == Player.STATE_READY) report(paused = !playing)
            }

            override fun onPlaybackStateChanged(state: Int) {
                isBuffering = state == Player.STATE_BUFFERING
                if (state == Player.STATE_ENDED) itemEnded()
            }

            override fun onPlayerError(e: PlaybackException) {
                error = "This couldn’t be played. ${e.errorCodeName.removePrefix("ERROR_CODE_").replace('_', ' ').lowercase()}."
            }
        })
    }

    /** Asks the server for [itemId] (or a random episode of a show, with [shuffle]) and starts it. */
    fun start(itemId: String, fromBeginning: Boolean = false, shuffle: Boolean = false) {
        scope.launch {
            try {
                val request = PlaybackRequest(
                    itemId = itemId,
                    profile = profile,
                    startPositionSeconds = if (fromBeginning) 0.0 else null,
                    replacesSessionId = info?.sessionId,
                    shuffle = if (shuffle) true else null,
                )
                val started = client.startPlayback(request)
                begin(started)
            } catch (e: Exception) {
                error = e.message
            }
        }
    }

    private fun begin(started: PlaybackInfo) {
        info = started
        error = null
        preferences = started.preferences ?: PlaybackPreferences()
        prompt = PlaybackPrompt.None
        autoSkipped.clear()
        isFinishing = false
        nextCancelled = false
        countdown = null
        countdownJob?.cancel()
        updatePrompt()
        duration = started.durationSeconds
        val media = MediaItem.Builder()
            .setUri(client.url(started.url))
            .apply { if (started.method == PlaybackMethod.Transcode) setMimeType(MimeTypes.APPLICATION_M3U8) }
            .build()
        player.setMediaItem(media, (started.startPositionSeconds * 1000).toLong())
        player.prepare()
        player.play()
        lastReport = System.currentTimeMillis()
        ticker?.cancel()
        ticker = scope.launch {
            while (isActive) {
                tick()
                delay(250)
            }
        }
    }

    private fun tick() {
        val info = info ?: return
        val seconds = player.currentPosition / 1000.0
        position = seconds
        if (player.duration > 0) duration = player.duration / 1000.0
        val next = info.prompt(seconds)
        // Skip intros and recaps by themselves when the profile asks, once each.
        if (next is PlaybackPrompt.Skip && (if (next.type == MarkerType.Intro) preferences.skipIntros else preferences.skipRecaps) &&
            autoSkipped.add("${next.type.raw}@${next.to}")
        ) {
            seekTo(next.to)
            return
        }
        // Skip credits: the episode is over as soon as they start.
        val credits = info.markers.firstOrNull { it.type == MarkerType.Credits }
        if (preferences.skipCredits && info.next != null && !nextCancelled && !isFinishing && credits != null && seconds >= credits.start) {
            player.pause()
            report(paused = true, ended = true)
            beginFinishing()
        }
        if (next != prompt) {
            prompt = next
            updatePrompt()
        }
        if (System.currentTimeMillis() - lastReport >= REPORT_INTERVAL_MS && player.isPlaying) report(paused = false)
    }

    fun togglePlayPause() = if (player.isPlaying) player.pause() else player.play()

    fun skip(seconds: Int) = seekTo((player.currentPosition / 1000.0 + seconds).coerceAtLeast(0.0))

    fun seekTo(seconds: Double) {
        player.seekTo((seconds * 1000).toLong())
        position = seconds
    }

    fun playNext() {
        val next = info?.next ?: return
        report(paused = true)
        start(next.id, fromBeginning = false)
    }

    fun playPrevious() {
        val previous = info?.previous ?: return
        report(paused = true)
        start(previous.id, fromBeginning = false)
    }

    fun promptTapped() {
        if (isFinishing) return playNext()
        when (val current = prompt) {
            is PlaybackPrompt.Skip -> seekTo(current.to)
            PlaybackPrompt.NextEpisode -> playNext()
            PlaybackPrompt.None -> Unit
        }
    }

    /** Not now: stop counting, and if the credits were skipped, let them play. */
    fun cancelNext() {
        nextCancelled = true
        val ended = player.playbackState == Player.STATE_ENDED
        isFinishing = false
        countdown = null
        countdownJob?.cancel()
        updatePrompt()
        if (!ended) player.play()
    }

    private fun itemEnded() {
        report(paused = true, ended = true)
        if (info?.next != null && !nextCancelled) beginFinishing() else onClose()
    }

    /** The episode is over: count down to the next one if auto-play is on, else wait to be asked. */
    private fun beginFinishing() {
        if (info?.next == null || isFinishing) return
        isFinishing = true
        countdownJob?.cancel()
        if (preferences.autoPlayNext) {
            countdown = COUNTDOWN_SECONDS
            countdownJob = scope.launch {
                while ((countdown ?: 0) > 0) {
                    updatePrompt()
                    delay(1_000)
                    if (!isFinishing) return@launch
                    countdown = (countdown ?: 1) - 1
                }
                if (isFinishing) playNext()
            }
        }
        updatePrompt()
    }

    private fun updatePrompt() {
        // "Next Episode in 4" (on shuffle too: the next one just happens to be random).
        val nextTitle = countdown?.let { "Next Episode in $it" } ?: "Next Episode"
        promptTitle = if (isFinishing) nextTitle else when (val current = prompt) {
            PlaybackPrompt.None -> null
            is PlaybackPrompt.Skip -> if (current.type == MarkerType.Recap) "Skip Recap" else "Skip Intro"
            PlaybackPrompt.NextEpisode -> nextTitle
        }
        countingDown = isFinishing && countdown != null
    }

    private fun report(paused: Boolean, ended: Boolean = false) {
        val session = info?.sessionId ?: return
        lastReport = System.currentTimeMillis()
        if (!model.recordsHistory) return
        val at = player.currentPosition / 1000.0
        scope.launch { runCatching { client.reportProgress(session, at, paused, ended) } }
    }

    /** The player closed: the last position, then the session ends (finishing even though the screen has gone). */
    fun close() {
        ticker?.cancel()
        countdownJob?.cancel()
        val session = info?.sessionId
        val at = player.currentPosition / 1000.0
        player.release()
        if (session != null) {
            scope.launch {
                if (model.recordsHistory) runCatching { client.reportProgress(session, at, paused = true) }
                runCatching { client.stopPlayback(session) }
                model.watchStateChanged()
            }
        }
    }

    private companion object {
        const val SKIP_MS = 10_000L
        const val REPORT_INTERVAL_MS = 10_000L
        const val COUNTDOWN_SECONDS = 5
    }
}
