package io.github.flexarr.ui.podcasts

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Search
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.pulltorefresh.PullToRefreshBox
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.github.flexarr.AppModel
import io.github.flexarr.core.DirectoryPodcast
import io.github.flexarr.core.FlexarrClient
import io.github.flexarr.core.Podcast
import io.github.flexarr.core.PodcastEpisode
import io.github.flexarr.core.PodcastGenre
import io.github.flexarr.core.addPodcast
import io.github.flexarr.core.continueListening
import io.github.flexarr.core.newPodcastEpisodes
import io.github.flexarr.core.podcastGenres
import io.github.flexarr.core.podcasts
import io.github.flexarr.core.searchPodcasts
import io.github.flexarr.core.topPodcasts
import io.github.flexarr.ui.components.ActionButton
import io.github.flexarr.ui.components.Edge
import io.github.flexarr.ui.components.Load
import io.github.flexarr.ui.components.LocalNavigator
import io.github.flexarr.ui.components.Loaded
import io.github.flexarr.ui.components.PageTitle
import io.github.flexarr.ui.components.PillButton
import io.github.flexarr.ui.components.PosterWidth
import io.github.flexarr.ui.components.RowSpacing
import io.github.flexarr.ui.components.SectionHeading
import io.github.flexarr.ui.components.StatusView
import io.github.flexarr.ui.components.rememberLoader
import io.github.flexarr.ui.search.SearchField
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.RoomBackground
import io.github.flexarr.ui.theme.glowColor
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.async
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

/** What Podcasts opens to: the shows followed, Up Next, the episodes part-heard and the newest unplayed ones. */
private class PodcastsHome(val podcasts: List<Podcast>, val listening: List<PodcastEpisode>, val newEpisodes: List<PodcastEpisode>) {
    val isEmpty get() = podcasts.isEmpty() && listening.isEmpty()
}

/**
 * Podcasts: the shows followed in a row, then Up Next, what's part-heard and the newest unplayed episodes, with Browse
 * for finding more. A profile the Podcasts library is hidden from is told so. The iPhone app's PodcastsView.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun PodcastsScreen(model: AppModel) {
    val client = model.client ?: return
    val navigator = LocalNavigator.current
    val available = model.podcastStatus?.available != false
    val loader = rememberLoader(model.credentials, model.watchStateVersion, model.podcasts.current?.id) {
        if (model.podcastStatus == null) model.loadPodcastStatus()
        model.loadUpNext()
        loadHome(client)
    }
    val glow = (loader.state as? Load.Loaded)?.value?.podcasts?.firstOrNull()?.let { glowColor(it.accentColor) }
    RoomBackground(ambient = glow, intensity = 0.18f) {
        Column(Modifier.fillMaxSize()) {
            PageTitle("Podcasts", onBack = navigator::back) {
                if (available) IconButton(onClick = navigator::openPodcastBrowse) { Icon(Icons.Filled.Search, contentDescription = "Browse", tint = Flexarr.screen) }
            }
            if (!available) {
                StatusView("Podcasts aren’t available on this profile.", empty = true)
                return@Column
            }
            Loaded(loader) { home ->
                PullToRefreshBox(isRefreshing = loader.refreshing, onRefresh = loader.retry) {
                    LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 24.dp)) {
                        if (home.isEmpty) {
                            item {
                                Column(Modifier.fillMaxWidth().padding(horizontal = Edge).padding(top = 60.dp), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(16.dp)) {
                                    Text("Follow a show and its new episodes come here.", fontSize = 17.sp, fontWeight = FontWeight.Medium, color = Flexarr.usher, textAlign = TextAlign.Center)
                                    ActionButton("Browse podcasts", onClick = navigator::openPodcastBrowse, icon = Icons.Filled.Search, prominent = true)
                                }
                            }
                        }
                        if (home.podcasts.isNotEmpty()) {
                            item(key = "shows") {
                                Column(Modifier.padding(vertical = 8.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                                    SectionHeading("Your podcasts")
                                    LazyRow(contentPadding = PaddingValues(horizontal = Edge), horizontalArrangement = Arrangement.spacedBy(RowSpacing)) {
                                        // Artwork alone, the most recently updated first (as the server lists them).
                                        items(home.podcasts, key = { it.id }) { show ->
                                            PodcastCard(show, onClick = { navigator.openPodcast(show.id) }, modifier = Modifier.width(PosterWidth), showsTitle = false)
                                        }
                                    }
                                }
                            }
                        }
                        // Up Next, the same on every device: Play next moves one to the top, and its menu takes one off.
                        if (model.upNext.isNotEmpty()) {
                            item(key = "upnext-heading") { SectionHeading("Up Next", Modifier.padding(top = 20.dp)) }
                            items(model.upNext, key = { "upnext-${it.id}" }) { episode ->
                                EpisodeListRow {
                                    PodcastEpisodeRow(model, episode, showsPodcast = true, compact = true, setPlayed = { setEpisodePlayed(model, episode, it) }, removeFromUpNext = { model.removeFromUpNext(episode.id) })
                                }
                            }
                        }
                        if (home.listening.isNotEmpty()) {
                            item(key = "listening-heading") { SectionHeading("Continue listening", Modifier.padding(top = 20.dp)) }
                            items(home.listening, key = { "listening-${it.id}" }) { episode ->
                                EpisodeListRow { PodcastEpisodeRow(model, episode, showsPodcast = true, setPlayed = { setEpisodePlayed(model, episode, it) }) }
                            }
                        }
                        val listening = home.listening.map { it.id }.toSet()
                        val unplayed = home.newEpisodes.filter { !it.isPlayed && it.id !in listening }.take(15)
                        if (unplayed.isNotEmpty()) {
                            item(key = "new-heading") { SectionHeading("New episodes", Modifier.padding(top = 20.dp)) }
                            items(unplayed, key = { "new-${it.id}" }) { episode ->
                                EpisodeListRow { PodcastEpisodeRow(model, episode, showsPodcast = true, compact = true, setPlayed = { setEpisodePlayed(model, episode, it) }) }
                            }
                        }
                    }
                }
            }
        }
    }
}

private suspend fun loadHome(client: FlexarrClient): PodcastsHome = coroutineScope {
    val continuing = async { runCatching { client.continueListening() }.getOrDefault(emptyList()) }
    val fresh = async { runCatching { client.newPodcastEpisodes(unplayed = true, take = 30) }.getOrDefault(emptyList()) }
    PodcastsHome(client.podcasts(), continuing.await(), fresh.await())
}

/** An episode row with a quiet separator beneath, on the shared edge (the row keeps to it itself, so a swipe's buttons reach the screen's). */
@Composable
fun EpisodeListRow(content: @Composable () -> Unit) {
    Column {
        content()
        HorizontalDivider(Modifier.padding(horizontal = Edge), color = Flexarr.aisle.copy(alpha = 0.6f))
    }
}

/**
 * Finding shows: Apple's charts for the country chosen in Settings on the web, by category, and search. A feed's own
 * address, pasted in, adds that feed (for shows Apple doesn't list). The iPhone app's PodcastBrowseView.
 */
@Composable
fun PodcastBrowseScreen(model: AppModel) {
    val client = model.client ?: return
    val navigator = LocalNavigator.current
    val scope = rememberCoroutineScope()
    var text by rememberSaveable { mutableStateOf("") }
    var genre by rememberSaveable { mutableStateOf<Int?>(null) }
    var genres by remember { mutableStateOf<List<PodcastGenre>>(emptyList()) }
    var results by remember { mutableStateOf<List<DirectoryPodcast>?>(null) }
    var adding by remember { mutableStateOf<Long?>(null) }
    var addingFeed by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }
    val top = rememberLoader(genre) {
        if (genres.isEmpty()) genres = runCatching { client.podcastGenres() }.getOrDefault(emptyList())
        client.topPodcasts(genre)
    }
    val feed = feedAddress(text)
    LaunchedEffect(text) {
        val words = text.trim()
        if (words.isEmpty() || feed != null) {
            results = null
            return@LaunchedEffect
        }
        // Typing: wait for a pause before asking Apple.
        delay(350)
        try {
            results = client.searchPodcasts(words)
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            error = e.message
        }
    }

    fun open(add: suspend () -> Podcast) {
        scope.launch {
            try {
                navigator.openPodcast(add().id)
            } catch (e: Exception) {
                error = e.message
            } finally {
                adding = null
                addingFeed = false
            }
        }
    }

    RoomBackground(ambient = null) {
        Column(Modifier.fillMaxSize()) {
            PageTitle("Browse", onBack = navigator::back)
            SearchField(text, onChange = { text = it }, placeholder = "Shows, or a feed’s address", modifier = Modifier.padding(horizontal = Edge).padding(bottom = 12.dp), onSearch = {})
            when {
                feed != null -> Box(Modifier.padding(horizontal = Edge)) {
                    ActionButton(if (addingFeed) "Adding…" else "Add this feed", onClick = {
                        addingFeed = true
                        open { client.addPodcast(feedUrl = feed) }
                    }, icon = Icons.Filled.Add, prominent = true, enabled = !addingFeed)
                }
                results != null -> {
                    val found = results.orEmpty()
                    if (found.isEmpty()) StatusView("No shows found for “${text.trim()}”.", empty = true, modifier = Modifier.height(240.dp))
                    else DirectoryGrid(found, adding) { show -> adding = show.iTunesId; open { client.addPodcast(iTunesId = show.iTunesId) } }
                }
                else -> Column {
                    LazyRow(contentPadding = PaddingValues(horizontal = Edge), horizontalArrangement = Arrangement.spacedBy(8.dp), modifier = Modifier.padding(bottom = 16.dp)) {
                        item { PillButton("Top shows", genre == null, { genre = null }) }
                        items(genres, key = { it.id }) { PillButton(it.name, genre == it.id, { genre = it.id }) }
                    }
                    Loaded(top) { shows -> DirectoryGrid(shows, adding) { show -> adding = show.iTunesId; open { client.addPodcast(iTunesId = show.iTunesId) } } }
                }
            }
        }
    }
    error?.let { message ->
        AlertDialog(
            onDismissRequest = { error = null },
            confirmButton = { TextButton(onClick = { error = null }) { Text("OK") } },
            title = { Text("Couldn’t add this") },
            text = { Text(message) },
            containerColor = Flexarr.seat,
        )
    }
}

@Composable
private fun DirectoryGrid(shows: List<DirectoryPodcast>, adding: Long?, onAdd: (DirectoryPodcast) -> Unit) {
    LazyVerticalGrid(
        GridCells.Adaptive(104.dp),
        contentPadding = PaddingValues(start = Edge, end = Edge, bottom = 24.dp),
        horizontalArrangement = Arrangement.spacedBy(14.dp),
        verticalArrangement = Arrangement.spacedBy(20.dp),
        modifier = Modifier.fillMaxSize(),
    ) {
        items(shows, key = { it.iTunesId }) { show -> DirectoryCard(show, isAdding = adding == show.iTunesId) { onAdd(show) } }
    }
}

/** An http or https address: the box holds a feed, not words to search for. */
private fun feedAddress(text: String): String? {
    val trimmed = text.trim()
    val uri = runCatching { java.net.URI(trimmed) }.getOrNull() ?: return null
    val scheme = uri.scheme?.lowercase() ?: return null
    return if ((scheme == "http" || scheme == "https") && uri.host != null) trimmed else null
}
