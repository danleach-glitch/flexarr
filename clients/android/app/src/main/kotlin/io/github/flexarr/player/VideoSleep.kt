package io.github.flexarr.player

import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.media3.common.Player
import io.github.flexarr.core.Format
import kotlinx.coroutines.CoroutineScope
import kotlin.math.ceil

/**
 * The sleep timer over a picture, a title's or a live channel's: the [timer] itself, then "Still watching?" over the
 * dimmed picture in its last minute with the sound fading over the last ten seconds, and the pause when it runs out,
 * dimmed until someone's there. It lasts as long as the screen, so a timer of minutes carries on into the next
 * episode (or channel). The iPhone player's sleep timer, which both of its screens share.
 */
class VideoSleep(scope: CoroutineScope, private val player: Player, private val onChange: () -> Unit = {}) {
    enum class Phase {
        Off,
        /** The last minute: "Still watching?" over the dimmed picture. */
        Asking,
        /** Ran out: paused, dimmed until someone touches something. */
        Asleep,
    }

    val timer = SleepTimer(scope).also { it.onExpire = { fallAsleep() } }
    var phase by mutableStateOf(Phase.Off)
        private set
    /** "Pausing in 0:42" while asking. */
    var detail by mutableStateOf("")
        private set

    val setting: SleepTimer.Setting? get() = timer.setting

    /** The timer has this episode stopping at its end, or has run out: no going on to the next by itself. */
    val holdsEpisode: Boolean get() = timer.setting == SleepTimer.Setting.EndOfEpisode || phase != Phase.Off

    /** The viewer chose a timer (or Off, or Cancel timer) in the menu or the question. */
    fun set(setting: SleepTimer.Setting?) {
        timer.set(setting)
        if (phase == Phase.Asking) endAsking() else onChange()
    }

    /** "+15 min" (or a tap, or a shake, while it asks): fifteen minutes from now, whatever the timer was. */
    fun extend() = set(SleepTimer.Setting.Minutes(15))

    /**
     * Checked a few times a second while it runs: the question in the last minute, the sound fading over the last ten
     * seconds, and the stop. [endOfEpisode] is for End of episode, which only the player knows: the seconds left and
     * whether it's time to ask, or null while it can't tell yet.
     */
    fun check(endOfEpisode: () -> Pair<Double, Boolean>? = { null }) {
        if (phase == Phase.Asleep) return
        val setting = timer.setting
        if (setting == null) {
            if (phase == Phase.Asking) endAsking()
            return
        }
        val (left, asking) = when (setting) {
            is SleepTimer.Setting.Minutes -> (timer.remaining ?: 0.0).let { it to (it <= 60) }
            SleepTimer.Setting.EndOfEpisode -> endOfEpisode() ?: return
        }
        if (setting == SleepTimer.Setting.EndOfEpisode && left <= 0.3) return fallAsleep()
        if (asking) {
            if (phase == Phase.Off) {
                phase = Phase.Asking
                onChange()
            }
            detail = "Pausing in " + Format.timestamp(ceil(left))
            player.volume = (left / 10).toFloat().coerceIn(0f, 1f)
        } else if (phase == Phase.Asking) {
            endAsking()
        }
    }

    /** Back to watching: the question and the dim go, the sound comes back up. */
    private fun endAsking() {
        phase = Phase.Off
        player.volume = 1f
        onChange()
    }

    /** Run out: the picture pauses (its place reported as for any pause) and stays dimmed until someone's there. */
    fun fallAsleep() {
        if (phase == Phase.Asleep) return
        if (timer.setting != null) timer.set(null)
        phase = Phase.Asleep
        player.pause()
        onChange()
    }

    /** Someone's there after the timer ran out: the dim goes (still paused). */
    fun wake() {
        if (phase != Phase.Asleep) return
        phase = Phase.Off
        player.volume = 1f
        onChange()
    }

    /** A shake of the phone: yes while it asks, and awake once it's run out, as on iPhone. */
    fun shaken() = when (phase) {
        Phase.Asking -> extend()
        Phase.Asleep -> wake()
        Phase.Off -> Unit
    }
}
