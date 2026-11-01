package io.github.flexarr.core

import java.util.Locale

// The words every screen shows, as FlexarrKit's Presentation.swift words them for the Apple apps. Wording follows
// docs/design.md: plain words ("Resume from 32:10"), no middle-dot meta strings.

object Format {
    /** "1h 56m", "47m", "2h". */
    fun runtime(minutes: Int): String? {
        if (minutes <= 0) return null
        val h = minutes / 60
        val m = minutes % 60
        return when {
            h == 0 -> "${m}m"
            m == 0 -> "${h}h"
            else -> "${h}h ${m}m"
        }
    }

    fun runtime(seconds: Double): String? = runtime(Math.round(seconds / 60).toInt())

    /** Player-style timestamp: "32:10", "1:02:05". */
    fun timestamp(seconds: Double): String {
        val total = seconds.toInt().coerceAtLeast(0)
        val h = total / 3600
        val m = (total % 3600) / 60
        val s = total % 60
        return if (h > 0) String.format(Locale.ROOT, "%d:%02d:%02d", h, m, s) else String.format(Locale.ROOT, "%d:%02d", m, s)
    }

    /** "42m left", "1h 3m left". */
    /** "1,204", in the device's own way of grouping digits. */
    fun count(value: Int): String = java.text.NumberFormat.getIntegerInstance().format(value)

    fun remaining(seconds: Double): String? = if (seconds < 60) null else runtime(seconds)?.let { "$it left" }
}

/** Duration in seconds from the probed file, falling back to the metadata's runtime. */
val ItemSummary.effectiveDuration: Double?
    get() = durationSeconds?.takeIf { it > 0 } ?: runtimeMinutes?.takeIf { it > 0 }?.let { it * 60.0 } ?: book?.durationSeconds?.takeIf { it > 0 }

/** Fraction watched (0 to 1) when the item has a resume point, otherwise null. */
val ItemSummary.progress: Double?
    get() {
        val duration = effectiveDuration ?: return null
        if (userData.watched || userData.positionSeconds <= 0) return null
        return (userData.positionSeconds / duration).coerceIn(0.0, 1.0)
    }

val ItemSummary.isResumable: Boolean get() = progress != null

/** "S2 E3", "S1 E1–E2", or null for anything but episodes, and for home videos (which go by their names). */
val ItemSummary.episodeCode: String?
    get() {
        if (kind != MediaKind.Episode || homeVideo == true) return null
        val episode = episodeNumber ?: return null
        val season = seasonNumber?.let { "S$it " }.orEmpty()
        val end = episodeNumberEnd
        return if (end != null && end > episode) "${season}E$episode–E$end" else "${season}E$episode"
    }

/** The headline for cards and the player: the series name for episodes, else the title. */
val ItemSummary.displayTitle: String get() = if (kind == MediaKind.Episode) seriesTitle ?: title else title

/** The second line for episodes: "S2 E3  Bit by a Dead Bee". */
val ItemSummary.episodeSubtitle: String?
    get() = if (kind != MediaKind.Episode) null else listOfNotNull(episodeCode, title).joinToString("  ")

/** A book or podcast episode: listened to, with square artwork, in Continue watching or listening. */
val ItemSummary.isListening: Boolean get() = kind == MediaKind.Audiobook || kind == MediaKind.PodcastEpisode

/** The second line for a book (its authors) or a podcast episode (its podcast); null for anything else. */
val ItemSummary.listeningSubtitle: String?
    get() = when (kind) {
        MediaKind.Audiobook -> book?.authorLine
        MediaKind.PodcastEpisode -> seriesTitle?.takeIf { it.isNotBlank() }
        else -> null
    }

val ItemSummary.releaseYear: Int?
    get() = year ?: releaseDate?.take(4)?.toIntOrNull() ?: airDate?.take(4)?.toIntOrNull()

/** Short facts for heroes and detail headers: year, runtime or seasons, rating, score, resolution; [scores] false (kids profiles) leaves the score out. */
fun ItemSummary.facts(scores: Boolean = true): List<String> =
    buildList {
        releaseYear?.let { add(it.toString()) }
        fun count(n: Int, noun: String) = add(if (n == 1) "1 $noun" else "$n ${noun}s")
        when (kind) {
            MediaKind.Series -> if (childCount > 0) count(childCount, if (homeVideo == true) "folder" else "season")
            MediaKind.Season -> if (childCount > 0) count(childCount, if (homeVideo == true) "video" else "episode")
            MediaKind.Artist -> if (childCount > 0) count(childCount, "album")
            MediaKind.Album -> if (childCount > 0) count(childCount, "track")
            else -> (runtimeMinutes?.let(Format::runtime) ?: durationSeconds?.let(Format::runtime))?.let(::add)
        }
        contentRating?.takeIf { it.isNotEmpty() }?.let(::add)
        if (scores) communityRating?.takeIf { it > 0 }?.let { add(String.format(Locale.ROOT, "%.1f", it)) }
        resolution?.takeIf { it.isNotEmpty() }?.let(::add)
    }

/** What the player should offer at a given moment, from the server's markers. */
sealed interface PlaybackPrompt {
    data object None : PlaybackPrompt
    /** Inside an intro or recap: offer to jump to [to]. */
    data class Skip(val type: MarkerType, val to: Double) : PlaybackPrompt
    /** The credits have started and there is a next episode. */
    data object NextEpisode : PlaybackPrompt

    companion object {
        fun at(time: Double, markers: List<Marker>, duration: Double, hasNext: Boolean): PlaybackPrompt {
            for (marker in markers) {
                if (marker.type != MarkerType.Intro && marker.type != MarkerType.Recap) continue
                // Stop offering the skip in the last second so it doesn't flash as the marker ends.
                if (time >= marker.start && time < marker.end - 1) return Skip(marker.type, marker.end)
            }
            if (!hasNext) return None
            markers.firstOrNull { it.type == MarkerType.Credits }?.let { credits ->
                return if (time >= credits.start) NextEpisode else None
            }
            // No credits marker found: offer the next episode for the end of it, where credits usually are.
            return if (duration > 120 && time >= duration - unmarkedCreditsSeconds(duration)) NextEpisode else None
        }

        /**
         * How long before the end the next episode is offered when no credits were found: 3% of the episode, between
         * 30 seconds and 2 minutes (about 40 seconds of a half-hour show, 100 of an hour-long one).
         */
        fun unmarkedCreditsSeconds(duration: Double): Double = (duration * 0.03).coerceIn(30.0, 120.0)
    }
}

fun PlaybackInfo.prompt(at: Double): PlaybackPrompt = PlaybackPrompt.at(at, markers, durationSeconds, next != null)

/**
 * What Home says when it has nothing to show: no library yet, a first scan under way, or libraries whose folders hold
 * nothing Flexarr recognised. Libraries are added and connected in a browser, so the wording points there. Null while
 * some library has something in it; podcasts come built in, so they don't count.
 */
data class EmptyHomeMessage(val title: String, val body: String, val folders: List<String>) {
    companion object {
        fun of(libraries: List<Library>): EmptyHomeMessage? {
            val media = libraries.filter { it.type != LibraryType.Podcasts }
            if (media.any { it.itemCount > 0 }) return null
            if (media.isEmpty()) {
                return EmptyHomeMessage(
                    "Nothing to watch yet",
                    "Open Flexarr in a browser and add a library in Settings › Libraries.",
                    emptyList(),
                )
            }
            val names = list(media.map { it.name })
            val wanted = list(media.map { kind(it.type) }.distinct())
            val scanning = media.mapNotNull { it.scan }
            if (scanning.isNotEmpty()) {
                val total = scanning.sumOf { it.total }
                val done = scanning.sumOf { it.processed }
                val progress = if (total > 0) " ($done of $total)" else ""
                return EmptyHomeMessage(
                    "Looking through your library",
                    "Flexarr is reading the files in $names$progress. Your $wanted appear here as they’re recognised.",
                    emptyList(),
                )
            }
            val folders = media.flatMap { it.folders }
            val where = if (folders.size > 1) "your library folders" else "your library folder"
            return EmptyHomeMessage(
                "Your library is empty",
                "Add files to $where, or connect Sonarr or Radarr in Flexarr on the web.",
                folders,
            )
        }

        private fun kind(type: LibraryType) = when (type) {
            LibraryType.Movies -> "films"
            LibraryType.TvShows -> "shows"
            LibraryType.Music -> "music"
            LibraryType.Podcasts -> "podcasts"
            LibraryType.HomeVideos -> "home videos"
            LibraryType.Audiobooks -> "audiobooks"
            LibraryType.Unknown -> "titles"
        }

        /** "Films", "Films and TV", "Films, TV and Music". */
        private fun list(names: List<String>): String =
            if (names.size <= 1) names.firstOrNull().orEmpty() else names.dropLast(1).joinToString(", ") + " and " + names.last()
    }
}

object ServerVersion {
    /** "0.1.0+d4c892c6190c…" reads better as "0.1.0 (d4c892c)". */
    fun short(version: String): String {
        val parts = version.split("+", limit = 2)
        return if (parts.size == 2) "${parts[0]} (${parts[1].take(7)})" else version
    }

    /** Whether [version] ("0.53.0", "0.54.0-dev.1a2b3c4", "0.53.1+d4c8…") is [major].[minor].[patch] or later. */
    fun atLeast(version: String, major: Int, minor: Int, patch: Int = 0): Boolean {
        val numbers = version.substringBefore('+').substringBefore('-').split('.').map { it.toIntOrNull() ?: return false }
        if (numbers.size != 3) return false
        return compareValuesBy(numbers, listOf(major, minor, patch), { it[0] }, { it[1] }, { it[2] }) >= 0
    }
}

/** The A–Z rail's letters: "#" for numbers and symbols, then A to Z. */
val alphabetLetters: List<String> = listOf("#") + ('A'..'Z').map { it.toString() }

/**
 * A title's letter on the A–Z rail, by the server's sort title (SortTitle.For): trimmed, and without a leading "The",
 * "A" or "An" when something follows it; "#" for anything that doesn't start with a letter from A to Z.
 */
fun alphabetLetter(title: String): String {
    var rest = title.trim()
    for (article in listOf("the ", "a ", "an ")) {
        if (rest.length > article.length && rest.regionMatches(0, article, 0, article.length, ignoreCase = true)) {
            rest = rest.substring(article.length)
            break
        }
    }
    val first = rest.firstOrNull()?.uppercaseChar() ?: return "#"
    return if (first in 'A'..'Z') first.toString() else "#"
}

/**
 * Letters with nothing to jump to: none among the [loaded] titles' letters, and either every title is loaded
 * ([complete]) or the list has already gone past the letter. "#" also covers accented titles, which sort after Z, so
 * it's only known once everything is loaded.
 */
fun emptyAlphabetLetters(loaded: List<String>, complete: Boolean): Set<String> {
    val found = loaded.toSet()
    val last = loaded.lastOrNull() ?: "#"
    return alphabetLetters.filter { letter ->
        letter !in found && (complete || (letter != "#" && last != "#" && last > letter))
    }.toSet()
}
