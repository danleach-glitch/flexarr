package io.github.flexarr.ui.podcasts

import androidx.compose.animation.animateContentSize
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.SegmentedButton
import androidx.compose.material3.SegmentedButtonDefaults
import androidx.compose.material3.SingleChoiceSegmentedButtonRow
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.pulltorefresh.PullToRefreshBox
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.github.flexarr.AppModel
import io.github.flexarr.core.Format
import io.github.flexarr.core.Podcast
import io.github.flexarr.core.PodcastEpisode
import io.github.flexarr.core.PodcastEpisodeSort
import io.github.flexarr.core.podcast
import io.github.flexarr.core.podcastEpisodes
import io.github.flexarr.core.refreshPodcast
import io.github.flexarr.core.subscribe
import io.github.flexarr.core.unsubscribe
import io.github.flexarr.ui.components.ActionButton
import io.github.flexarr.ui.components.Edge
import io.github.flexarr.ui.components.FactsLine
import io.github.flexarr.ui.components.LocalNavigator
import io.github.flexarr.ui.components.PageTitle
import io.github.flexarr.ui.components.StatusView
import io.github.flexarr.ui.components.pressable
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.RoomBackground
import io.github.flexarr.ui.theme.glowColor
import io.github.flexarr.ui.theme.marquee
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.launch

private const val PAGE_SIZE = 40

/**
 * A show: its artwork, who makes it and what it's about, Follow and Play, then its episodes: the unplayed ones or all,
 * newest or oldest first (chosen in the ⋮ menu, which also marks them all played). The iPhone app's PodcastView.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun PodcastScreen(model: AppModel, id: String) {
    val client = model.client ?: return
    val navigator = LocalNavigator.current
    val scope = rememberCoroutineScope()
    var podcast by remember { mutableStateOf<Podcast?>(null) }
    val episodes = remember { mutableStateListOf<PodcastEpisode>() }
    var total by remember { mutableIntStateOf(0) }
    var error by remember { mutableStateOf<String?>(null) }
    var unplayedOnly by rememberSaveable { mutableStateOf(true) }
    /** Oldest first: from a followed show's subscription, else what was chosen on this screen. */
    var oldestFirst by rememberSaveable { mutableStateOf<Boolean?>(null) }
    var loadingMore by remember { mutableStateOf(false) }
    var saving by remember { mutableStateOf(false) }
    var refreshing by remember { mutableStateOf(false) }
    var reload by remember { mutableIntStateOf(0) }
    var menu by remember { mutableStateOf(false) }
    var markingAllPlayed by remember { mutableStateOf(false) }
    var unfollowing by remember { mutableStateOf(false) }
    val sort = if (oldestFirst == true) PodcastEpisodeSort.Oldest else PodcastEpisodeSort.Newest

    LaunchedEffect(id, unplayedOnly, oldestFirst, reload, model.watchStateVersion) {
        try {
            // The order comes with the show the first time, so its episodes wait for it.
            if (oldestFirst == null) {
                val show = client.podcast(id)
                podcast = show
                oldestFirst = show.subscription?.oldestFirst ?: false
                return@LaunchedEffect // runs again with the order known
            }
            val page = client.podcastEpisodes(id, sort, unplayedOnly, take = PAGE_SIZE)
            podcast = client.podcast(id)
            episodes.clear()
            episodes.addAll(page.items)
            total = page.total
            error = null
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            if (podcast == null) error = e.message
        } finally {
            refreshing = false
        }
    }

    fun loadMore() {
        if (loadingMore || episodes.size >= total) return
        loadingMore = true
        val skip = episodes.size
        scope.launch {
            runCatching { client.podcastEpisodes(id, sort, unplayedOnly, skip = skip, take = PAGE_SIZE) }.onSuccess { page ->
                if (skip == episodes.size) {
                    val known = episodes.map { it.id }.toSet()
                    episodes.addAll(page.items.filter { it.id !in known })
                    total = page.total
                }
            }
            loadingMore = false
        }
    }

    fun follow(following: Boolean) {
        saving = true
        scope.launch {
            runCatching {
                // The order chosen while looking carries over to the subscription.
                if (following) client.subscribe(id, if (oldestFirst == true) true else null) else {
                    client.unsubscribe(id)
                    client.podcast(id)
                }
            }.onSuccess { podcast = it }
            saving = false
        }
    }

    /** Marks an episode played or unplayed here at once (on Unplayed, a played one goes), then on the server. */
    fun setPlayed(episode: PodcastEpisode, played: Boolean) {
        val changes = episode.isPlayed != played
        if (!changes && !(!played && episode.progress != null)) return
        val index = episodes.indexOfFirst { it.id == episode.id }
        if (unplayedOnly && played) {
            if (index >= 0) episodes.removeAt(index)
            total = (total - 1).coerceAtLeast(0)
        } else if (index >= 0) {
            episodes[index] = episode.withPlayed(played)
        }
        podcast?.let { show -> if (changes) podcast = show.copy(unplayedCount = (show.unplayedCount + if (played) -1 else 1).coerceAtLeast(0)) }
        scope.launch {
            if (runCatching { client.setWatched(episode.id, played) }.isFailure) reload++ // didn't stick: back to what the server has
            else runCatching { client.podcast(id) }.onSuccess { podcast = it }
        }
    }

    val show = podcast
    RoomBackground(ambient = glowColor(show?.accentColor), intensity = 0.3f) {
        Column(Modifier.fillMaxSize()) {
            PageTitle("", onBack = navigator::back) {
                if (show != null) {
                    Box {
                        IconButton(onClick = { menu = true }) { Icon(Icons.Filled.MoreVert, contentDescription = "More", tint = Flexarr.screen) }
                        DropdownMenu(menu, onDismissRequest = { menu = false }) {
                            Text("Order", fontSize = 13.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.usher, modifier = Modifier.padding(horizontal = 16.dp, vertical = 8.dp))
                            listOf("Newest first" to false, "Oldest first" to true).forEach { (label, oldest) ->
                                DropdownMenuItem(
                                    text = { Text(label) },
                                    leadingIcon = { Box(Modifier.width(20.dp)) { if (oldestFirst == oldest) Icon(Icons.Filled.Check, contentDescription = "Chosen") } },
                                    onClick = {
                                        menu = false
                                        if (oldest != oldestFirst) {
                                            oldestFirst = oldest
                                            if (show.isSubscribed) scope.launch { runCatching { client.subscribe(id, oldest) }.onSuccess { podcast = it } }
                                        }
                                    },
                                )
                            }
                            HorizontalDivider(color = Flexarr.aisle)
                            DropdownMenuItem(text = { Text("Mark all as played") }, enabled = show.unplayedCount > 0, onClick = { menu = false; markingAllPlayed = true })
                        }
                    }
                }
            }
            when {
                show != null -> PullToRefreshBox(isRefreshing = refreshing, onRefresh = {
                    // Pulled down: the feed is fetched again first, for an episode out a minute ago.
                    refreshing = true
                    scope.launch {
                        runCatching { client.refreshPodcast(id) }
                        reload++
                    }
                }) {
                    LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 24.dp)) {
                        item(key = "header") {
                            Header(model, show, episodes, oldestFirst == true, saving) { following ->
                                if (following) follow(true) else unfollowing = true
                            }
                        }
                        item(key = "episodes-heading") {
                            EpisodesHeading(total, unplayedOnly) { unplayedOnly = it }
                        }
                        if (episodes.isEmpty()) {
                            item { StatusView(if (unplayedOnly) "You’re up to date." else "No episodes yet.", empty = true, modifier = Modifier.height(160.dp)) }
                        }
                        itemsIndexed(episodes, key = { _, episode -> episode.id }) { index, episode ->
                            if (index >= episodes.size - 10) LaunchedEffect(episodes.size) { loadMore() }
                            EpisodeListRow { PodcastEpisodeRow(model, episode, setPlayed = { setPlayed(episode, it) }) }
                        }
                        if (loadingMore) item { Box(Modifier.fillMaxWidth().padding(20.dp), contentAlignment = Alignment.Center) { CircularProgressIndicator(color = Flexarr.screen) } }
                    }
                }
                error != null -> StatusView(error, retry = { reload++ })
                else -> StatusView()
            }
        }
    }

    if (markingAllPlayed && show != null) {
        AlertDialog(
            onDismissRequest = { markingAllPlayed = false },
            title = { Text("Mark all as played?") },
            text = { Text("All ${Format.count(show.episodeCount)} episodes of ${show.title} will be marked as played.") },
            confirmButton = {
                TextButton(onClick = {
                    markingAllPlayed = false
                    scope.launch {
                        runCatching { client.setWatched(id, true) }
                        model.watchStateChanged()
                    }
                }) { Text("Mark as Played") }
            },
            dismissButton = { TextButton(onClick = { markingAllPlayed = false }) { Text("Cancel") } },
            containerColor = Flexarr.seat,
        )
    }
    if (unfollowing) {
        AlertDialog(
            onDismissRequest = { unfollowing = false },
            title = { Text("Stop following this show?") },
            text = { Text("Its new episodes stop coming to this phone. Episodes already downloaded stay until you delete them.") },
            confirmButton = { TextButton(onClick = { unfollowing = false; follow(false) }) { Text("Stop following", color = Flexarr.danger) } },
            dismissButton = { TextButton(onClick = { unfollowing = false }) { Text("Cancel") } },
            containerColor = Flexarr.seat,
        )
    }
}

@Composable
private fun Header(model: AppModel, podcast: Podcast, episodes: List<PodcastEpisode>, oldestFirst: Boolean, saving: Boolean, onFollow: (Boolean) -> Unit) {
    var expanded by remember { mutableStateOf(false) }
    val ambient = glowColor(podcast.accentColor)
    Column(
        Modifier.fillMaxWidth().padding(horizontal = Edge).padding(top = 4.dp, bottom = 8.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(16.dp),
    ) {
        PodcastArtwork(podcast.artwork.poster, podcast.title, podcast.accentColor, Modifier.width(220.dp).shadow(20.dp, RoundedCornerShape(12.dp)), imageWidth = 780, cornerRadius = 12.dp)
        Column(horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(6.dp)) {
            Text(podcast.title, style = marquee(28).copy(fontWeight = FontWeight.ExtraBold), textAlign = TextAlign.Center, maxLines = 3, overflow = TextOverflow.Ellipsis)
            podcast.author?.let { Text(it, fontSize = 17.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen.copy(alpha = 0.9f), textAlign = TextAlign.Center) }
            FactsLine(listOfNotNull(podcast.categories.firstOrNull(), podcast.countLine, if (podcast.explicit) "Explicit" else null))
        }
        Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            ActionButton(
                if (saving) "…" else if (podcast.isSubscribed) "Following" else "Follow",
                onClick = { onFollow(!podcast.isSubscribed) },
                icon = if (podcast.isSubscribed) Icons.Filled.Check else Icons.Filled.Add,
                prominent = !podcast.isSubscribed,
                ambient = ambient,
                enabled = !saving,
            )
            // What Play starts: an episode part-heard, else the first unplayed one in the order shown.
            val next = episodes.firstOrNull { it.progress != null } ?: episodes.firstOrNull { !it.isPlayed }
            if (next != null) {
                ActionButton(
                    if (next.progress != null) "Resume" else if (oldestFirst) "Play" else "Latest episode",
                    onClick = { model.podcasts.play(next) },
                    icon = Icons.Filled.PlayArrow,
                    prominent = podcast.isSubscribed,
                    ambient = ambient,
                )
            }
        }
        podcast.error?.let { Text("The feed couldn’t be checked last time: $it", fontSize = 12.sp, color = Flexarr.danger, textAlign = TextAlign.Center) }
        podcast.overview?.let { overview ->
            Text(
                overview,
                fontSize = 15.sp,
                color = Flexarr.screen.copy(alpha = 0.85f),
                maxLines = if (expanded) Int.MAX_VALUE else 3,
                overflow = TextOverflow.Ellipsis,
                modifier = Modifier.fillMaxWidth().animateContentSize().pressable({ expanded = !expanded }),
            )
        }
    }
}

/** How many episodes, and Unplayed or All. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun EpisodesHeading(total: Int, unplayedOnly: Boolean, onChange: (Boolean) -> Unit) {
    Row(Modifier.fillMaxWidth().padding(horizontal = Edge).padding(top = 12.dp), verticalAlignment = Alignment.CenterVertically) {
        Text(
            if (unplayedOnly) "${Format.count(total)} unplayed" else if (total == 1) "1 episode" else "${Format.count(total)} episodes",
            style = marquee(22).copy(fontWeight = FontWeight.Bold),
            maxLines = 1,
            modifier = Modifier.weight(1f),
        )
        SingleChoiceSegmentedButtonRow {
            listOf("Unplayed" to true, "All" to false).forEachIndexed { index, (label, value) ->
                SegmentedButton(
                    selected = unplayedOnly == value,
                    onClick = { onChange(value) },
                    shape = SegmentedButtonDefaults.itemShape(index, 2),
                    icon = {},
                    colors = SegmentedButtonDefaults.colors(activeContainerColor = Flexarr.aisle, activeContentColor = Flexarr.screen, inactiveContainerColor = Flexarr.seat, inactiveContentColor = Flexarr.usher, activeBorderColor = Flexarr.aisle, inactiveBorderColor = Flexarr.aisle),
                ) { Text(label, fontSize = 13.sp) }
            }
        }
    }
}
