package io.github.flexarr.data

import android.content.Context
import androidx.core.content.edit

/**
 * How this phone's player behaves, kept on the phone (not the profile): how far back and forward jump. As the
 * iPhone's "FlexarrSkipSeconds", chosen in Settings › Playback.
 */
object PlayerSettings {
    /** The jumps Settings offers, in seconds. */
    val skipChoices = listOf(10, 15, 30)

    private const val SKIP_SECONDS = "skipSeconds"

    /** How far the player's back and forward buttons (and a double-tap at either side) jump, in seconds. */
    fun skipSeconds(context: Context): Int = prefs(context).getInt(SKIP_SECONDS, 10).takeIf { it in skipChoices } ?: 10

    fun setSkipSeconds(context: Context, seconds: Int) = prefs(context).edit { putInt(SKIP_SECONDS, seconds) }

    private fun prefs(context: Context) = context.getSharedPreferences("player", Context.MODE_PRIVATE)
}
