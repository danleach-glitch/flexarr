package io.github.flexarr.ui.components

import android.util.Log
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import kotlin.coroutines.cancellation.CancellationException

/** Something a screen fetches: still coming, here, or failed with a message to show. */
sealed interface Load<out T> {
    data object Loading : Load<Nothing>
    data class Loaded<T>(val value: T) : Load<T>
    data class Failed(val message: String) : Load<Nothing>
}

/** [refreshing]: fetching again with the last answer still on screen (pull to refresh shows it). */
class Loader<T>(val state: Load<T>, val refreshing: Boolean, val retry: () -> Unit)

/**
 * Runs [load] now and again whenever [keys] change. Reloading keeps what's on screen until the new answer comes, so
 * coming back from the player doesn't flash a spinner, and a reload that fails leaves it there.
 */
@Composable
fun <T> rememberLoader(vararg keys: Any?, load: suspend () -> T): Loader<T> {
    var state by remember { mutableStateOf<Load<T>>(Load.Loading) }
    var attempt by remember { mutableIntStateOf(0) }
    var refreshing by remember { mutableStateOf(false) }
    LaunchedEffect(*keys, attempt) {
        val shown = state is Load.Loaded
        if (!shown) state = Load.Loading
        refreshing = shown
        try {
            state = Load.Loaded(load())
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            Log.w("Flexarr", "Couldn't load", e)
            if (!shown) state = Load.Failed(e.message ?: "Something went wrong.")
        } finally {
            refreshing = false
        }
    }
    return Loader(state, refreshing) { attempt++ }
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
