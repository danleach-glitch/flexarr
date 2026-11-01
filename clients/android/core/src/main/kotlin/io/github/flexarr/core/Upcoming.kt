package io.github.flexarr.core

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
                return if (sameSeason) "${first.code}–E${items.last().episodeNumber ?: 0}, ${items.size} episodes" else "${items.size} episodes"
            }

        /** Ready to watch, or how far its download has got; null otherwise. */
        val status: String?
            get() {
                if (items.size < 2) return first.status
                if (items.all { it.available == true }) return "Ready to watch"
                return if (items.any { it.download != null }) "Downloading" else null
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
        0L -> "Today"
        1L -> "Tomorrow"
        -1L -> "Yesterday"
        else -> day.format(DateTimeFormatter.ofPattern("EEE d MMM"))
    }
}

/** "S1E3", with no zero padding. */
val UpcomingItem.code: String get() = "S${seasonNumber ?: 0}E${episodeNumber ?: 0}"

/** Ready to watch, or how far its download has got; null otherwise. */
val UpcomingItem.status: String?
    get() = when {
        available == true -> "Ready to watch"
        download != null -> "Downloading ${Math.round(download.progress * 100)}%"
        else -> null
    }
