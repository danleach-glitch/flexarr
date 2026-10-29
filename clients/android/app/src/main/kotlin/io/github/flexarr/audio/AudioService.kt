package io.github.flexarr.audio

import android.app.PendingIntent
import android.content.Intent
import androidx.media3.session.MediaSession
import androidx.media3.session.MediaSessionService
import io.github.flexarr.MainActivity
import io.github.flexarr.appModel

/**
 * Keeps a podcast playing with the app in the background or the screen off, and puts its controls on the lock screen,
 * in the notification shade and on headphones. The player itself is the app's [PodcastPlayer]; this only publishes it.
 */
class AudioService : MediaSessionService() {
    private var session: MediaSession? = null

    override fun onCreate() {
        super.onCreate()
        // Tapping the notification opens the app.
        val open = PendingIntent.getActivity(
            this,
            0,
            Intent(this, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )
        session = MediaSession.Builder(this, appModel.podcasts.player).setSessionActivity(open).build().also { addSession(it) }
    }

    override fun onGetSession(controllerInfo: MediaSession.ControllerInfo): MediaSession? = session

    /** Swiped away from recent apps: carry on if it's playing, otherwise there's nothing to keep. */
    override fun onTaskRemoved(rootIntent: Intent?) {
        val player = session?.player
        if (player == null || !player.playWhenReady || player.mediaItemCount == 0) stopSelf()
    }

    override fun onDestroy() {
        // The player belongs to the app and outlives this service; only the session goes.
        session?.release()
        session = null
        super.onDestroy()
    }
}
