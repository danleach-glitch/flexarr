package io.github.flexarr.data

import android.content.Context
import androidx.core.content.edit
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import java.time.LocalTime
import java.util.Locale

/**
 * How films and episodes sound on this phone: the speakers, a lift for the dialogue and night mode. Kept on the phone
 * (it's about the room), with the same choices and values as the web and the Apple apps. Settings › Playback and the
 * player's audio menu both change it.
 */
data class SoundSettings(
    val speakers: Speakers = Speakers.Auto,
    val dialogueBoost: DialogueBoost = DialogueBoost.Off,
    val nightMode: NightMode = NightMode.Off,
    /** Minutes after midnight that a scheduled night mode starts and stops; it runs past midnight when start > end. */
    val nightStart: Int = 22 * 60,
    val nightEnd: Int = 7 * 60,
) {
    enum class Speakers(val label: String, val channels: Int?) {
        Auto("Auto", null), Stereo("Stereo", 2), Surround("5.1", 6)
    }

    /** [db]: the lift each strength gives the dialogue, the same in every app. */
    enum class DialogueBoost(val db: Double) { Off(0.0), Low(3.0), Medium(6.0), High(9.0) }

    enum class NightMode { Off, On, Scheduled }

    /** What the sound does at [now], with the schedule worked out. */
    fun shaping(now: LocalTime): Shaping = Shaping(
        channels = speakers.channels,
        dialogueDb = dialogueBoost.db,
        night = nightMode == NightMode.On || (nightMode == NightMode.Scheduled && inWindow(now.hour * 60 + now.minute)),
    )

    private fun inWindow(minute: Int) = if (nightStart <= nightEnd) minute in nightStart until nightEnd else minute >= nightStart || minute < nightEnd

    /** The night-mode hours as "22:00–07:00". */
    val nightHours: String get() = "${clock(nightStart)}–${clock(nightEnd)}"

    data class Shaping(val channels: Int? = null, val dialogueDb: Double = 0.0, val night: Boolean = false) {
        val isActive: Boolean get() = channels != null || dialogueDb > 0 || night
    }

    companion object {
        fun clock(minutes: Int) = String.format(Locale.ROOT, "%02d:%02d", minutes / 60, minutes % 60)

        private val state = MutableStateFlow<SoundSettings?>(null)
        private val shared = state.asStateFlow()

        /** The phone's settings, live: the player follows a change made while it plays. The same flow every call. */
        fun flow(context: Context): StateFlow<SoundSettings?> {
            if (state.value == null) state.value = read(context)
            return shared
        }

        fun current(context: Context): SoundSettings = state.value ?: read(context).also { state.value = it }

        fun update(context: Context, change: (SoundSettings) -> SoundSettings) {
            val next = change(current(context))
            state.value = next
            prefs(context).edit {
                putString("speakers", next.speakers.name)
                putString("dialogueBoost", next.dialogueBoost.name)
                putString("nightMode", next.nightMode.name)
                putInt("nightStart", next.nightStart)
                putInt("nightEnd", next.nightEnd)
            }
        }

        private fun read(context: Context): SoundSettings {
            val p = prefs(context)
            val d = SoundSettings()
            fun minutes(key: String, fallback: Int) = p.getInt(key, fallback).takeIf { it in 0 until 24 * 60 } ?: fallback
            return SoundSettings(
                speakers = Speakers.entries.firstOrNull { it.name == p.getString("speakers", null) } ?: d.speakers,
                dialogueBoost = DialogueBoost.entries.firstOrNull { it.name == p.getString("dialogueBoost", null) } ?: d.dialogueBoost,
                nightMode = NightMode.entries.firstOrNull { it.name == p.getString("nightMode", null) } ?: d.nightMode,
                nightStart = minutes("nightStart", d.nightStart),
                nightEnd = minutes("nightEnd", d.nightEnd),
            )
        }

        private fun prefs(context: Context) = context.getSharedPreferences("sound", Context.MODE_PRIVATE)
    }
}
