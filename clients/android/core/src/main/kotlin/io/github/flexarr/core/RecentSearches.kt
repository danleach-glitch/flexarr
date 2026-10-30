package io.github.flexarr.core

import kotlinx.serialization.Serializable

// What this profile searched for and opened from Search lately, the same on every device. Servers before 0.56 have
// none: they answer 404, read here as no recents.

/** The searches (up to 5) and the titles opened from them (up to 10), the latest first. */
@Serializable
data class RecentSearches(
    val queries: List<String> = emptyList(),
    val items: List<ItemSummary> = emptyList(),
) {
    val isEmpty: Boolean get() = queries.isEmpty() && items.isEmpty()
}

@Serializable
internal data class RecentSearchBody(val query: String?, val itemId: String?)

suspend fun FlexarrClient.recentSearches(): RecentSearches =
    try {
        get("search/recent")
    } catch (e: FlexarrException.Http) {
        if (e.status == 404) RecentSearches() else throw e
    }

/**
 * Remembers a pick from Search: the words searched, with the title when the pick was one (a person's just the words).
 * A title reopened from the recents themselves is just [itemId], which moves it to the front.
 */
suspend fun FlexarrClient.recordSearch(query: String?, itemId: String? = null) {
    send("POST", "search/recent", body = FlexarrClient.json.encodeToString(RecentSearchBody(query, itemId)))
}

suspend fun FlexarrClient.forgetSearch(query: String) {
    send("DELETE", "search/recent", mapOf("query" to query))
}

suspend fun FlexarrClient.forgetRecentItem(itemId: String) {
    send("DELETE", "search/recent", mapOf("itemId" to itemId))
}

suspend fun FlexarrClient.clearRecentSearches() {
    send("DELETE", "search/recent")
}
