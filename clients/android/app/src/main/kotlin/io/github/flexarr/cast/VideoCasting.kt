package io.github.flexarr.cast

import androidx.activity.ComponentActivity
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.lifecycle.DefaultLifecycleObserver
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleOwner
import io.github.flexarr.player.PlaybackController

/**
 * The video player's side of casting. While the phone is connected to a Chromecast, the title the player opens (or has
 * open as it connects) goes there from where it is, pausing here, and [showing] says to put up the casting screen.
 * When the cast ends it carries on here from the Chromecast's place: straight away with the player in front, or as
 * soon as it's back in front.
 */
class VideoCasting(private val activity: ComponentActivity, private val controller: PlaybackController, val casting: Casting) : Casting.VideoHost {
    /** The phone's session last handed over, so each is handed over once. */
    private var handedOff: String? = null
    private var pending: Pair<String, Double?>? = null

    init {
        casting.videoHost = this
        activity.lifecycle.addObserver(object : DefaultLifecycleObserver {
            override fun onStart(owner: LifecycleOwner) {
                pending?.let { (itemId, at) -> controller.start(itemId, startAt = at) }
                pending = null
            }
        })
    }

    /** A video is on the Chromecast (or on its way): the casting screen takes the picture's place. */
    val showing: Boolean get() = casting.casts(Casting.Kind.Video)

    /** Hands the player's title over whenever the phone is connected and a session of its own starts. */
    @Composable
    fun HandOff() {
        val info = controller.info
        LaunchedEffect(info?.sessionId, casting.isConnected) {
            if (info == null || handedOff == info.sessionId || !casting.takes(info.item.id)) return@LaunchedEffect
            handedOff = info.sessionId
            val at = controller.player.currentPosition / 1000.0
            controller.player.pause()
            casting.castVideo(info, at, replaces = if (controller.isOffline) null else info.sessionId)
        }
    }

    override fun resumeHere(itemId: String, at: Double?) {
        if (activity.lifecycle.currentState.isAtLeast(Lifecycle.State.STARTED)) controller.start(itemId, startAt = at) else pending = itemId to at
    }

    override fun finished() = activity.finish()

    /** The player closed. */
    fun release() {
        if (casting.videoHost === this) casting.videoHost = null
    }
}
