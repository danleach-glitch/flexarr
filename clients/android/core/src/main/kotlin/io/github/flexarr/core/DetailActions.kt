package io.github.flexarr.core

/**
 * The film, show and episode page's actions, in the order every client shows them (design.md, Platforms): Play or
 * Resume, Start over, Shuffle, Trailer, Watched, Watchlist, Download, then ⋯ with the rest. FlexarrKit's
 * `DetailActions` holds the same rules for the Apple apps; change both together.
 *
 * The phones show at most [PHONE_SLOTS] buttons beside Play (plus ⋯), so the row fits like an episode's: Start over and
 * Download always stay, and the rest fill what's left by where you are with the title. Before you start: Trailer,
 * Watchlist, then Watched; once you've started or watched it: Watched, Watchlist, then Trailer; Shuffle last. Whatever
 * doesn't fit moves to the top of ⋯, in the same order.
 */
object DetailActions {
    enum class Kind { MOVIE, SHOW, EPISODE }

    enum class Platform { PHONE, TV }

    enum class Button { PLAY, START_OVER, SHUFFLE, TRAILER, WATCHED, WATCHLIST, DOWNLOAD, MORE }

    /** How many buttons a phone's row holds beside Play and ⋯. */
    const val PHONE_SLOTS = 3

    /** What the ⋯ menu holds: only what the app can already do. */
    sealed interface MenuItem {
        /**
         * A button that didn't fit on a phone's row: Shuffle, Trailer, Watched (Mark as watched or unwatched) or
         * Watchlist (Add to or Remove from watchlist). Start over and Download never move.
         */
        data class Moved(val button: Button) : MenuItem
        data object Subtitles : MenuItem
        data object GoToShow : MenuItem
    }

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
        /** It can go on a watchlist here: films and shows, from servers that have one. */
        val canWatchlist: Boolean = true,
    )

    data class Actions(val buttons: List<Button>, val menu: List<MenuItem>)

    fun of(input: Input): Actions {
        var row = buildList {
            if (input.hasProgress) add(Button.START_OVER)
            if (input.kind == Kind.SHOW) add(Button.SHUFFLE)
            if (input.hasTrailer && !input.isKids && input.canPlayTrailers) add(Button.TRAILER)
            // A child's watched marks follow what they watch, not what they press.
            if (!input.isKids) add(Button.WATCHED)
            if (!input.isKids && input.canWatchlist) add(Button.WATCHLIST)
            if (input.platform == Platform.PHONE && input.canDownload) add(Button.DOWNLOAD)
        }
        var moved = emptyList<MenuItem>()
        if (input.platform == Platform.PHONE && row.size > PHONE_SLOTS) {
            val pinned = row.filter { it == Button.START_OVER || it == Button.DOWNLOAD }
            val started = input.hasProgress || input.isWatched
            val priority = if (started) {
                listOf(Button.WATCHED, Button.WATCHLIST, Button.TRAILER, Button.SHUFFLE)
            } else {
                listOf(Button.TRAILER, Button.WATCHLIST, Button.WATCHED, Button.SHUFFLE)
            }
            val kept = (pinned + priority.filter { it in row }.take((PHONE_SLOTS - pinned.size).coerceAtLeast(0))).toSet()
            moved = row.filterNot { it in kept }.map { MenuItem.Moved(it) }
            row = row.filter { it in kept }
        }
        val menu = buildList {
            addAll(moved)
            if (input.canSearchSubtitles && !input.isKids) add(MenuItem.Subtitles)
            if (input.kind == Kind.EPISODE) add(MenuItem.GoToShow)
        }
        return Actions(listOf(Button.PLAY) + row + if (menu.isEmpty()) emptyList() else listOf(Button.MORE), menu)
    }
}

private val YOUTUBE_ID = Regex("^[A-Za-z0-9_-]{11}$")

/** [raw] when it's a YouTube video id (eleven letters, digits, - and _), else null. */
fun youTubeId(raw: String?): String? = raw?.takeIf { YOUTUBE_ID.matches(it) }
