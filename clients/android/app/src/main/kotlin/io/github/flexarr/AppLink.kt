package io.github.flexarr

import androidx.core.content.edit
import android.content.Context
import android.content.Intent
import android.net.Uri
import io.github.flexarr.core.FlexarrClient
import java.util.UUID

/**
 * Somewhere in the app to go from outside it: a notification, an app shortcut, the home-screen widget, or a
 * `flexarr://` link from another app (the iPhone app's: `flexarr://play/<id>?t=…&shuffle=1`, `flexarr://live/<id>`, `flexarr://watchlist`,
 * any with `profile=<id or name>`). Each is a `flexarr://` address on an intent to [MainActivity], which hands it to
 * the screens ([AppModel.pendingLink]). MainActivity is open to every app (it's the launcher's), so [received] checks
 * a link came from this one before it plays anything or skips "Who's watching?".
 */
sealed interface AppLink {
    /** A film, show or episode's page. */
    data class Item(val id: String) : AppLink
    /**
     * Plays a film or episode (or a show's next episode) from where it was left, or from [startAt] seconds; [shuffle]
     * plays a show's episodes in a random order.
     */
    data class Play(val id: String, val startAt: Int? = null, val shuffle: Boolean = false) : AppLink
    /** Puts a Live TV channel on. */
    data class Live(val channelId: String) : AppLink
    /** The Live TV guide. */
    data object LiveTv : AppLink
    /** A podcast's page. */
    data class Podcast(val id: String) : AppLink
    /** Plays a podcast episode. */
    data class PodcastEpisode(val id: String) : AppLink
    data object Search : AppLink
    data object Downloads : AppLink
    /** Home, where Continue Watching is. */
    data object Home : AppLink
    /** The profile's watchlist (the watchlist widget's heading). */
    data object Watchlist : AppLink
    /** Settings (a notification that someone's guessing the main profile's PIN). */
    data object Settings : AppLink
    /** Asks whether to let in the device showing [code] (a notification that it's asking to connect). */
    data class Pairing(val code: String) : AppLink
    /**
     * [link] as the profile [profile] (its id, or its name in any case): switched to first, straight away when it has
     * no PIN, else through "Who's watching?", which asks for it. [askFirst] (a link from another app) always goes
     * through "Who's watching?", so another app can't change who's watching by itself.
     */
    data class AsProfile(val profile: String, val link: AppLink, val askFirst: Boolean = false) : AppLink

    val uri: Uri
        get() = when (this) {
            is Item -> Uri.parse("flexarr://item/${Uri.encode(id)}")
            is Play -> Uri.parse("flexarr://play/${Uri.encode(id)}").buildUpon().apply {
                startAt?.let { appendQueryParameter("t", it.toString()) }
                if (shuffle) appendQueryParameter("shuffle", "1")
            }.build()
            is Live -> Uri.parse("flexarr://live/${Uri.encode(channelId)}")
            LiveTv -> Uri.parse("flexarr://livetv")
            is Podcast -> Uri.parse("flexarr://podcast/${Uri.encode(id)}")
            is PodcastEpisode -> Uri.parse("flexarr://episode/${Uri.encode(id)}")
            Search -> Uri.parse("flexarr://search")
            Downloads -> Uri.parse("flexarr://downloads")
            Home -> Uri.parse("flexarr://home")
            Watchlist -> Uri.parse("flexarr://watchlist")
            Settings -> Uri.parse("flexarr://settings")
            is Pairing -> Uri.parse("flexarr://pairing/${Uri.encode(code)}")
            is AsProfile -> link.uri.buildUpon().appendQueryParameter("profile", profile).build()
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
                prefs.getString(SECRET, null) ?: UUID.randomUUID().toString().also { prefs.edit { putString(SECRET, it) } }
            }.also { cachedSecret = it }
        }

        private const val SECRET = "secret"

        /**
         * The link [intent] opened the app with. One that didn't come from this app (another app sent it) never
         * starts anything playing: a title opens its page instead, a channel the Live TV guide, and a profile it
         * names is chosen in "Who's watching?", with its PIN if it has one.
         */
        fun received(context: Context, intent: Intent): AppLink? {
            val link = parse(intent.data) ?: return null
            if (intent.getStringExtra(FROM_APP) == secret(context)) return link
            return fromOutside(link)
        }

        private fun fromOutside(link: AppLink): AppLink? = when (link) {
            is Play -> Item(link.id)
            is Live -> LiveTv
            // An episode has no page of its own to open by its id alone.
            is PodcastEpisode -> null
            // Only Flexarr's own notification asks whether to let a device in.
            is Pairing -> null
            is AsProfile -> fromOutside(link.link)?.let { AsProfile(link.profile, it, askFirst = true) }
            else -> link
        }

        fun parse(uri: Uri?): AppLink? {
            if (uri?.scheme != "flexarr") return null
            // Ids are the server's (GUIDs): anything else could reach another of its paths once put in one.
            val id = uri.pathSegments.firstOrNull()?.takeIf(FlexarrClient::isPlainId)
            val link = when (uri.host) {
                "item" -> id?.let(::Item)
                "play" -> id?.let {
                    Play(
                        it,
                        startAt = uri.getQueryParameter("t")?.toDoubleOrNull()?.takeIf { t -> t.isFinite() && t >= 0 }?.toInt(),
                        shuffle = uri.getQueryParameter("shuffle") == "1",
                    )
                }
                "live" -> id?.let(::Live)
                "livetv" -> LiveTv
                "podcast" -> id?.let(::Podcast)
                "episode" -> id?.let(::PodcastEpisode)
                "search" -> Search
                "downloads" -> Downloads
                "home" -> Home
                "watchlist" -> Watchlist
                "settings" -> Settings
                "pairing" -> uri.pathSegments.firstOrNull()?.takeIf { code -> code.length <= MAX_CODE && code.all { it.isLetterOrDigit() || it == '-' } }?.let(::Pairing)
                else -> null
            } ?: return null
            // The iPhone's `s=<subtitle key>` is let be: the player chooses subtitles as it always does.
            // A profile is only looked for among this server's, never sent anywhere, so its name may be anything.
            val profile = uri.getQueryParameter("profile")?.trim()?.takeIf { it.isNotEmpty() && it.length <= MAX_PROFILE }
            return profile?.let { AsProfile(it, link) } ?: link
        }

        private const val MAX_PROFILE = 100
        private const val MAX_CODE = 32
    }
}
