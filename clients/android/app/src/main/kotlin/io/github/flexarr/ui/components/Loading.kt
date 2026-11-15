package io.github.flexarr.ui.components

import io.github.flexarr.core.i18n.t
import io.github.flexarr.core.FlexarrException
import io.github.flexarr.logging.FlexarrLog
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.currentCompositeKeyHashCode
import androidx.compose.ui.platform.LocalContext
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewmodel.compose.LocalViewModelStoreOwner
import androidx.lifecycle.viewmodel.compose.viewModel
import io.github.flexarr.FlexarrApplication
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
 * The answers a screen's [rememberLoader]s last had, kept while the screen is in the back stack (a ViewModel lives as
 * long as its NavHost entry), so coming back to it shows them at once rather than a spinner. Each is kept by its place
 * in the screen and what it was loaded for.
 */
internal class LoaderMemory : ViewModel() {
    /** [keys]: what [rememberLoader] was handed, and who was signed in. */
    class Kept(val keys: List<Any?>, val value: Any?, val loadedAt: Long)

    private val answers = HashMap<Any, Kept>()

    /**
     * What to show at [slot], loaded for [keys], on coming back: the answer kept there when it was loaded for the same
     * sign-in (the screen's own keys, a title's id say, don't change while it's in the back stack; a watched-state
     * version that moved meanwhile fetches again with it showing), or null.
     */
    fun restore(slot: Any, keys: List<Any?>): Kept? = answers[slot]?.takeIf { it.keys.lastOrNull() == keys.lastOrNull() }

    fun keep(slot: Any, kept: Kept) {
        answers[slot] = kept
    }

    companion object {
        /**
         * Whether the [kept] answer can stand on coming back, loaded for [keys], instead of being fetched again: for
         * just the same, and no more than a minute old ([Freshness]).
         */
        fun isCurrent(kept: Kept?, keys: List<Any?>): Boolean = kept != null && kept.keys == keys && !Freshness.isStale(kept.loadedAt)
    }
}

/**
 * Runs [load] now and again whenever [keys] change, or the screen comes back to an answer more than a minute old.
 * Reloading keeps what's on screen until the new answer comes, so coming back from the player doesn't flash a
 * spinner, and a reload that fails leaves it there (telling [onRefreshFailed], for a screen that says so).
 *
 * Back to the screen from one opened over it, the answer it had is shown straight away ([LoaderMemory]), and only
 * fetched again once it's more than a minute old or what it's loaded for has changed, as on iPhone.
 */
@Composable
fun <T> rememberLoader(vararg keys: Any?, onRefreshFailed: ((Exception) -> Unit)? = null, load: suspend () -> T): Loader<T> {
    val memory = LocalViewModelStoreOwner.current?.let { viewModel<LoaderMemory>(it) }
    val slot = currentCompositeKeyHashCode
    // Who's signed in, so one profile's answer is never shown to another (a child's leaves out what it may not see).
    val signIn = (LocalContext.current.applicationContext as? FlexarrApplication)?.signedInAs
    val identity = keys.toList() + listOf(signIn)
    val currentIdentity by rememberUpdatedState(identity)
    val kept = remember { memory?.restore(slot, identity) }
    @Suppress("UNCHECKED_CAST")
    var state by remember { mutableStateOf<Load<T>>(kept?.let { Load.Loaded(it.value as T) } ?: Load.Loading) }
    var attempt by remember { mutableIntStateOf(0) }
    var refreshing by remember { mutableStateOf(false) }
    var loadedAt by remember { mutableStateOf(kept?.loadedAt) }
    // Set until the first load has had its turn: a kept answer that's still current needs no fetching.
    val firstLoad = remember { booleanArrayOf(true) }
    val refreshFailed by rememberUpdatedState(onRefreshFailed)
    RefreshWhenStale(loadedAt) { attempt++ }
    LaunchedEffect(*keys, attempt) {
        val first = firstLoad[0].also { firstLoad[0] = false }
        if (first && attempt == 0 && LoaderMemory.isCurrent(kept, currentIdentity)) return@LaunchedEffect
        val shown = state is Load.Loaded
        if (!shown) state = Load.Loading
        refreshing = shown
        val loadingFor = currentIdentity
        try {
            val value = load()
            state = Load.Loaded(value)
            val now = System.currentTimeMillis()
            loadedAt = now
            memory?.keep(slot, LoaderMemory.Kept(loadingFor, value, now))
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            // The server out of reach is expected now and then (a home server, away from home): not worth a warning.
            if (e is FlexarrException.Unreachable) flexarrLog.info("Couldn't load", e) else flexarrLog.warning("Couldn't load", e)
            if (!shown) state = Load.Failed(e.message ?: t("Something went wrong.")) else refreshFailed?.invoke(e)
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
