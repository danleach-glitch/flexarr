package io.github.flexarr.ui

import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.onFocusChanged
import androidx.compose.ui.platform.LocalContext
import io.github.flexarr.appModel
import io.github.flexarr.core.ItemSummary
import kotlinx.coroutines.delay

/** How long a page is looked at, or a TV's button or card has focus, before what it plays is got ready. */
const val PAGE_WARM_DELAY_MS = 2000L
const val FOCUS_WARM_DELAY_MS = 1000L

/**
 * A page showing [item] for a moment gets what Play would start ready, so it opens at once (docs/fast-start.md);
 * leaving the page lets it go, unless Play took it.
 */
@Composable
fun GetsReadyAfterAMoment(item: ItemSummary?) {
    val model = LocalContext.current.appModel
    val id = item?.id ?: return
    LaunchedEffect(id) {
        delay(PAGE_WARM_DELAY_MS)
        model.getReady(item)
    }
    DisposableEffect(id) { onDispose { model.dropReady(id) } }
}

/** A TV button or card that, with focus for a moment, gets [item] ready; focus moving on lets it go. */
@Composable
fun Modifier.getsReadyOnFocus(item: ItemSummary?): Modifier {
    val model = LocalContext.current.appModel
    var focused by remember { mutableStateOf(false) }
    val id = item?.id
    LaunchedEffect(focused, id) {
        if (!focused || item == null) return@LaunchedEffect
        delay(FOCUS_WARM_DELAY_MS)
        model.getReady(item)
    }
    DisposableEffect(id) { onDispose { if (id != null) model.dropReady(id) } }
    return onFocusChanged { state ->
        if (focused && !state.hasFocus && id != null) model.dropReady(id)
        focused = state.hasFocus
    }
}
