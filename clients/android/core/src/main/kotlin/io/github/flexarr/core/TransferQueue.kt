package io.github.flexarr.core

/**
 * How many downloads asked for by hand come down at once. A whole show asked for at once used to start a transfer (a
 * job or a worker, each with its notification, asking after the server every half minute) for every episode; now a few
 * run for each profile and the rest wait their turn ([LocalDownload.queued]), saved with the list, so they carry on in
 * order after the app or the phone restarts. Downloads fetched by themselves (the next episode, podcasts' newest) are
 * few, and may be waiting for Wi-Fi: they neither count nor wait.
 */
object TransferQueue {
    /** Transfers asked for by hand under way at once, for each profile. */
    const val MOST_RUNNING = 3

    /** On its way with a transfer of its own (or one to come), asked for by hand: it takes one of the places. */
    private fun takesPlace(download: LocalDownload): Boolean = download.isPending && !download.queued && !download.automatic

    /** Whether a download asked for by hand now, for [profileId], waits its turn. */
    fun waits(profileId: String, downloads: List<LocalDownload>, most: Int = MOST_RUNNING): Boolean =
        downloads.count { it.profileId == profileId && takesPlace(it) } >= most

    /** The waiting downloads to start now: for each profile, the first asked for, as many as there are places free. */
    fun toStart(downloads: List<LocalDownload>, most: Int = MOST_RUNNING): List<LocalDownload> =
        downloads.groupBy { it.profileId }.flatMap { (_, theirs) ->
            val free = most - theirs.count(::takesPlace)
            if (free <= 0) emptyList() else theirs.filter { it.queued && it.isPending }.sortedBy { it.createdAtMillis }.take(free)
        }

    /** Waiting its turn on this phone: shows as Queued, with no transfer yet. */
    fun isQueued(download: LocalDownload): Boolean = download.queued && download.isPending
}
