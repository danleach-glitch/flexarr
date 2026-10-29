package io.github.flexarr.player

import android.content.Context
import android.util.Log
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableDoubleStateOf
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.media3.common.AudioAttributes
import androidx.media3.common.C
import androidx.media3.common.MediaItem
import androidx.media3.common.MimeTypes
import androidx.media3.common.PlaybackException
import androidx.media3.common.Player
import androidx.media3.common.TrackSelectionOverride
import androidx.media3.common.Tracks
import androidx.media3.common.VideoSize
import androidx.media3.common.text.Cue
import androidx.media3.common.text.CueGroup
import androidx.media3.exoplayer.ExoPlayer
import io.github.flexarr.AppModel
import io.github.flexarr.core.DeviceProfile
import io.github.flexarr.core.FlexarrClient
import io.github.flexarr.core.MarkerType
import io.github.flexarr.core.MediaKind
import io.github.flexarr.core.PlaybackInfo
import io.github.flexarr.core.PlaybackMethod
import io.github.flexarr.core.PlaybackPreferences
import io.github.flexarr.core.PlaybackPrompt
import io.github.flexarr.core.PlaybackRequest
import io.github.flexarr.core.SubtitlePosition
import io.github.flexarr.core.SubtitleSize
import io.github.flexarr.core.SubtitleTrack
import io.github.flexarr.core.prompt
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlin.math.abs
import kotlin.math.ceil

/**
 * One title playing: asks the server for a session, plays its stream with ExoPlayer, reports progress, offers Skip
 * Intro and Next Episode, and moves on to the next episode. The audio, subtitle, speed and sleep-timer menus act
 * through it. The iPhone app's PlaybackController, for the phone.
 */
class PlaybackController(context: Context, private val model: AppModel, private val client: FlexarrClient, private val onClose: () -> Unit) {
    val player: ExoPlayer = ExoPlayer.Builder(context)
        .setAudioAttributes(AudioAttributes.Builder().setUsage(C.USAGE_MEDIA).setContentType(C.AUDIO_CONTENT_TYPE_MOVIE).build(), true)
        .setHandleAudioBecomingNoisy(true)
        .setSeekBackIncrementMs(SKIP_MS)
        .setSeekForwardIncrementMs(SKIP_MS)
        .build()
    private val deviceProfile = DeviceCapabilities.profile(context)
    /** [deviceProfile] once the server's version is known, which decides whether it can have fragmented MP4. */
    private var serverProfile: DeviceProfile? = null
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

    // The menus' state.

    /** The subtitle track showing (its key), or null for none. A burned-in one is the server's [PlaybackInfo.selectedSubtitleKey]. */
    var subtitleKey: String? by mutableStateOf(null)
        private set
    var subtitleSize by mutableStateOf(SubtitleSize.Standard)
        private set
    var subtitlePosition by mutableStateOf(SubtitlePosition.Bottom)
        private set
    var subtitlesInBlackBars by mutableStateOf(false)
        private set
    /** What the subtitles say now, drawn by the screen over the picture (or in the black bars). */
    var cues: List<Cue> by mutableStateOf(emptyList())
        private set
    var videoSize: VideoSize by mutableStateOf(VideoSize.UNKNOWN)
        private set
    var rate by mutableFloatStateOf(1f)
        private set
    /** The file's sound offset for Audio sync (null: not adjustable, as with an older server). */
    var audioSync: Double? by mutableStateOf(null)
        private set
    /** Opening the stream again (another audio track, moved subtitles): the menus wait for it. */
    var isReopening by mutableStateOf(false)
        private set

    // The sleep timer: it lasts as long as the screen, so a timer of minutes carries on into the next episode.

    enum class SleepPhase {
        Off,
        /** The last minute: "Still watching?" over the dimmed picture. */
        Asking,
        /** Ran out: paused, dimmed until someone touches something. */
        Asleep,
    }

    val sleep = SleepTimer(scope).also { it.onExpire = { fallAsleep() } }
    var sleepPhase by mutableStateOf(SleepPhase.Off)
        private set
    /** "Pausing in 0:42" while asking. */
    var sleepDetail by mutableStateOf("")
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
    /** The viewer's own subtitle pick this session ("off" as null), which a reopened stream keeps. */
    private var viewerSubtitleKey: String? = null
    private var viewerChoseSubtitle = false
    /** The speed to carry into a stream opened again for the same title. */
    private var carriedRate: Float? = null
    /** The sideloaded subtitle still to be turned on once the player has read its tracks. */
    private var pendingSubtitle: String? = null
    private var audioDelaySave: Job? = null
    private var lastRecovery = 0L

    init {
        player.addListener(object : Player.Listener {
            override fun onIsPlayingChanged(playing: Boolean) {
                isPlaying = playing
                // Paused or playing again: the server hears straight away.
                if (info != null && player.playbackState == Player.STATE_READY) report(paused = !playing)
                // Played again from elsewhere (headphones, say) after the sleep timer ran out: awake.
                if (playing && sleepPhase == SleepPhase.Asleep) wake()
            }

            override fun onPlaybackStateChanged(state: Int) {
                isBuffering = state == Player.STATE_BUFFERING
                if (state == Player.STATE_ENDED) itemEnded()
            }

            override fun onPlayerError(e: PlaybackException) {
                // A converted stream that stopped (the server restarted, or forgot the conversion after a long jump):
                // a fresh session from the same place, once in a while, before giving up.
                val now = System.currentTimeMillis()
                if (info?.method == PlaybackMethod.Transcode && e.errorCode == PlaybackException.ERROR_CODE_IO_BAD_HTTP_STATUS && now - lastRecovery > RECOVERY_INTERVAL_MS) {
                    lastRecovery = now
                    Log.w("Flexarr", "Converted stream failed; opening it again", e)
                    reopen()
                    return
                }
                error = "This couldn’t be played. ${e.errorCodeName.removePrefix("ERROR_CODE_").replace('_', ' ').lowercase()}."
            }

            override fun onTracksChanged(tracks: Tracks) {
                pendingSubtitle?.let { if (applySubtitle(it)) pendingSubtitle = null }
            }

            override fun onCues(cueGroup: CueGroup) {
                cues = cueGroup.cues
            }

            override fun onVideoSizeChanged(size: VideoSize) {
                videoSize = size
            }
        })
        // Subtitles are chosen here, not by the device's language.
        player.trackSelectionParameters = player.trackSelectionParameters.buildUpon().setTrackTypeDisabled(C.TRACK_TYPE_TEXT, true).build()
    }

    /** Asks the server for [itemId] (or a random episode of a show, with [shuffle]) and starts it. */
    fun start(itemId: String, fromBeginning: Boolean = false, shuffle: Boolean = false) {
        // A podcast playing stops for the video.
        model.podcasts.pause()
        // Another title: the viewer's subtitle pick and speed were for the last one.
        viewerChoseSubtitle = false
        viewerSubtitleKey = null
        open { profile ->
            PlaybackRequest(
                itemId = itemId,
                profile = profile,
                startPositionSeconds = if (fromBeginning) 0.0 else null,
                replacesSessionId = info?.sessionId,
                shuffle = if (shuffle) true else null,
            )
        }
    }

    /** Asks the server for a session for [request] (given this device's profile) and starts it. */
    private fun open(reopening: Boolean = false, request: (DeviceProfile) -> PlaybackRequest) {
        scope.launch {
            if (reopening) isReopening = true
            try {
                // Whether this server can have fragmented MP4 depends on its version, asked once.
                val profile = serverProfile ?: DeviceCapabilities.withServer(deviceProfile, runCatching { client.systemStatus().version }.getOrNull())
                    .also { serverProfile = it }
                begin(client.startPlayback(request(profile)))
            } catch (e: Exception) {
                if (reopening) Log.w("Flexarr", "Couldn't open the stream again", e) else error = e.message
            } finally {
                isReopening = false
            }
        }
    }

    /** The same title opened again where it is: another audio track, the viewer's subtitles, or moved sound or subtitles. */
    private fun reopen(audioIndex: Int? = null) {
        val current = info ?: return
        carriedRate = rate
        report(paused = true)
        val at = player.currentPosition / 1000.0
        open(reopening = true) { profile ->
            PlaybackRequest(
                itemId = current.item.id,
                profile = profile,
                startPositionSeconds = at,
                replacesSessionId = current.sessionId,
                fileId = current.fileId,
                audioStreamIndex = audioIndex ?: current.selectedAudioIndex,
                // The viewer's pick ("off" included) wins; otherwise what's showing, and the server decides.
                subtitleKey = if (viewerChoseSubtitle) viewerSubtitleKey ?: "off" else current.selectedSubtitleKey,
            )
        }
    }

    private fun begin(started: PlaybackInfo) {
        val sameTitle = info?.item?.id == started.item.id
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
        subtitleSize = preferences.subtitleSize ?: SubtitleSize.Standard
        subtitlePosition = preferences.subtitlePosition ?: SubtitlePosition.Bottom
        subtitlesInBlackBars = preferences.subtitlesInBlackBars ?: false
        audioSync = if (started.hasPlayerControls) started.audioDelaySeconds else null
        // Moved on to something that isn't an episode: there's no end of episode to stop at.
        if (started.item.kind != MediaKind.Episode && sleep.setting == SleepTimer.Setting.EndOfEpisode) sleep.set(null)

        // Text subtitles ride beside the stream, each known by its key; picture ones come burned in.
        val sideloaded = started.subtitles.filter { it.isText && it.url != null }.map { track ->
            MediaItem.SubtitleConfiguration.Builder(android.net.Uri.parse(client.url(track.url!!)))
                .setId(SUBTITLE_ID_PREFIX + track.key)
                .setMimeType(MimeTypes.TEXT_VTT)
                .setLanguage(track.language)
                .setLabel(track.label)
                .build()
        }
        val media = MediaItem.Builder()
            .setUri(client.url(started.url))
            .setSubtitleConfigurations(sideloaded)
            .apply { if (started.method == PlaybackMethod.Transcode) setMimeType(MimeTypes.APPLICATION_M3U8) }
            .build()
        val shown = when {
            started.subtitleBurnedIn -> null
            viewerChoseSubtitle -> viewerSubtitleKey
            else -> started.selectedSubtitleKey
        }?.takeIf { key -> started.subtitles.any { it.key == key && it.isText } }
        subtitleKey = if (started.subtitleBurnedIn) started.selectedSubtitleKey else shown
        pendingSubtitle = null
        disableSubtitles()
        if (shown != null) pendingSubtitle = shown

        player.setMediaItem(media, (started.startPositionSeconds * 1000).toLong())
        player.prepare()
        // The speed it was at before its stream was opened again, or the show's remembered one.
        val startRate = (if (sameTitle) carriedRate else null) ?: started.speed?.toFloat() ?: 1f
        carriedRate = null
        setPlayerRate(startRate)
        // Asleep (End of episode stopped the last one): stay paused under the dim.
        if (sleepPhase != SleepPhase.Asleep) player.play()
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
        checkSleep()
        val next = info.prompt(seconds)
        // Skip intros and recaps by themselves when the profile asks, once each.
        if (next is PlaybackPrompt.Skip && (if (next.type == MarkerType.Intro) preferences.skipIntros else preferences.skipRecaps) &&
            autoSkipped.add("${next.type.raw}@${next.to}")
        ) {
            seekTo(next.to)
            return
        }
        // Skip credits: the episode is over as soon as they start (unless the sleep timer stops it there).
        val credits = info.markers.firstOrNull { it.type == MarkerType.Credits }
        if (preferences.skipCredits && info.next != null && !nextCancelled && !isFinishing && !sleepHoldsEpisode && credits != null && seconds >= credits.start) {
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
        when {
            sleepHoldsEpisode -> fallAsleep()
            info?.next != null && !nextCancelled -> beginFinishing()
            else -> onClose()
        }
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
            // The sleep timer stops this episode at its end: there's no next one to offer.
            PlaybackPrompt.NextEpisode -> if (sleepHoldsEpisode) null else nextTitle
        }
        countingDown = isFinishing && countdown != null
    }

    // Audio

    /** Another of the file's audio tracks: the server opens the stream again with it. */
    fun selectAudio(index: Int) {
        val current = info ?: return
        if (index == current.selectedAudioIndex || isReopening) return
        reopen(audioIndex = index)
    }

    /**
     * Moves the file's sound later (earlier when negative) for everyone. Saved once the nudging has stopped for a
     * second, then the stream is opened again with the sound moved (the server moves it; this player can't).
     */
    fun setAudioDelay(seconds: Double) {
        val current = info ?: return
        if (!current.hasPlayerControls) return
        val clamped = (Math.round(seconds * 1000) / 1000.0).coerceIn(-AUDIO_SYNC_LIMIT, AUDIO_SYNC_LIMIT)
        audioSync = clamped
        val itemId = current.item.id
        audioDelaySave?.cancel()
        audioDelaySave = scope.launch {
            delay(1_000)
            try {
                client.setAudioDelay(itemId, clamped)
            } catch (e: Exception) {
                Log.w("Flexarr", "Couldn't save the audio sync", e)
                return@launch
            }
            while (isReopening) delay(200)
            val now = info ?: return@launch
            if (now.item.id != itemId) return@launch
            val inStream = if (now.audioDelayInStream == true) now.audioDelaySeconds ?: 0.0 else 0.0
            if (abs(inStream - clamped) > 0.0005) reopen()
        }
    }

    // Subtitles

    /** Subtitles chosen in the menu (null for off). The server remembers the pick for this title and its show. */
    fun selectSubtitle(track: SubtitleTrack?) {
        val current = info ?: return
        if (isReopening) return
        viewerChoseSubtitle = true
        viewerSubtitleKey = track?.key
        subtitleKey = track?.key
        scope.launch { runCatching { client.setSubtitle(current.sessionId, track?.key) } }
        when {
            // Picture-based ones are burned into the picture by the server; and one burned in now has to go.
            track != null && !track.isText -> reopen()
            current.subtitleBurnedIn -> reopen()
            track == null -> {
                pendingSubtitle = null
                disableSubtitles()
            }
            !applySubtitle(track.key) -> pendingSubtitle = track.key
        }
    }

    /** Turns on the sideloaded track for [key]; false if the player hasn't read its tracks yet. */
    private fun applySubtitle(key: String): Boolean {
        val group = player.currentTracks.groups.firstOrNull { group ->
            group.type == C.TRACK_TYPE_TEXT && (0 until group.length).any { group.getTrackFormat(it).id?.endsWith(SUBTITLE_ID_PREFIX + key) == true }
        } ?: return false
        val index = (0 until group.length).first { group.getTrackFormat(it).id?.endsWith(SUBTITLE_ID_PREFIX + key) == true }
        player.trackSelectionParameters = player.trackSelectionParameters.buildUpon()
            .setTrackTypeDisabled(C.TRACK_TYPE_TEXT, false)
            .setOverrideForType(TrackSelectionOverride(group.mediaTrackGroup, index))
            .build()
        return true
    }

    private fun disableSubtitles() {
        player.trackSelectionParameters = player.trackSelectionParameters.buildUpon()
            .clearOverridesOfType(C.TRACK_TYPE_TEXT)
            .setTrackTypeDisabled(C.TRACK_TYPE_TEXT, true)
            .build()
        cues = emptyList()
    }

    /** Shows this title's subtitles later (or earlier): remembered on the server, which shifts them, and fetched anew. */
    fun setSubtitleDelay(seconds: Double) {
        val current = info ?: return
        if (isReopening || seconds == (current.subtitleDelaySeconds ?: 0.0)) return
        scope.launch {
            try {
                client.setSubtitleDelay(current.item.id, seconds)
            } catch (e: Exception) {
                Log.w("Flexarr", "Couldn't move subtitles", e)
                return@launch
            }
            if (info?.sessionId == current.sessionId) reopen()
        }
    }

    /** The profile's subtitle size, changed while watching: drawn at once, and saved for every device. */
    fun chooseSubtitleSize(size: SubtitleSize) {
        subtitleSize = size
        preferences = preferences.copy(subtitleSize = size)
        savePreferences()
    }

    /** Where the profile's subtitles sit, changed while watching: drawn at once, and saved for every device. */
    fun setSubtitlePlacement(position: SubtitlePosition, inBlackBars: Boolean) {
        subtitlePosition = position
        subtitlesInBlackBars = inBlackBars
        preferences = preferences.copy(subtitlePosition = position, subtitlesInBlackBars = inBlackBars)
        savePreferences()
    }

    private fun savePreferences() {
        val saved = preferences
        scope.launch { runCatching { client.setPlaybackPreferences(saved) } }
    }

    // Speed

    /** A speed chosen in the menu. During an episode it's kept for its show (for this profile); a film's is for now. */
    fun chooseRate(rate: Float) {
        if (rate == this.rate) return
        setPlayerRate(rate)
        val current = info ?: return
        if (!current.hasPlayerControls || current.item.kind != MediaKind.Episode) return
        scope.launch {
            runCatching { client.setPlaybackSpeed(current.item.id, rate.toDouble()) }
                .onFailure { Log.w("Flexarr", "Couldn't remember the show's speed", it) }
        }
    }

    private fun setPlayerRate(rate: Float) {
        this.rate = rate.coerceIn(0.25f, MAX_RATE)
        player.setPlaybackSpeed(this.rate)
    }

    // Sleep timer

    /** The sleep timer has this episode stopping at its end, or has run out: no going on to the next by itself. */
    val sleepHoldsEpisode: Boolean
        get() = sleep.setting == SleepTimer.Setting.EndOfEpisode || sleepPhase != SleepPhase.Off

    /** End of episode is offered for episodes only. */
    val offersEndOfEpisode: Boolean get() = info?.item?.kind == MediaKind.Episode

    /** The viewer chose a timer (or Off, or Cancel timer) in the menu or the question. */
    fun setSleep(setting: SleepTimer.Setting?) {
        sleep.set(setting)
        if (sleepPhase == SleepPhase.Asking) endAsking()
        updatePrompt()
    }

    /** "+15 min": fifteen minutes from now, whatever the timer was (End of episode included). */
    fun extendSleep() = setSleep(SleepTimer.Setting.Minutes(15))

    /**
     * Checked a few times a second while it runs: the question in the last minute (or as End of episode's credits
     * start), the sound fading over the last ten seconds, and the stop.
     */
    private fun checkSleep() {
        if (sleepPhase == SleepPhase.Asleep) return
        val setting = sleep.setting
        if (setting == null) {
            if (sleepPhase == SleepPhase.Asking) endAsking()
            return
        }
        val left: Double
        val asking: Boolean
        when (setting) {
            is SleepTimer.Setting.Minutes -> {
                left = sleep.remaining ?: 0.0
                asking = left <= 60
            }
            SleepTimer.Setting.EndOfEpisode -> {
                val info = info ?: return
                if (duration <= 0) return
                // At the credits when the profile skips them (and they're marked), else the very end; the question
                // comes as the credits start, or a minute before the end.
                val credits = info.markers.firstOrNull { it.type == MarkerType.Credits }?.start
                val stopsAtCredits = preferences.skipCredits && info.next != null
                val stop = if (stopsAtCredits) credits ?: duration else duration
                val question = if (stopsAtCredits) stop - 60 else credits ?: (stop - 60)
                left = (stop - position) / rate.coerceAtLeast(0.25f)
                if (left <= 0.3) return fallAsleep()
                asking = position >= minOf(question, stop - 10)
            }
        }
        if (asking) {
            if (sleepPhase == SleepPhase.Off) {
                sleepPhase = SleepPhase.Asking
                updatePrompt()
            }
            sleepDetail = "Pausing in " + io.github.flexarr.core.Format.timestamp(ceil(left))
            player.volume = (left / 10).toFloat().coerceIn(0f, 1f)
        } else if (sleepPhase == SleepPhase.Asking) {
            endAsking()
        }
    }

    /** Back to watching: the question and the dim go, the sound comes back up. */
    private fun endAsking() {
        sleepPhase = SleepPhase.Off
        player.volume = 1f
        updatePrompt()
    }

    /** Run out: the picture pauses (its place reported as for any pause) and stays dimmed until someone's there. */
    private fun fallAsleep() {
        if (sleepPhase == SleepPhase.Asleep) return
        if (sleep.setting != null) sleep.set(null)
        sleepPhase = SleepPhase.Asleep
        player.pause()
        updatePrompt()
    }

    /** Someone's there after the timer ran out: the dim goes (still paused). */
    fun wake() {
        if (sleepPhase != SleepPhase.Asleep) return
        sleepPhase = SleepPhase.Off
        player.volume = 1f
        updatePrompt()
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
        sleep.set(null)
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

    companion object {
        private const val SKIP_MS = 10_000L
        private const val REPORT_INTERVAL_MS = 10_000L
        private const val COUNTDOWN_SECONDS = 5
        private const val SUBTITLE_ID_PREFIX = "flexarr-subtitle:"
        private const val RECOVERY_INTERVAL_MS = 30_000L
        const val MAX_RATE = 2f
        const val AUDIO_SYNC_STEP = 0.05
        const val AUDIO_SYNC_LIMIT = 2.0
        /** The timings offered for subtitles: a pick opens the stream again (a second or two) with them moved. */
        val delayChoices = listOf(-2.0, -1.0, -0.5, 0.0, 0.5, 1.0, 2.0)
        val rateChoices = listOf(0.5f, 0.75f, 1f, 1.25f, 1.5f, 2f)

        /** "In time", "0.5s later", "1s earlier". */
        fun delayLabel(seconds: Double): String {
            if (seconds == 0.0) return "In time"
            val amount = abs(seconds).let { if (it == Math.floor(it)) it.toInt().toString() else it.toString().trimEnd('0') }
            return "${amount}s ${if (seconds > 0) "later" else "earlier"}"
        }

        fun rateLabel(rate: Float): String {
            if (rate == 1f) return "Normal"
            val text = if (rate == Math.floor(rate.toDouble()).toFloat()) rate.toInt().toString() else rate.toString().trimEnd('0')
            return "$text×"
        }

        /** "+0.15 s", "−0.30 s", "0.00 s". */
        fun syncLabel(seconds: Double): String {
            val amount = String.format(java.util.Locale.US, "%.2f s", abs(seconds))
            return if (seconds > 0.0005) "+$amount" else if (seconds < -0.0005) "−$amount" else amount
        }
    }
}
