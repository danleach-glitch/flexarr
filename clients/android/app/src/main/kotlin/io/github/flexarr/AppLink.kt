package io.github.flexarr

import android.content.Context
import android.content.Intent
import android.net.Uri
import io.github.flexarr.core.FlexarrClient
import java.util.UUID

/**
 * Somewhere in the app to go from outside it: a notification, an app shortcut or the home-screen widget. Each is a
 * `flexarr://` address on an intent to [MainActivity], which hands it to the screens ([AppModel.pendingLink]).
 * MainActivity is open to every app (it's the launcher's), so [received] checks a link came from this one before it
 * plays anything.
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

    /** An intent that opens the app there, marked as the app's own (a notification's, the widget's). */
    fun intent(context: Context): Intent =
        Intent(Intent.ACTION_VIEW, uri, context, MainActivity::class.java)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP)
            .putExtra(FROM_APP, secret(context))

    companion object {
        private const val FROM_APP = "io.github.flexarr.fromApp"

        @Volatile
        private var cachedSecret: String? = null

        /**
         * A random value only this app knows, made once and kept, that its own links carry: another app can send
         * MainActivity a `flexarr://play/…` link, but not this.
         */
        private fun secret(context: Context): String = cachedSecret ?: synchronized(this) {
            cachedSecret ?: run {
                val prefs = context.getSharedPreferences("links", Context.MODE_PRIVATE)
                prefs.getString(SECRET, null) ?: UUID.randomUUID().toString().also { prefs.edit().putString(SECRET, it).apply() }
            }.also { cachedSecret = it }
        }

        private const val SECRET = "secret"

        /**
         * The link [intent] opened the app with. One that would start something playing but didn't come from this
         * app (another app sent it) opens the title's page instead, so nothing plays without the person asking.
         */
        fun received(context: Context, intent: Intent): AppLink? {
            val link = parse(intent.data) ?: return null
            if (intent.getStringExtra(FROM_APP) == secret(context)) return link
            return when (link) {
                is Play -> Item(link.id)
                // An episode has no page of its own to open by its id alone.
                is PodcastEpisode -> null
                else -> link
            }
        }

        fun parse(uri: Uri?): AppLink? {
            if (uri?.scheme != "flexarr") return null
            // Ids are the server's (GUIDs): anything else could reach another of its paths once put in one.
            val id = uri.pathSegments.firstOrNull()?.takeIf(FlexarrClient::isPlainId)
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
