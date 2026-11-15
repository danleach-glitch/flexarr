package io.github.flexarr.core

/**
 * What the server is still to be told about downloads (one has come, one was deleted), each kept with the profile it's
 * of. The server answers about a download only to a sign-in as its own profile (404 for anyone else's), so each waits
 * for a sign-in as that profile: told as someone else, it would only be dropped. As the iPhone app's `ProfileTells`.
 */
object ProfileTells {
    /** Between the profile and the download's id: neither ever holds it. */
    private const val SEPARATOR = '\u001f'

    /** Download [id], to be told as [profileId]. */
    fun entry(id: String, profileId: String): String = "$profileId$SEPARATOR$id"

    /** An entry's profile (null for one kept before entries said whose they were) and download id. */
    fun parse(entry: String): Pair<String?, String> {
        val at = entry.indexOf(SEPARATOR)
        return if (at < 0) null to entry else entry.substring(0, at) to entry.substring(at + 1)
    }

    /**
     * The entries a sign-in as [profileId] tells now, each with its download id: its own, and any kept before entries
     * said whose they were (told by whoever's signed in, as they always were). None while nobody's chosen.
     */
    fun due(entries: Collection<String>, profileId: String?): List<Pair<String, String>> {
        if (profileId == null) return emptyList()
        return entries.mapNotNull { entry ->
            val (owner, id) = parse(entry)
            if (owner == null || owner == profileId) entry to id else null
        }
    }
}
