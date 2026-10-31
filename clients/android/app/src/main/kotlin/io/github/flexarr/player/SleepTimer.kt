package io.github.flexarr.player

import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

/**
 * When playback should stop by itself: after so many minutes, or at the end of the episode. Podcasts and the video
 * player each keep one; this is the part they share, the choice and the clock. What happens as it runs out is theirs:
 * a podcast just pauses, a video asks "Still watching?" first and fades out. The iPhone app's SleepTimer.
 */
class SleepTimer(private val scope: CoroutineScope) {
    sealed interface Setting {
        val label: String

        data class Minutes(val minutes: Int) : Setting {
            override val label get() = "$minutes minutes"
        }

        data object EndOfEpisode : Setting {
            override val label get() = "End of episode"
        }

        /** A book's: stops where the chapter playing ends. */
        data object EndOfChapter : Setting {
            override val label get() = "End of chapter"
        }
    }

    /** A timer of minutes runs [minutes] longer (a shake of the phone, for a book). */
    fun extend(minutes: Int) {
        val ends = endsAt ?: return
        val setting = setting ?: return
        job?.cancel()
        val later = ends + minutes * 60_000L
        endsAt = later
        job = scope.launch {
            delay(later - System.currentTimeMillis())
            job = null
            this@SleepTimer.setting = null
            endsAt = null
            onExpire?.invoke()
        }
        this.setting = setting
    }

    var setting: Setting? by mutableStateOf(null)
        private set

    /** When a timer of minutes runs out (milliseconds since the epoch). */
    var endsAt: Long? by mutableStateOf(null)
        private set

    /** A timer of minutes ran out (its setting is cleared first). End of episode is the player's to notice. */
    var onExpire: (() -> Unit)? = null
    private var job: Job? = null

    /** Starts (or replaces, or with null turns off) the timer. A timer of minutes counts from now. */
    fun set(setting: Setting?) {
        job?.cancel()
        job = null
        this.setting = setting
        endsAt = null
        if (setting !is Setting.Minutes) return
        val ends = System.currentTimeMillis() + setting.minutes * 60_000L
        endsAt = ends
        job = scope.launch {
            delay(ends - System.currentTimeMillis())
            job = null
            this@SleepTimer.setting = null
            endsAt = null
            onExpire?.invoke()
        }
    }

    /** Seconds until a timer of minutes runs out (null for none, or end of episode). */
    val remaining: Double?
        get() = endsAt?.let { ((it - System.currentTimeMillis()) / 1000.0).coerceAtLeast(0.0) }

    /** "Sleep timer · 12 min left" while minutes run. */
    val menuTitle: String
        get() = remaining?.let { "Sleep timer · ${kotlin.math.ceil(it / 60).toInt()} min left" } ?: "Sleep timer"

    companion object {
        /** The lengths offered, in minutes. */
        val minuteChoices = listOf(15, 30, 45, 60)
    }
}
