package io.github.flexarr.ui.search

/**
 * What Search shows for the words in the field, on the phone and the TV. Results and failures are kept with the words
 * they answer, so an answer to "far" never stands for "fargo": a newer search that failed shows its failure, not the
 * older results, and with nothing answered yet it says it's searching.
 */
internal sealed interface SearchShown<out T> {
    /** Nothing typed. */
    data object Idle : SearchShown<Nothing>

    /** Typed, with nothing answered yet. */
    data object Searching : SearchShown<Nothing>

    /** The search for these words failed. */
    data class Failed(val message: String) : SearchShown<Nothing>

    /** The latest results, for [query]: perhaps the words before, while the newest are on their way. */
    data class Found<T>(val query: String, val results: T) : SearchShown<T>
}

internal fun <T> searchShown(words: String, results: Pair<String, T>?, failure: Pair<String, String>?): SearchShown<T> = when {
    words.isEmpty() -> SearchShown.Idle
    failure != null && failure.first == words -> SearchShown.Failed(failure.second)
    results != null -> SearchShown.Found(results.first, results.second)
    else -> SearchShown.Searching
}
