package io.github.flexarr.core

/**
 * The film, show and episode page's actions, in the order every client shows them (design.md, Platforms): Play or
 * Resume, Start over, Shuffle, Trailer, Watched, Watchlist, Download, then ⋯ with the rest. FlexarrKit's
 * `DetailActions` holds the same rules for the Apple apps; change both together.
 */
object DetailActions {
    enum class Kind { MOVIE, SHOW, EPISODE }

    enum class Platform { PHONE, TV }

    enum class Button { PLAY, START_OVER, SHUFFLE, TRAILER, WATCHED, WATCHLIST, DOWNLOAD, MORE }

    /**
     * What the ⋯ menu holds: only what the app can already do. [MARK_UNWATCHED] is for part way through and not
     * watched: forget the place without marking it watched (Watched only marks it watched then), so it's never the
     * same thing twice; once it's watched, Watched itself becomes Mark as unwatched and this goes.
     */
    enum class MenuItem { MARK_UNWATCHED, SUBTITLES, GO_TO_SHOW }

    data class Input(
        val kind: Kind,
        /** Part watched and not finished: for a show, the episode Play would start. */
        val hasProgress: Boolean,
        val isWatched: Boolean,
        val hasTrailer: Boolean,
        val isKids: Boolean,
        val platform: Platform,
        /** A trailer can play here: on a TV, something (the YouTube app) opens it. */
        val canPlayTrailers: Boolean = true,
        /** The phone can keep a copy of it. */
        val canDownload: Boolean = false,
        /** The server can search for subtitles for it (it has a file and a subtitle source). */
        val canSearchSubtitles: Boolean = false,
    )

    data class Actions(val buttons: List<Button>, val menu: List<MenuItem>)

    fun of(input: Input): Actions {
        val buttons = buildList {
            add(Button.PLAY)
            if (input.hasProgress) add(Button.START_OVER)
            if (input.kind == Kind.SHOW) add(Button.SHUFFLE)
            if (input.hasTrailer && !input.isKids && input.canPlayTrailers) add(Button.TRAILER)
            // A child's watched marks follow what they watch, not what they press.
            if (!input.isKids) add(Button.WATCHED)
            // Only what the server can put on a watchlist shows it (films and shows, from servers that have one): the page's call.
            if (!input.isKids) add(Button.WATCHLIST)
            if (input.platform == Platform.PHONE && input.canDownload) add(Button.DOWNLOAD)
        }
        val menu = buildList {
            if (!input.isKids && input.hasProgress && !input.isWatched) add(MenuItem.MARK_UNWATCHED)
            if (input.canSearchSubtitles && !input.isKids) add(MenuItem.SUBTITLES)
            if (input.kind == Kind.EPISODE) add(MenuItem.GO_TO_SHOW)
        }
        return Actions(if (menu.isEmpty()) buttons else buttons + Button.MORE, menu)
    }
}

private val YOUTUBE_ID = Regex("^[A-Za-z0-9_-]{11}$")

/** [raw] when it's a YouTube video id (eleven letters, digits, - and _), else null. */
fun youTubeId(raw: String?): String? = raw?.takeIf { YOUTUBE_ID.matches(it) }
