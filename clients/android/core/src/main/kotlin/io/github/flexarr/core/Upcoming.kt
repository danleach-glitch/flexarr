package io.github.flexarr.core

import io.github.flexarr.core.i18n.Words
import io.github.flexarr.core.i18n.t
import java.time.LocalDate
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.time.temporal.ChronoUnit

/**
 * The Coming soon page: what's coming, a day at a time, and a show's episodes on the same day (a season dropping at
 * once) as one entry, as on the web and the Apple apps (UpcomingDay).
 */
object UpcomingDays {
    data class Day(val date: LocalDate, val entries: List<Entry>)

    data class Entry(val items: List<UpcomingItem>) {
        val first: UpcomingItem get() = items.first()
        val title: String get() = first.cardTitle

        /** "S1E3 Title", "S2E1–E8, 8 episodes", or how a film comes out. */
        val detail: String
            get() {
                if (items.size < 2) return first.cardDetail
                val sameSeason = items.all { it.seasonNumber == first.seasonNumber }
                return if (sameSeason) {
                    t(
                        "S{season}E{episode}–E{last}, {count, plural, one {# episode} other {# episodes}}",
                        "season" to (first.seasonNumber ?: 0).toString(), "episode" to (first.episodeNumber ?: 0).toString(),
                        "last" to (items.last().episodeNumber ?: 0).toString(), "count" to items.size,
                    )
                } else {
                    t("{count, plural, one {# episode} other {# episodes}}", "count" to items.size)
                }
            }

        /** Ready to watch, or how far its download has got; null otherwise. */
        val status: String?
            get() {
                if (items.size < 2) return first.status
                if (items.all { it.available == true }) return t("Ready to watch")
                return if (items.any { it.download != null }) t("Downloading") else null
            }

        /** Opening it does something: it's in the library, or there's an overview to read. */
        val hasAction: Boolean get() = first.libraryItemId != null || !first.overview.isNullOrBlank()
    }

    /** [items] from yesterday on, by day, in time order. */
    fun days(items: List<UpcomingItem>, zone: ZoneId = ZoneId.systemDefault(), today: LocalDate = LocalDate.now(zone)): List<Day> {
        val from = today.minusDays(1)
        return items.sortedBy { it.date }
            .groupBy { it.date.atZone(zone).toLocalDate() }
            .filterKeys { it >= from }
            .toSortedMap()
            .map { (day, onDay) ->
                val groups = LinkedHashMap<String, MutableList<UpcomingItem>>()
                onDay.forEach { item ->
                    val key = if (item.kind == MediaKind.Episode) item.seriesTitle?.let { "series:$it" } ?: item.id else item.id
                    groups.getOrPut(key) { mutableListOf() } += item
                }
                Day(day, groups.values.map { Entry(it) })
            }
    }

    /** A day's heading: "Today", "Tomorrow", "Yesterday", otherwise "Fri 7 Nov". */
    fun heading(day: LocalDate, today: LocalDate = LocalDate.now()): String = when (ChronoUnit.DAYS.between(today, day)) {
        0L -> t("Today")
        1L -> t("Tomorrow")
        -1L -> t("Yesterday")
        else -> day.format(DateTimeFormatter.ofPattern("EEE d MMM", io.github.flexarr.core.i18n.Localization.locale))
    }
}

/** "S1E3", with no zero padding. */
val UpcomingItem.code: String get() = t(Words.seasonEpisodeShort, "season" to (seasonNumber ?: 0).toString(), "episode" to (episodeNumber ?: 0).toString())

/** Ready to watch, or how far its download has got; null otherwise. */
val UpcomingItem.status: String?
    get() = when {
        available == true -> t("Ready to watch")
        download != null -> t("Downloading {percent}%", "percent" to Math.round(download.progress * 100))
        else -> null
    }
