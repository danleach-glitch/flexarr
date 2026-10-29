package io.github.flexarr.audio

import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import androidx.media3.common.Player
import androidx.media3.session.MediaLibraryService
import androidx.media3.session.MediaLibraryService.MediaLibrarySession
import androidx.media3.session.MediaSession
import io.github.flexarr.MainActivity
import io.github.flexarr.appModel

/**
 * Keeps music or a podcast playing with the app in the background or the screen off, and puts its controls on the lock
 * screen, in the notification shade and on headphones. The players themselves belong to the app ([MusicPlayer] and
 * [PodcastPlayer]); this only publishes whichever is playing. It's also what Android Auto browses ([CarLibrary]).
 */
class AudioService : MediaLibraryService() {
    private var session: MediaLibrarySession? = null
    private lateinit var library: CarLibrary

    override fun onCreate() {
        super.onCreate()
        // Tapping the notification opens the app.
        val open = PendingIntent.getActivity(
            this,
            0,
            Intent(this, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )
        library = CarLibrary(appModel)
        session = MediaLibrarySession.Builder(this, library.Router(activePlayer()), library).setSessionActivity(open).build().also { addSession(it) }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        // Music and podcasts take turns: the session follows whichever started last.
        session?.let { session -> activePlayer().let { if ((session.player as? CarLibrary.Router)?.wrapped !== it) session.player = library.Router(it) } }
        return super.onStartCommand(intent, flags, startId)
    }

    /** The music if any is queued, else the podcast player. */
    private fun activePlayer(): Player {
        val model = appModel
        return if (model.music.current != null) model.music.sessionPlayer else model.podcasts.player
    }

    override fun onGetSession(controllerInfo: MediaSession.ControllerInfo): MediaLibrarySession? = session

    /** Swiped away from recent apps: carry on if it's playing, otherwise there's nothing to keep. */
    override fun onTaskRemoved(rootIntent: Intent?) {
        val player = session?.player
        if (player == null || !player.playWhenReady || player.mediaItemCount == 0) stopSelf()
    }

    override fun onDestroy() {
        // The players belong to the app and outlive this service; only the session goes.
        session?.release()
        session = null
        super.onDestroy()
    }

    companion object {
        /** Something started playing: publish it (starting the service, or pointing its session at the new player). */
        fun start(context: Context) {
            context.startService(Intent(context, AudioService::class.java))
        }

        /** Something stopped: once neither music nor a podcast is left, there's nothing to publish. */
        fun stopIfIdle(context: Context) {
            val model = context.appModel
            if (model.music.current == null && model.podcasts.current == null) {
                context.stopService(Intent(context, AudioService::class.java))
            }
        }
    }
}
