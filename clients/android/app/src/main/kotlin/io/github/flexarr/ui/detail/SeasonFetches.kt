package io.github.flexarr.ui.detail

import androidx.compose.runtime.mutableStateMapOf
import io.github.flexarr.core.ItemDetail
import io.github.flexarr.core.i18n.t
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CompletableDeferred

/**
 * A show page's seasons, each with its episodes, as fetched for it (phone and TV alike). A season is only ever asked
 * for once at a time: the page's load and the season being chosen both want the one it opens on, and the second waits
 * for the first rather than asking again.
 */
internal class SeasonFetches(private val fetch: suspend (String) -> ItemDetail) {
    val seasons = mutableStateMapOf<String, ItemDetail>()
    /** Seasons whose episodes couldn't be fetched, with why: said in the episode area with Try again, not a spinner. */
    val failed = mutableStateMapOf<String, String>()
    private val inFlight = HashMap<String, CompletableDeferred<Unit>>()

    /**
     * A season as it is now: fetched again (the page loading again after something was watched, say), even with an
     * older fetch of it still out, whose answer may be from before.
     */
    suspend fun refresh(season: String) {
        val done = CompletableDeferred<Unit>()
        inFlight[season] = done
        try {
            try {
                seasons[season] = fetch(season)
                failed.remove(season)
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                if (season !in seasons) failed[season] = e.message ?: t("Something went wrong.")
            }
        } finally {
            if (inFlight[season] === done) inFlight.remove(season)
            done.complete(Unit)
        }
    }

    /** A season chosen: fetched unless it's here already or on its way. */
    suspend fun ensure(season: String) {
        inFlight[season]?.await()
        if (season !in seasons) refresh(season)
    }

    /** A season that came another way (the page opened on it). */
    fun put(season: ItemDetail) {
        seasons[season.item.id] = season
    }
}
