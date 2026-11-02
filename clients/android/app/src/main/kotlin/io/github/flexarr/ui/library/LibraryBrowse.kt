package io.github.flexarr.ui.library

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyListState
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.pulltorefresh.PullToRefreshBox
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import io.github.flexarr.AppModel
import io.github.flexarr.core.Home
import io.github.flexarr.ui.components.Load
import io.github.flexarr.ui.components.Loader
import io.github.flexarr.ui.components.StatusView
import io.github.flexarr.ui.home.HomeHero
import io.github.flexarr.ui.home.HomeRowSlot
import io.github.flexarr.ui.home.showsIn

/** Whether a Browse page has anything on it: a hero, or a row with something in it. */
internal fun Home.hasBrowse() = featured.isNotEmpty() || rows.any { it.showsIn(this) }

/**
 * A film, TV or home videos library's own home page, as on the web and the TVs: a hero of its titles, then its rows
 * (Continue watching, the latest, Coming soon, its Top 10, its genres…). A row's See all opens the titles behind it.
 * Loaded by the library screen ([loader]), with its scroll position ([state]), so switching to All and back finds it
 * as it was left.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun LibraryBrowse(model: AppModel, loader: Loader<Home?>, state: LazyListState, onFeatured: (Int) -> Unit) {
    when (val load = loader.state) {
        is Load.Loaded -> {
            val browse = load.value ?: return StatusView()
            if (!browse.hasBrowse()) return StatusView("Nothing in this library yet.", empty = true)
            PullToRefreshBox(isRefreshing = loader.refreshing, onRefresh = loader.retry, modifier = Modifier.fillMaxSize()) {
                LazyColumn(Modifier.fillMaxSize(), state = state, contentPadding = PaddingValues(bottom = 32.dp), verticalArrangement = Arrangement.spacedBy(28.dp)) {
                    if (browse.featured.isNotEmpty()) item(key = "hero") { HomeHero(browse, onIndex = onFeatured) }
                    items(browse.rows.filter { it.showsIn(browse) }, key = { it.id }) { HomeRowSlot(model, it, browse) }
                }
            }
        }
        Load.Loading -> StatusView()
        is Load.Failed -> StatusView(load.message, retry = loader.retry)
    }
}
