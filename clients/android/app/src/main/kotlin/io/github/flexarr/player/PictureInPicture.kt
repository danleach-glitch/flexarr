package io.github.flexarr.player

import android.app.PendingIntent
import android.app.PictureInPictureParams
import android.app.RemoteAction
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.PackageManager
import android.graphics.drawable.Icon
import android.os.Build
import android.util.Rational
import androidx.activity.ComponentActivity
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.core.content.ContextCompat
import androidx.lifecycle.Lifecycle
import androidx.media3.common.VideoSize
import io.github.flexarr.R

/**
 * Picture in picture for a player screen, a title's or a live channel's: a small window in the corner over everything
 * else, in the picture's shape, with back, play or pause, and forward. Leaving the app while it plays goes there by
 * itself, and closing the window closes the player. The activity passes on its picture in picture calls.
 */
internal class PictureInPicture(private val activity: ComponentActivity, private val source: Source) {
    /** What the window shows and what its buttons do. */
    interface Source {
        val videoSize: VideoSize
        val isPlaying: Boolean
        /** How far back and forward go (Settings › Playback). */
        val skipSeconds: Int
        /** Forward goes somewhere (a live channel at live can't). */
        val canSkipForward: Boolean get() = true
        fun skip(seconds: Int)
        fun togglePlayPause()
    }

    /** In the corner window: only the picture (and its subtitles) shows. */
    var isActive by mutableStateOf(false)
        private set

    val isSupported: Boolean
        get() = activity.packageManager.hasSystemFeature(PackageManager.FEATURE_PICTURE_IN_PICTURE)

    /** Each screen's own, so one's buttons never reach another. */
    private val action = "io.github.flexarr.PIP_CONTROL." + activity.javaClass.simpleName

    private val receiver = object : BroadcastReceiver() {
        override fun onReceive(context: Context, intent: Intent) {
            when (intent.getIntExtra(EXTRA_CONTROL, 0)) {
                BACK -> source.skip(-source.skipSeconds)
                TOGGLE -> source.togglePlayPause()
                FORWARD -> source.skip(source.skipSeconds)
            }
        }
    }

    init {
        ContextCompat.registerReceiver(activity, receiver, IntentFilter(action), ContextCompat.RECEIVER_NOT_EXPORTED)
    }

    private fun params(): PictureInPictureParams {
        val size = source.videoSize
        val playing = source.isPlaying
        val seconds = source.skipSeconds
        return PictureInPictureParams.Builder().apply {
            if (size.width > 0 && size.height > 0) {
                // Android takes shapes between 1:2.39 and 2.39:1.
                val ratio = (size.width * size.pixelWidthHeightRatio / size.height).coerceIn(1 / 2.39f, 2.39f)
                setAspectRatio(Rational((ratio * 1000).toInt(), 1000))
            }
            setActions(
                listOf(
                    // Material draws 10 on the arrows; any other jump is the plain arrow, named by its seconds.
                    button(if (seconds == 10) R.drawable.ic_pip_replay_10 else R.drawable.ic_pip_replay, "Back $seconds seconds", BACK),
                    if (playing) button(R.drawable.ic_pip_pause, "Pause", TOGGLE) else button(R.drawable.ic_pip_play, "Play", TOGGLE),
                    button(if (seconds == 10) R.drawable.ic_pip_forward_10 else R.drawable.ic_pip_forward, "Forward $seconds seconds", FORWARD)
                        .apply { isEnabled = source.canSkipForward },
                ),
            )
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
                setAutoEnterEnabled(playing)
                setSeamlessResizeEnabled(true)
            }
        }.build()
    }

    private fun button(icon: Int, title: String, code: Int): RemoteAction {
        val intent = PendingIntent.getBroadcast(
            activity,
            code,
            Intent(action).setPackage(activity.packageName).putExtra(EXTRA_CONTROL, code),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )
        return RemoteAction(Icon.createWithResource(activity, icon), title, title, intent)
    }

    /** The picture's shape or playing changed: the window (and whether it's gone to by itself) follows. */
    fun update() {
        if (isSupported) runCatching { activity.setPictureInPictureParams(params()) }
    }

    fun enter() {
        if (isSupported) runCatching { activity.enterPictureInPictureMode(params()) }
    }

    /** Before Android 12 there's no going there by itself: leaving the app while it plays does it here. */
    fun userLeaving() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.S && source.isPlaying) enter()
    }

    fun modeChanged(inPictureInPicture: Boolean) {
        isActive = inPictureInPicture
        // The corner window was closed (rather than opened out again): so is the player.
        if (!inPictureInPicture && !activity.lifecycle.currentState.isAtLeast(Lifecycle.State.STARTED)) activity.finish()
    }

    fun release() {
        runCatching { activity.unregisterReceiver(receiver) }
    }

    private companion object {
        const val EXTRA_CONTROL = "control"
        const val BACK = 1
        const val TOGGLE = 2
        const val FORWARD = 3
    }
}
