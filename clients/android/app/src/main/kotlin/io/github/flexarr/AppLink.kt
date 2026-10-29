package io.github.flexarr

import android.content.Context
import android.content.Intent
import android.net.Uri

/**
 * Somewhere in the app to go from outside it: a notification, an app shortcut or the home-screen widget. Each is a
 * `flexarr://` address on an intent to [MainActivity], which hands it to the screens ([AppModel.pendingLink]).
 */
sealed interface AppLink {
    /** A film, show or episode's page. */
    data class Item(val id: String) : AppLink
    /** Plays a film or episode (or a show's next episode) from where it was left. */
    data class Play(val id: String) : AppLink
    /** A podcast's page. */
    data class Podcast(val id: String) : AppLink
    /** Plays a podcast episode. */
    data class PodcastEpisode(val id: String) : AppLink
    data object Search : AppLink
    data object Downloads : AppLink
    /** Home, where Continue Watching is. */
    data object Home : AppLink

    val uri: Uri
        get() = when (this) {
            is Item -> Uri.parse("flexarr://item/${Uri.encode(id)}")
            is Play -> Uri.parse("flexarr://play/${Uri.encode(id)}")
            is Podcast -> Uri.parse("flexarr://podcast/${Uri.encode(id)}")
            is PodcastEpisode -> Uri.parse("flexarr://episode/${Uri.encode(id)}")
            Search -> Uri.parse("flexarr://search")
            Downloads -> Uri.parse("flexarr://downloads")
            Home -> Uri.parse("flexarr://home")
        }

    /** An intent that opens the app there. */
    fun intent(context: Context): Intent =
        Intent(Intent.ACTION_VIEW, uri, context, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP)

    companion object {
        fun parse(uri: Uri?): AppLink? {
            if (uri?.scheme != "flexarr") return null
            val id = uri.pathSegments.firstOrNull()
            return when (uri.host) {
                "item" -> id?.let(::Item)
                "play" -> id?.let(::Play)
                "podcast" -> id?.let(::Podcast)
                "episode" -> id?.let(::PodcastEpisode)
                "search" -> Search
                "downloads" -> Downloads
                "home" -> Home
                else -> null
            }
        }
    }
}
