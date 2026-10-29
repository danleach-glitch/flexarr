package io.github.flexarr.ui.components

import io.github.flexarr.core.FlexarrException
import io.github.flexarr.logging.FlexarrLog
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.runtime.rememberUpdatedState
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LifecycleResumeEffect
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.lifecycle.repeatOnLifecycle
import kotlinx.coroutines.delay
import kotlin.coroutines.cancellation.CancellationException

private val flexarrLog = FlexarrLog("screens")

/** Something a screen fetches: still coming, here, or failed with a message to show. */
sealed interface Load<out T> {
    data object Loading : Load<Nothing>
    data class Loaded<T>(val value: T) : Load<T>
    data class Failed(val message: String) : Load<Nothing>
}

/** [refreshing]: fetching again with the last answer still on screen (pull to refresh shows it). */
class Loader<T>(val state: Load<T>, val refreshing: Boolean, val retry: () -> Unit)

/**
 * How long a screen's answer counts as current, as on iPhone. The app stays alive for days, and changes made
 * elsewhere (watching on the web, a new episode) reach a screen only when it fetches again.
 */
object Freshness {
    const val MAX_AGE_MS = 60_000L

    fun isStale(loadedAt: Long?): Boolean = loadedAt == null || System.currentTimeMillis() - loadedAt > MAX_AGE_MS
}

/**
 * Calls [refresh] when the screen comes back (from a title opened on top of it, another tab, or the app being away)
 * with an answer more than a minute old, fetched at [loadedAt]; null means nothing is on screen yet, so nothing to refresh.
 */
@Composable
fun RefreshWhenStale(loadedAt: Long?, refresh: () -> Unit) {
    LifecycleResumeEffect(loadedAt) {
        if (loadedAt != null && Freshness.isStale(loadedAt)) refresh()
        onPauseOrDispose {}
    }
}

/**
 * Runs [action] now and then every [periodMs] while the screen is showing, starting over when it comes back: nothing is
 * polled for a screen under another one, or while the app is away.
 */
@Composable
fun RepeatWhileShowing(periodMs: Long, vararg keys: Any?, action: suspend () -> Unit) {
    val lifecycle = LocalLifecycleOwner.current.lifecycle
    val current by rememberUpdatedState(action)
    LaunchedEffect(lifecycle, *keys) {
        lifecycle.repeatOnLifecycle(Lifecycle.State.STARTED) {
            while (true) {
                current()
                delay(periodMs)
            }
        }
    }
}

/**
 * Runs [load] now and again whenever [keys] change, or the screen comes back to an answer more than a minute old.
 * Reloading keeps what's on screen until the new answer comes, so coming back from the player doesn't flash a
 * spinner, and a reload that fails leaves it there.
 */
@Composable
fun <T> rememberLoader(vararg keys: Any?, load: suspend () -> T): Loader<T> {
    var state by remember { mutableStateOf<Load<T>>(Load.Loading) }
    var attempt by remember { mutableIntStateOf(0) }
    var refreshing by remember { mutableStateOf(false) }
    var loadedAt by remember { mutableStateOf<Long?>(null) }
    RefreshWhenStale(loadedAt) { attempt++ }
    LaunchedEffect(*keys, attempt) {
        val shown = state is Load.Loaded
        if (!shown) state = Load.Loading
        refreshing = shown
        try {
            state = Load.Loaded(load())
            loadedAt = System.currentTimeMillis()
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            // The server out of reach is expected now and then (a home server, away from home): not worth a warning.
            if (e is FlexarrException.Unreachable) flexarrLog.info("Couldn't load", e) else flexarrLog.warning("Couldn't load", e)
            if (!shown) state = Load.Failed(e.message ?: "Something went wrong.")
        } finally {
            refreshing = false
        }
    }
    // The same Loader (and Try again) until something changes, so what's handed it doesn't recompose for nothing.
    val retry: () -> Unit = remember { { attempt++ } }
    return remember(state, refreshing) { Loader(state, refreshing, retry) }
}

/** Shows [content] once loaded, a spinner until then, and the error with Try again if it fails. */
@Composable
fun <T> Loaded(loader: Loader<T>, modifier: Modifier = Modifier, content: @Composable (T) -> Unit) {
    when (val state = loader.state) {
        is Load.Loaded -> content(state.value)
        Load.Loading -> StatusView(modifier = modifier)
        is Load.Failed -> StatusView(state.message, loader.retry, modifier = modifier)
    }
}
