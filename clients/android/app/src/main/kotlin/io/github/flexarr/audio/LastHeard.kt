package io.github.flexarr.audio

import android.content.Context

/**
 * The last book or podcast episode started on this phone, kept so the headset's play button and Android's recent media
 * controls can pick it up again after the app has been closed ([CarLibrary.onPlaybackResumption]). Where it got to is
 * the server's (or the download's) to say, as when it's played from the app.
 */
internal data class LastHeard(
    /** What [CarLibrary.play] takes: `book:<id>` or `episode:<id>`. */
    val mediaId: String,
    val title: String,
    val subtitle: String?,
    /** The server's relative path to its picture. */
    val artwork: String?,
) {
    companion object {
        private const val PREFERENCES = "audio"
        private const val ID = "lastHeard.id"
        private const val TITLE = "lastHeard.title"
        private const val SUBTITLE = "lastHeard.subtitle"
        private const val ARTWORK = "lastHeard.artwork"

        fun save(context: Context, heard: LastHeard) {
            context.getSharedPreferences(PREFERENCES, Context.MODE_PRIVATE).edit()
                .putString(ID, heard.mediaId)
                .putString(TITLE, heard.title)
                .putString(SUBTITLE, heard.subtitle)
                .putString(ARTWORK, heard.artwork)
                .apply()
        }

        fun load(context: Context): LastHeard? {
            val prefs = context.getSharedPreferences(PREFERENCES, Context.MODE_PRIVATE)
            val id = prefs.getString(ID, null) ?: return null
            return LastHeard(id, prefs.getString(TITLE, null).orEmpty(), prefs.getString(SUBTITLE, null), prefs.getString(ARTWORK, null))
        }

        /** Signed out: nothing to pick up again. */
        fun clear(context: Context) {
            context.getSharedPreferences(PREFERENCES, Context.MODE_PRIVATE).edit().clear().apply()
        }
    }
}
