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
import io.github.flexarr.core.SubtitleSyncState
import io.github.flexarr.core.SubtitleTrack
import io.github.flexarr.core.DownloadedSubtitles
import io.github.flexarr.core.FlexarrException
import io.github.flexarr.core.prompt
import io.github.flexarr.core.startSubtitleSync
import io.github.flexarr.core.subtitleSync
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
class PlaybackController(context: Context, private val model: AppModel, val client: FlexarrClient, private val onClose: () -> Unit) {
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
    /** A moment's message in the middle of the picture ("Re-syncing subtitles…"), counted so the same one shows again. */
    var flash: Pair<String, Int>? by mutableStateOf(null)
        private set
    /** Searching for subtitles over the picture (paused meanwhile): the file searched for. */
    var subtitleSearchFile: String? by mutableStateOf(null)
        private set
    /** This session's scrubbing previews, once the server has made them. */
    var previews: TrickplayPreviews? by mutableStateOf(null)
        private set
    /** A re-sync of this title's subtitle file to its audio is under way. */
    var isResyncing by mutableStateOf(false)
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
    /** This title's file couldn't be decoded here: it's converted from now on (until another title starts). */
    private var forcedTranscode = false

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
                // The file played as it is, and this device's decoder couldn't cope after all (it claimed a codec it
                // can't do in practice, say): once, the server's converted stream instead, from the same place.
                if (info?.method == PlaybackMethod.DirectPlay && !forcedTranscode && e.errorCode in DECODER_ERRORS) {
                    forcedTranscode = true
                    Log.w("Flexarr", "Couldn't decode the file; asking for it converted", e)
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

    /**
     * Asks the server for [itemId] (or a random episode of a show, with [shuffle]) and starts it. [subtitlesOff]: the
     * viewer turned subtitles off in the last episode, which carries on into this one.
     */
    fun start(itemId: String, fromBeginning: Boolean = false, shuffle: Boolean = false, startAt: Double? = null, subtitlesOff: Boolean = false) {
        newTitle(subtitlesOff)
        open { profile ->
            PlaybackRequest(
                itemId = itemId,
                profile = profile,
                startPositionSeconds = startAt ?: if (fromBeginning) 0.0 else null,
                replacesSessionId = info?.sessionId,
                shuffle = if (shuffle) true else null,
                subtitleKey = if (subtitlesOff) "off" else viewerSubtitleKey,
            )
        }
    }

    /** Another title starts: the viewer's subtitle pick and speed were for the last one (a debug launch's pick holds once). */
    private fun newTitle(subtitlesOff: Boolean) {
        // A podcast playing stops for the video.
        model.podcasts.pause()
        forcedTranscode = false
        viewerChoseSubtitle = debugSubtitleKey != null || subtitlesOff
        viewerSubtitleKey = debugSubtitleKey
        debugSubtitleKey = null
    }

    private var debugSubtitleKey: String? = null
    private var debugPlacement: Triple<SubtitlePosition?, SubtitleSize?, Boolean?>? = null

    /** Debug launches: subtitles as if chosen in the menu (not saved), so a test can look at them straight away. */
    fun debugSubtitles(key: String?, position: SubtitlePosition?, size: SubtitleSize?, inBlackBars: Boolean?) {
        debugSubtitleKey = key
        if (position != null || size != null || inBlackBars != null) debugPlacement = Triple(position, size, inBlackBars)
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
                forceTranscode = if (forcedTranscode) true else null,
            )
        }
    }

    private fun begin(started: PlaybackInfo) {
        val sameTitle = info?.item?.id == started.item.id
        previews?.close()
        previews = TrickplayPreviews(client, started.sessionId, scope)
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
        debugPlacement?.let { (position, size, inBlackBars) ->
            position?.let { subtitlePosition = it }
            size?.let { subtitleSize = it }
            inBlackBars?.let { subtitlesInBlackBars = it }
        }
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
            // The credits started: get the next episode ready. Back out of them: let it go.
            if (next == PlaybackPrompt.NextEpisode) {
                if (!sleepHoldsEpisode) prepareNext()
            } else if (!isFinishing) {
                dropPrepared()
            }
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
        val current = info ?: return
        val next = current.next ?: return
        report(paused = true)
        val subtitlesOff = nextSubtitlesOff(current)
        val ready = prepared
        if (ready != null && ready.info.item.id == next.id && ready.subtitlesOff == subtitlesOff) {
            // Got ready during the credits: this session ends, and the next one opens straight away.
            Log.i("Flexarr", "Using the next episode got ready during the credits")
            prepared = null
            scope.launch { runCatching { client.stopPlayback(current.sessionId) } }
            newTitle(subtitlesOff)
            begin(ready.info)
            return
        }
        dropPrepared()
        start(next.id, fromBeginning = false, subtitlesOff = subtitlesOff)
    }

    /**
     * Subtitles turned off carry on to the next episode; a chosen track is the server's to match there. Forced ones
     * (foreign dialogue) left on count as off.
     */
    private fun nextSubtitlesOff(current: PlaybackInfo): Boolean {
        if (!viewerChoseSubtitle) return false
        val key = viewerSubtitleKey ?: return true
        return current.subtitles.any { it.key == key && it.isForced }
    }

    // The next episode, got ready during the credits.

    private class Prepared(val info: PlaybackInfo, val subtitlesOff: Boolean)

    private var prepared: Prepared? = null
    private var preparing: Job? = null

    /**
     * Starts the next episode's session now, during the credits, so it opens at once when its turn comes. A busy
     * server says no, and then it starts the usual way.
     */
    private fun prepareNext() {
        val current = info ?: return
        val next = current.next ?: return
        if (nextCancelled || prepared?.info?.item?.id == next.id || preparing != null) return
        val subtitlesOff = nextSubtitlesOff(current)
        preparing = scope.launch {
            try {
                val profile = serverProfile ?: return@launch
                val ready = runCatching {
                    client.startPlayback(PlaybackRequest(itemId = next.id, profile = profile, subtitleKey = if (subtitlesOff) "off" else null, prepare = true))
                }.getOrNull() ?: return@launch
                // Still wanted? (Not if this episode was closed, or moved on from, meanwhile.)
                if (info?.sessionId != current.sessionId || nextCancelled) {
                    runCatching { client.stopPlayback(ready.sessionId) }
                    return@launch
                }
                Log.i("Flexarr", "Next episode ready: ${ready.item.title}")
                prepared = Prepared(ready, subtitlesOff)
            } finally {
                preparing = null
            }
        }
    }

    /** Lets the prepared session go (best effort): it wasn't wanted, or playback ended. */
    private fun dropPrepared() {
        preparing?.cancel()
        preparing = null
        val ready = prepared ?: return
        prepared = null
        scope.launch { runCatching { client.stopPlayback(ready.info.sessionId) } }
    }

    fun playPrevious() {
        val previous = info?.previous ?: return
        report(paused = true)
        dropPrepared()
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
        dropPrepared()
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
        prepareNext()
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
        if (model.recordsHistory) scope.launch { runCatching { client.setSubtitle(current.sessionId, track?.key) } }
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
        if (!model.recordsHistory) return
        val saved = preferences
        scope.launch { runCatching { client.setPlaybackPreferences(saved) } }
    }

    // Finding and re-syncing subtitles

    /** The menu offers Search for subtitles: a server with Bazarr, for a title it manages. */
    val canSearchSubtitles: Boolean get() = info?.canSearchSubtitles == true && info?.fileId != null

    /** The subtitle file beside the video that's showing, which Re-sync to audio lines up; null for one inside it. */
    val resyncableSubtitle: String?
        get() = subtitleKey?.takeIf { key -> info?.subtitles?.any { it.key == key && it.isExternal == true } == true }

    private var wasPlayingBeforeSearch = false
    private var resync: Job? = null

    /** The search over the picture, paused meanwhile. */
    fun searchSubtitles() {
        val current = info ?: return
        if (subtitleSearchFile != null || !canSearchSubtitles) return
        wasPlayingBeforeSearch = player.isPlaying
        player.pause()
        subtitleSearchFile = current.fileId
    }

    /** Subtitles chosen there are saved beside the file, and the title opens again where it was with them on. */
    fun subtitlesDownloaded(saved: DownloadedSubtitles) {
        subtitleSearchFile = null
        Log.i("Flexarr", "Subtitles downloaded: ${saved.label} (${saved.key})")
        viewerChoseSubtitle = true
        viewerSubtitleKey = saved.key
        reopen()
    }

    /** Closed with Done: playing carries on if it was. */
    fun closeSubtitleSearch() {
        subtitleSearchFile = null
        if (wasPlayingBeforeSearch) player.play()
    }

    /**
     * Asks the server to line the subtitle file [key] up with the audio, then follows it: when it's done the title
     * opens again where it is (the file has changed), when it fails the viewer is told why. Playing carries on.
     */
    fun resyncSubtitles(key: String) {
        val current = info ?: return
        val itemId = current.item.id
        val fileId = current.fileId ?: return
        if (isResyncing) return
        isResyncing = true
        resync = scope.launch {
            try {
                try {
                    client.startSubtitleSync(itemId, fileId, key)
                } catch (e: FlexarrException.Http) {
                    // One is already running for this file: follow that.
                    if (e.status != 409) throw e
                }
                showFlash("Re-syncing subtitles…")
                var misses = 0
                // A film's re-sync takes a minute or more; given up on after half an hour.
                repeat(600) {
                    delay(3_000)
                    val status = try {
                        client.subtitleSync(itemId, fileId).also { misses = 0 }
                    } catch (e: FlexarrException) {
                        if (++misses >= 5) throw e
                        return@repeat
                    }
                    when (status.state) {
                        SubtitleSyncState.Running, SubtitleSyncState.Unknown -> return@repeat
                        SubtitleSyncState.Done -> return@launch resyncDone(itemId, fileId, status.key ?: key)
                        SubtitleSyncState.Failed -> return@launch resyncFailed(status.error ?: "The subtitles couldn’t be re-synced.")
                        SubtitleSyncState.Idle -> return@launch resyncFailed("The re-sync stopped before it finished.")
                    }
                }
                resyncFailed("The re-sync is taking too long. Try again later.")
            } catch (e: FlexarrException) {
                resyncFailed(e.message ?: "The subtitles couldn’t be re-synced.")
            }
        }
    }

    /** The re-sync finished: still watching that file, it opens again where it is with the re-synced subtitles on. */
    private fun resyncDone(itemId: String, fileId: String, key: String) {
        isResyncing = false
        val current = info ?: return
        if (current.item.id != itemId || current.fileId != fileId) return
        showFlash("Subtitles re-synced")
        viewerChoseSubtitle = true
        viewerSubtitleKey = key
        reopen()
    }

    private fun resyncFailed(message: String) {
        isResyncing = false
        showFlash(message)
    }

    private fun showFlash(text: String) {
        flash = text to ((flash?.second ?: 0) + 1)
    }

    // Speed

    /** A speed chosen in the menu. During an episode it's kept for its show (for this profile); a film's is for now. */
    fun chooseRate(rate: Float) {
        if (rate == this.rate) return
        setPlayerRate(rate)
        val current = info ?: return
        if (!current.hasPlayerControls || current.item.kind != MediaKind.Episode || !model.recordsHistory) return
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
        resync?.cancel()
        previews?.close()
        dropPrepared()
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
        private val DECODER_ERRORS = setOf(
            PlaybackException.ERROR_CODE_DECODING_FAILED,
            PlaybackException.ERROR_CODE_DECODER_INIT_FAILED,
            PlaybackException.ERROR_CODE_DECODER_QUERY_FAILED,
            PlaybackException.ERROR_CODE_DECODING_FORMAT_UNSUPPORTED,
            PlaybackException.ERROR_CODE_DECODING_FORMAT_EXCEEDS_CAPABILITIES,
        )
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
