package io.github.flexarr.core

import kotlinx.serialization.Serializable
import java.net.URI

/**
 * Watching done from a download that hasn't reached the server yet, kept apart from the download (which signing out
 * deletes) until it's sent: only to the server and profile it was watched as, since the main profile has the same id
 * on every server. As FlexarrKit's `UnsentReport`.
 */
@Serializable
data class UnsentReport(
    /** The server, as [UnsentReports.serverKey] writes its address. */
    val server: String,
    val profileId: String,
    val itemId: String,
    val position: Double,
    val finished: Boolean,
    val playedAtMillis: Long,
    /** How many times the server has said it has no such title (see [UnsentReports.notFoundAgain]). */
    val notFound: Int = 0,
) {
    /** The same title, watched as the same profile on the same server. */
    fun isFor(other: UnsentReport): Boolean = server == other.server && profileId == other.profileId && itemId == other.itemId
}

/** The rules for reports of watching done offline that are waiting to be sent, as FlexarrKit's `UnsentReports`. */
object UnsentReports {
    /**
     * A title the server says it hasn't (404) is tried this many times, on separate catch-ups, before its report is
     * dropped: once could be a library mid-rescan, but every time for ever is a report that never goes.
     */
    const val NOT_FOUND_LIMIT = 3

    /** How a server is told apart: its address, without a trailing slash, its scheme and host in lower case. */
    fun serverKey(address: String): String {
        val text = address.trimEnd('/')
        val uri = runCatching { URI(text) }.getOrNull() ?: return text.lowercase()
        val scheme = uri.scheme?.lowercase() ?: return text.lowercase()
        val host = uri.host?.lowercase() ?: return text.lowercase()
        val port = if (uri.port >= 0) ":${uri.port}" else ""
        return "$scheme://$host$port${uri.rawPath.orEmpty()}"
    }

    /** [kept] with [new] added: one per title, server and profile, the one played most recently. */
    fun adding(new: List<UnsentReport>, kept: List<UnsentReport>): List<UnsentReport> {
        val result = kept.toMutableList()
        for (report in new) {
            val index = result.indexOfFirst { it.isFor(report) }
            if (index < 0) result += report else if (report.playedAtMillis >= result[index].playedAtMillis) result[index] = report
        }
        return result
    }

    /** The ones a sign-in to [server] as [profileId] sends: none while either isn't known. */
    fun due(kept: List<UnsentReport>, server: String?, profileId: String?): List<UnsentReport> {
        if (server == null || profileId == null) return emptyList()
        return kept.filter { it.server == server && it.profileId == profileId }
    }

    /** [kept] without [report], once it's been sent (or dropped): a newer one for the same title, recorded meanwhile, stays. */
    fun removing(report: UnsentReport, kept: List<UnsentReport>): List<UnsentReport> =
        kept.filterNot { it.isFor(report) && it.playedAtMillis <= report.playedAtMillis }

    /** The server said it has no such title, [count] times before: the count to keep, or null once it's had its tries. */
    fun notFoundAgain(count: Int): Int? = if (count + 1 >= NOT_FOUND_LIMIT) null else count + 1
}
