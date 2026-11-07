package io.github.flexarr.downloads

import io.github.flexarr.core.LocalDownload

/**
 * The downloads list with each one found at once, by its id or by whose it is and what it's of, rather than by going
 * down the list: a playlist's page asks after every one of its tracks at each step of a transfer, which for a thousand
 * tracks was a million comparisons a time. Made once whenever the list changes. Where two entries share an id or a
 * title, the first in the list is the one found, as going down the list found it.
 */
internal class DownloadIndex(val all: List<LocalDownload>) {
    private val byId = HashMap<String, LocalDownload>(all.size * 2)
    private val byProfile = HashMap<String, HashMap<String, LocalDownload>>()

    init {
        for (download in all) {
            if (download.id !in byId) byId[download.id] = download
            val titles = byProfile.getOrPut(download.profileId) { HashMap() }
            if (download.item.id !in titles) titles[download.item.id] = download
        }
    }

    fun byId(id: String): LocalDownload? = byId[id]

    /** [profileId]'s download of the title [itemId]; with no profile, none. */
    fun entry(profileId: String?, itemId: String): LocalDownload? = profileId?.let(byProfile::get)?.get(itemId)
}
