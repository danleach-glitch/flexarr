package io.github.flexarr.cast

import android.annotation.SuppressLint
import android.content.Context
import android.net.Uri
import android.widget.Toast
import androidx.annotation.OptIn
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableDoubleStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.runtime.snapshotFlow
import androidx.media3.cast.MediaItemConverter
import androidx.media3.cast.RemoteCastPlayer
import androidx.media3.common.MediaItem
import androidx.media3.common.MimeTypes
import androidx.media3.common.PlaybackException
import androidx.media3.common.PlaybackParameters
import androidx.media3.common.Player
import androidx.media3.common.util.UnstableApi
import com.google.android.gms.cast.HlsSegmentFormat
import com.google.android.gms.cast.HlsVideoSegmentFormat
import com.google.android.gms.cast.MediaInfo
import com.google.android.gms.cast.MediaQueueItem
import com.google.android.gms.cast.framework.CastContext
import com.google.android.gms.cast.framework.CastSession
import com.google.android.gms.cast.framework.SessionManagerListener
import com.google.android.gms.common.images.WebImage
import io.github.flexarr.AppModel
import io.github.flexarr.audio.MusicPlayer
import io.github.flexarr.core.DeviceProfile
import io.github.flexarr.core.FlexarrClient
import io.github.flexarr.core.ItemSummary
import io.github.flexarr.core.MediaKind
import io.github.flexarr.core.PlaybackInfo
import io.github.flexarr.core.PlaybackMethod
import io.github.flexarr.core.PlaybackRequest
import io.github.flexarr.core.PodcastEpisode
import io.github.flexarr.core.ServerVersion
import io.github.flexarr.core.artistName
import io.github.flexarr.core.displayTitle
import io.github.flexarr.core.episodeSubtitle
import io.github.flexarr.core.nextEpisode
import io.github.flexarr.logging.FlexarrLog
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import org.json.JSONObject
import java.util.concurrent.Executors
import com.google.android.gms.cast.MediaMetadata as CastMetadata
import androidx.media3.common.MediaMetadata

private val flexarrLog = FlexarrLog("cast")

/**
 * Chromecast, as the iPhone app has AirPlay: while the phone is connected to one, what plays goes there. Each title is
 * its own server session, asked for with what a Chromecast plays (H.264 and AAC, as MP4 or converted to HLS in MPEG-TS),
 * and the Chromecast fetches its stream straight from the server, which needs no key: the session id in the address is
 * the credential. The phone follows it, shows where it's got to, and reports progress for the profile's history.
 *
 * The video player hands its title over as the phone connects (see [VideoHost]); music and podcasts go over by
 * themselves when they're playing. Ending the cast from the Cast button carries on here from the same place, as
 * turning AirPlay off does; losing the Chromecast leaves music and podcasts paused at that place. Live TV isn't cast.
 *
 * Needs Google Play services: without them (or before they've answered) [available] stays false and no Cast button shows.
 */
@OptIn(UnstableApi::class)
class Casting private constructor(private val context: Context, private val model: AppModel) {
    enum class Kind { Video, Music, Podcast }

    /** What's on the Chromecast. */
    class Playing(
        val kind: Kind,
        val itemId: String,
        val title: String,
        val subtitle: String?,
        val artwork: String?,
        val sessionId: String,
        /** A video's next episode, cast by itself at the end when the profile auto-plays. */
        val next: ItemSummary? = null,
        val autoPlayNext: Boolean = false,
        val episode: PodcastEpisode? = null,
    )

    /** The open video player, which hands its title over and takes it back. */
    interface VideoHost {
        /** The cast ended (or the viewer chose this phone): carry on with [itemId] here, from [at] (null: where it was left). */
        fun resumeHere(itemId: String, at: Double?)

        /** It played to the end on the Chromecast, with nothing after it. */
        fun finished()
    }

    /** The Cast framework loaded (Google Play services are there): the Cast button shows. */
    var available by mutableStateOf(false)
        private set
    /** The Chromecast connected to, by name; null when none is. */
    var deviceName: String? by mutableStateOf(null)
        private set
    val isConnected: Boolean get() = deviceName != null
    /** What's going to the Chromecast, from the moment it's asked for. */
    var kind: Kind? by mutableStateOf(null)
        private set
    var playing: Playing? by mutableStateOf(null)
        private set
    var isLoading by mutableStateOf(false)
        private set
    var isPlaying by mutableStateOf(false)
        private set
    var position by mutableDoubleStateOf(0.0)
        private set
    var duration by mutableDoubleStateOf(0.0)
        private set

    var videoHost: VideoHost? = null

    private val scope = model.scope
    private var castContext: CastContext? = null
    private var player: RemoteCastPlayer? = null
    /** Bumped by every title asked for, so a session that arrives after another was asked for is ended rather than cast. */
    private var generation = 0
    private var lastReport = 0L
    private var ticker: Job? = null
    /** Titles that couldn't be cast (or were taken back) this connection: they play on the phone. */
    private val declined = mutableSetOf<String>()
    /** What was last asked for, and from where, until it's playing (when the cast ends before it arrives). */
    private var asked: Triple<String, Double?, PodcastEpisode?>? = null
    private var musicQueue: List<ItemSummary> = emptyList()
    private var musicIndex = 0
    /** The server's version, asked once per server: converted streams reach a Chromecast from 0.55. */
    private var serverVersion: Pair<FlexarrClient, String?>? = null

    /** Whether [itemId], starting on the phone, should go to the Chromecast instead. */
    fun takes(itemId: String): Boolean = isConnected && itemId !in declined

    /** Whether [kind] is what the Chromecast has (or is being given). */
    fun casts(kind: Kind): Boolean = this.kind == kind

    // Starting

    /** The video player's title, from [at], in place of its own session ([replaces], so it stops converting for nobody). */
    fun castVideo(info: PlaybackInfo, at: Double, replaces: String?) {
        // Picture subtitles come burned in; text ones aren't shown by the Default Media Receiver.
        val burned = info.selectedSubtitleKey?.takeIf { key -> info.subtitles.any { it.key == key && !it.isText } }
        load(
            Kind.Video,
            PlaybackRequest(
                itemId = info.item.id,
                profile = videoProfile,
                startPositionSeconds = at,
                replacesSessionId = replaces,
                fileId = info.fileId,
                audioStreamIndex = info.selectedAudioIndex,
                subtitleKey = burned ?: "off",
            ),
        )
    }

    /** The music player's queue from its current track, where it's got to; it pauses on the phone. */
    fun castMusic() {
        val music = model.music
        if (music.current == null) return
        val at = music.position
        music.pause()
        musicQueue = music.queue
        musicIndex = music.index
        castTrack(at)
    }

    /** The podcast episode playing, where it's got to; it pauses on the phone. */
    fun castPodcast() {
        val podcasts = model.podcasts
        val episode = podcasts.current ?: return
        val at = podcasts.position
        podcasts.pause()
        castEpisode(episode, at)
    }

    private fun castTrack(at: Double = 0.0) {
        val track = musicQueue.getOrNull(musicIndex) ?: return
        load(Kind.Music, PlaybackRequest(itemId = track.id, profile = audioProfile, startPositionSeconds = at))
    }

    private fun castEpisode(episode: PodcastEpisode, at: Double?) {
        load(Kind.Podcast, PlaybackRequest(itemId = episode.id, profile = audioProfile, startPositionSeconds = at), episode)
    }

    /** Asks the server for [request]'s session and loads it on the Chromecast. The newest ask wins. */
    private fun load(kind: Kind, request: PlaybackRequest, episode: PodcastEpisode? = null) {
        val client = model.client ?: return
        val player = player ?: return
        val mine = ++generation
        endSession()
        this.kind = kind
        asked = Triple(request.itemId, request.startPositionSeconds, episode)
        isLoading = true
        scope.launch {
            try {
                val info = client.startPlayback(request)
                if (mine != generation) {
                    runCatching { client.stopPlayback(info.sessionId) }
                    return@launch
                }
                // Before 0.55 a converted stream doesn't say a Chromecast's player may read it.
                if (info.method == PlaybackMethod.Transcode && !streamsConverted(client)) {
                    runCatching { client.stopPlayback(info.sessionId) }
                    return@launch fail(request.itemId, "To cast this, update Flexarr on the server to 0.55 or later.", request.startPositionSeconds)
                }
                val now = describe(kind, info, episode, client)
                playing = now
                duration = info.durationSeconds
                position = info.startPositionSeconds
                player.setMediaItem(mediaItem(now, info, client), (info.startPositionSeconds * 1000).toLong())
                player.prepare()
                if (kind == Kind.Podcast) player.playbackParameters = PlaybackParameters(model.podcasts.speed.toFloat().coerceIn(0.5f, 2f))
                player.play()
                lastReport = System.currentTimeMillis()
                keepTicking()
            } catch (e: Exception) {
                if (mine != generation) return@launch
                flexarrLog.warning("Couldn't cast ${request.itemId}", e)
                fail(request.itemId, e.message ?: "This couldn’t be cast.", request.startPositionSeconds)
            } finally {
                if (mine == generation) isLoading = false
            }
        }
    }

    private fun describe(kind: Kind, info: PlaybackInfo, episode: PodcastEpisode?, client: FlexarrClient): Playing {
        val item = info.item
        return when (kind) {
            Kind.Video -> Playing(
                kind, item.id, item.displayTitle, item.episodeSubtitle ?: item.year?.toString(),
                client.imageUrl(item.artwork.backdrop ?: item.artwork.thumb ?: item.artwork.poster, 1280), info.sessionId,
                next = info.next, autoPlayNext = info.preferences?.autoPlayNext ?: true,
            )
            Kind.Music -> Playing(kind, item.id, item.title, item.artistName, client.imageUrl(item.artwork.poster, 600), info.sessionId)
            Kind.Podcast -> Playing(
                kind, item.id, episode?.title ?: item.title, episode?.podcastTitle ?: item.seriesTitle,
                client.imageUrl(episode?.artwork?.let { it.thumb ?: it.poster } ?: item.artwork.poster, 600), info.sessionId, episode = episode,
            )
        }
    }

    private fun mediaItem(now: Playing, info: PlaybackInfo, client: FlexarrClient): MediaItem =
        MediaItem.Builder()
            .setUri(client.url(info.url))
            .setMediaId(now.itemId)
            .setMimeType(
                when {
                    info.method == PlaybackMethod.Transcode -> MimeTypes.APPLICATION_M3U8
                    now.kind == Kind.Video -> MimeTypes.VIDEO_MP4
                    else -> MimeTypes.AUDIO_MPEG
                },
            )
            .setMediaMetadata(
                MediaMetadata.Builder()
                    .setTitle(now.title)
                    .setArtist(now.subtitle)
                    .setArtworkUri(now.artwork?.let(Uri::parse))
                    .setDurationMs((info.durationSeconds * 1000).toLong())
                    .setMediaType(
                        when (now.kind) {
                            Kind.Video -> if (info.item.kind == MediaKind.Episode) MediaMetadata.MEDIA_TYPE_TV_SHOW else MediaMetadata.MEDIA_TYPE_MOVIE
                            Kind.Music -> MediaMetadata.MEDIA_TYPE_MUSIC
                            Kind.Podcast -> MediaMetadata.MEDIA_TYPE_PODCAST_EPISODE
                        },
                    )
                    .build(),
            )
            .build()

    private suspend fun streamsConverted(client: FlexarrClient): Boolean {
        val known = serverVersion?.takeIf { it.first === client }
        val version = known?.second ?: runCatching { client.systemStatus().version }.getOrNull().also { serverVersion = client to it }
        return version != null && ServerVersion.atLeast(version, 0, 55)
    }

    /** It couldn't be cast: it plays on the phone instead, which says why. */
    private fun fail(itemId: String, message: String, at: Double?) {
        val kind = kind
        this.kind = null
        playing = null
        declined += itemId
        Toast.makeText(context, message, Toast.LENGTH_LONG).show()
        when (kind) {
            Kind.Video -> videoHost?.resumeHere(itemId, at)
            Kind.Music, Kind.Podcast, null -> Unit
        }
    }

    // Controls

    fun togglePlayPause() {
        val player = player ?: return
        if (playing == null) return
        if (player.isPlaying) player.pause() else player.play()
    }

    fun skip(seconds: Int) = seekTo(position + seconds)

    fun seekTo(seconds: Double) {
        val player = player ?: return
        if (playing == null) return
        val target = seconds.coerceIn(0.0, if (duration > 0) duration else seconds.coerceAtLeast(0.0))
        player.seekTo((target * 1000).toLong())
        position = target
        keepTicking()
    }

    val hasNextTrack: Boolean get() = kind == Kind.Music && (musicIndex + 1 < musicQueue.size || (model.music.repeatMode == MusicPlayer.RepeatMode.All && musicQueue.isNotEmpty()))

    fun nextTrack() {
        if (kind != Kind.Music || musicQueue.isEmpty()) return
        musicIndex = when {
            musicIndex + 1 < musicQueue.size -> musicIndex + 1
            model.music.repeatMode == MusicPlayer.RepeatMode.All -> 0
            else -> return
        }
        castTrack()
    }

    /** Back restarts the track unless it's just begun, as on the phone. */
    fun previousTrack() {
        if (kind != Kind.Music || musicQueue.isEmpty()) return
        if (position > 3 || musicIndex == 0) return seekTo(0.0)
        musicIndex--
        castTrack()
    }

    /** A podcast speed chosen while casting (the Chromecast plays between half and double). */
    fun setSpeed(speed: Double) {
        if (kind == Kind.Podcast) player?.playbackParameters = PlaybackParameters(speed.toFloat().coerceIn(0.5f, 2f))
    }

    /** Back to the phone, from where the Chromecast got to; the Chromecast stays connected for the next title. */
    fun playHere() {
        (playing?.itemId ?: asked?.first)?.let { declined += it }
        returnToPhone(play = true)
    }

    /** The video player closed: the Chromecast stops, and the session ends where it got to. */
    fun stop() {
        generation++
        endSession()
        player?.stop()
        player?.clearMediaItems()
        kind = null
        playing = null
        asked = null
        isLoading = false
        isPlaying = false
    }

    // Following it

    private val listener = object : Player.Listener {
        override fun onIsPlayingChanged(playing: Boolean) {
            isPlaying = playing
            if (playing) keepTicking()
            if (this@Casting.playing != null && player?.playbackState == Player.STATE_READY) report(paused = !playing)
        }

        override fun onPlaybackStateChanged(state: Int) {
            if (state == Player.STATE_ENDED && playing != null && !isLoading) itemEnded()
        }

        override fun onPlayerError(error: PlaybackException) {
            val now = playing ?: return
            flexarrLog.warning("The Chromecast couldn't play ${now.title}", error)
            val at = position
            endSession()
            fail(now.itemId, "The Chromecast couldn’t play this.", at)
        }
    }

    /** Twice a second while it plays: where it's got to, and a progress report every so often. */
    private fun keepTicking() {
        if (ticker?.isActive == true) return
        ticker = scope.launch {
            while (isActive) {
                val player = player ?: break
                if (playing != null && player.playbackState != Player.STATE_IDLE) {
                    position = player.currentPosition / 1000.0
                    if (player.duration > 0) duration = player.duration / 1000.0
                }
                if (!player.isPlaying) break
                if (System.currentTimeMillis() - lastReport >= REPORT_INTERVAL_MS) report(paused = false)
                delay(500)
            }
        }
    }

    private fun report(paused: Boolean, ended: Boolean = false) {
        val client = model.client ?: return
        val session = playing?.sessionId ?: return
        lastReport = System.currentTimeMillis()
        if (!model.recordsHistory) return
        val at = if (ended) duration else position
        scope.launch { runCatching { client.reportProgress(session, at, paused, ended) } }
    }

    /** Reports where it got to and ends its session (best effort; the server times abandoned ones out). */
    private fun endSession() {
        val client = model.client ?: return
        val session = playing?.sessionId ?: return
        ticker?.cancel()
        val at = position
        val records = model.recordsHistory
        playing = null
        scope.launch {
            if (records) runCatching { client.reportProgress(session, at, paused = true) }
            runCatching { client.stopPlayback(session) }
            model.watchStateChanged()
        }
    }

    /** Played through: on to what's next, as the phone would. */
    private fun itemEnded() {
        val now = playing ?: return
        report(paused = true, ended = true)
        when (now.kind) {
            Kind.Video -> {
                val next = now.next
                if (next != null && now.autoPlayNext) {
                    load(Kind.Video, PlaybackRequest(itemId = next.id, profile = videoProfile, subtitleKey = "off"))
                } else {
                    stop()
                    videoHost?.finished()
                }
            }
            Kind.Music -> when {
                model.music.repeatMode == MusicPlayer.RepeatMode.One -> castTrack()
                hasNextTrack -> nextTrack()
                else -> stop()
            }
            Kind.Podcast -> {
                val client = model.client ?: return stop()
                stop()
                scope.launch {
                    val next = runCatching { client.nextEpisode(now.itemId) }.getOrNull() ?: return@launch
                    if (isConnected) castEpisode(next, null)
                }
            }
        }
    }

    /**
     * The cast is over (ended from the Cast button, or the Chromecast lost): what it had carries on here from where it
     * got to, playing when [play], else paused there.
     */
    private fun returnToPhone(play: Boolean) {
        val kind = kind ?: return
        val now = playing
        // Still on its way to the Chromecast: what was asked for, from where it was asked.
        val itemId = now?.itemId ?: asked?.first ?: return
        val at = if (now != null) position else asked?.second
        val episode = now?.episode ?: asked?.third
        stop()
        when (kind) {
            Kind.Video -> videoHost?.resumeHere(itemId, at)
            Kind.Music -> {
                val music = model.music
                if (music.current?.id == itemId) {
                    at?.let(music::seek)
                    if (play) music.resume()
                } else if (play) {
                    music.queue.indexOfFirst { it.id == itemId }.takeIf { it >= 0 }?.let(music::skipTo)
                }
            }
            Kind.Podcast -> {
                val podcasts = model.podcasts
                if (podcasts.current?.id == itemId) {
                    at?.let(podcasts::seek)
                    if (play) podcasts.togglePlayPause()
                } else if (play && episode != null) {
                    podcasts.play(episode)
                }
            }
        }
    }

    // The Cast session

    private val sessions = object : SessionManagerListener<CastSession> {
        override fun onSessionStarted(session: CastSession, sessionId: String) = connected(session)
        override fun onSessionResumed(session: CastSession, wasSuspended: Boolean) = connected(session)
        override fun onSessionEnding(session: CastSession) {
            // Its last place, while the player can still say.
            player?.takeIf { playing != null }?.let { position = it.currentPosition / 1000.0 }
        }
        override fun onSessionEnded(session: CastSession, error: Int) {
            deviceName = null
            // Ended from the Cast button: carry on here. Lost: music and podcasts wait, paused at that place.
            returnToPhone(play = error == 0 || kind == Kind.Video)
            declined.clear()
        }
        override fun onSessionStarting(session: CastSession) = Unit
        override fun onSessionStartFailed(session: CastSession, error: Int) = Unit
        override fun onSessionResuming(session: CastSession, sessionId: String) = Unit
        override fun onSessionResumeFailed(session: CastSession, error: Int) = Unit
        override fun onSessionSuspended(session: CastSession, reason: Int) = Unit
    }

    private fun connected(session: CastSession) {
        declined.clear()
        deviceName = session.castDevice?.friendlyName ?: "Chromecast"
    }

    private fun loaded(cast: CastContext) {
        castContext = cast
        player = RemoteCastPlayer.Builder(context).setMediaItemConverter(Converter).build().also { it.addListener(listener) }
        cast.sessionManager.addSessionManagerListener(sessions, CastSession::class.java)
        cast.sessionManager.currentCastSession?.takeIf { it.isConnected }?.let(::connected)
        available = true
        // Music or a podcast playing on the phone while it's connected goes to the Chromecast (the video player hands
        // its own over).
        scope.launch {
            snapshotFlow { Triple(isConnected, model.music.isPlaying, model.music.current?.id) }.collect { (connected, playing, id) ->
                if (!connected || !playing || id == null || id in declined) return@collect
                // Played on the phone (from headphones, say) while it's on the Chromecast: it carries on there.
                if (this@Casting.playing?.itemId == id) {
                    model.music.pause()
                    player?.play()
                } else {
                    castMusic()
                }
            }
        }
        scope.launch {
            snapshotFlow { Triple(isConnected, model.podcasts.isPlaying, model.podcasts.current?.id) }.collect { (connected, playing, id) ->
                if (!connected || !playing || id == null || id in declined) return@collect
                if (this@Casting.playing?.itemId == id) {
                    model.podcasts.pause()
                    player?.play()
                } else {
                    castPodcast()
                }
            }
        }
    }

    /**
     * The Default Media Receiver's description of each title: its address and type (converted streams are HLS in
     * MPEG-TS, which it has to be told), and its name and artwork for the TV and the system's controls.
     */
    private object Converter : MediaItemConverter {
        override fun toMediaQueueItem(mediaItem: MediaItem): MediaQueueItem {
            val local = requireNotNull(mediaItem.localConfiguration)
            val metadata = mediaItem.mediaMetadata
            val type = local.mimeType ?: MimeTypes.VIDEO_MP4
            val cast = CastMetadata(
                when (metadata.mediaType) {
                    MediaMetadata.MEDIA_TYPE_MOVIE -> CastMetadata.MEDIA_TYPE_MOVIE
                    MediaMetadata.MEDIA_TYPE_TV_SHOW -> CastMetadata.MEDIA_TYPE_TV_SHOW
                    MediaMetadata.MEDIA_TYPE_MUSIC -> CastMetadata.MEDIA_TYPE_MUSIC_TRACK
                    else -> CastMetadata.MEDIA_TYPE_GENERIC
                },
            ).apply {
                metadata.title?.let { putString(CastMetadata.KEY_TITLE, it.toString()) }
                metadata.artist?.let {
                    putString(CastMetadata.KEY_SUBTITLE, it.toString())
                    putString(CastMetadata.KEY_ARTIST, it.toString())
                }
                metadata.artworkUri?.let { addImage(WebImage(it)) }
            }
            val info = MediaInfo.Builder(local.uri.toString())
                .setContentUrl(local.uri.toString())
                .setStreamType(MediaInfo.STREAM_TYPE_BUFFERED)
                .setContentType(type)
                .setMetadata(cast)
                .setCustomData(JSONObject().put(MEDIA_ID, mediaItem.mediaId))
                .apply {
                    metadata.durationMs?.takeIf { it > 0 }?.let(::setStreamDuration)
                    if (type == MimeTypes.APPLICATION_M3U8) {
                        setHlsSegmentFormat(HlsSegmentFormat.TS)
                        setHlsVideoSegmentFormat(HlsVideoSegmentFormat.MPEG2_TS)
                    }
                }
                .build()
            return MediaQueueItem.Builder(info).build()
        }

        override fun toMediaItem(mediaQueueItem: MediaQueueItem): MediaItem {
            val info = requireNotNull(mediaQueueItem.media)
            val cast = info.metadata
            return MediaItem.Builder()
                .setUri(info.contentUrl ?: info.contentId)
                .setMediaId(info.customData?.optString(MEDIA_ID).orEmpty())
                .setMimeType(info.contentType)
                .setMediaMetadata(
                    MediaMetadata.Builder()
                        .setTitle(cast?.getString(CastMetadata.KEY_TITLE))
                        .setArtist(cast?.getString(CastMetadata.KEY_SUBTITLE))
                        .setArtworkUri(cast?.images?.firstOrNull()?.url)
                        .build(),
                )
                .build()
        }

        private const val MEDIA_ID = "mediaId"
    }

    companion object {
        private const val REPORT_INTERVAL_MS = 10_000L

        /**
         * What every Chromecast plays: H.264 up to 1080p with stereo AAC or MP3, in MP4. Anything else the server
         * converts, to HLS in MPEG-TS (fragmented MP4 HLS isn't asked for: not every receiver takes it).
         */
        val videoProfile = DeviceProfile(
            client = "chromecast",
            containers = listOf("mp4", "m4v", "mov"),
            videoCodecs = listOf("h264"),
            audioCodecs = listOf("aac", "mp3"),
            hdr = false,
            maxHeight = 1080,
            maxAudioChannels = 2,
            maxBitrate = 0,
            fmp4 = false,
            dolbyVision = false,
            hdr10 = false,
            hlg = false,
        )

        /** Music and podcasts: the files a Chromecast's player reads as they are. */
        val audioProfile = videoProfile.copy(
            containers = listOf("mp4", "m4a", "mp3", "aac", "flac", "ogg", "opus", "wav", "webm"),
            audioCodecs = listOf("aac", "mp3", "flac", "opus", "vorbis"),
        )

        /** The one for the app, once [start] has run (and there's a model to cast from). It holds the application's context. */
        @SuppressLint("StaticFieldLeak")
        var shared: Casting? = null
            private set

        /**
         * Sets Cast up as the app starts. The framework loads in the background; on a device without Google Play
         * services it fails quietly and the app goes on without it.
         */
        fun start(context: Context, model: AppModel) {
            val casting = Casting(context.applicationContext, model).also { shared = it }
            try {
                androidx.media3.cast.Cast.getSingletonInstance(context).initialize()
                CastContext.getSharedInstance(context, Executors.newSingleThreadExecutor())
                    .addOnSuccessListener { casting.loaded(it) }
                    .addOnFailureListener { flexarrLog.info("Cast isn't available on this device", it) }
            } catch (e: Exception) {
                flexarrLog.info("Cast isn't available on this device", e)
            }
        }
    }
}
