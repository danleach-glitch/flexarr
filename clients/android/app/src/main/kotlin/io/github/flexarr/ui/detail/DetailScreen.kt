package io.github.flexarr.ui.detail

import io.github.flexarr.core.i18n.t
import io.github.flexarr.core.i18n.Words

import io.github.flexarr.ui.GetsReadyAfterAMoment

import androidx.compose.ui.semantics.LiveRegionMode
import androidx.compose.ui.semantics.liveRegion
import io.github.flexarr.player.LocalPlayFrom
import io.github.flexarr.ui.books.BookScreen
import io.github.flexarr.ui.music.AlbumScreen
import io.github.flexarr.ui.music.ArtistScreen
import androidx.compose.animation.animateContentSize
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.async
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.launch
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.gestures.animateScrollBy
import androidx.compose.foundation.gestures.scrollBy
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Cast
import androidx.compose.material.icons.filled.Shuffle
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.Text
import androidx.compose.material3.pulltorefresh.PullToRefreshBox
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.drawWithContent
import androidx.compose.ui.graphics.BlendMode
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.CompositingStrategy
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.github.flexarr.AppModel
import io.github.flexarr.appModel
import io.github.flexarr.core.CreditRole
import io.github.flexarr.core.DetailActions
import io.github.flexarr.core.Format
import io.github.flexarr.core.ItemDetail
import io.github.flexarr.core.ItemSummary
import io.github.flexarr.core.MediaFile
import io.github.flexarr.core.TitleVersion
import io.github.flexarr.core.sameEpisode
import io.github.flexarr.core.MediaKind
import io.github.flexarr.core.ShowMissing
import io.github.flexarr.core.Availability
import io.github.flexarr.ui.external.TitleRequestState
import io.github.flexarr.ui.external.canAskForTitles
import io.github.flexarr.ui.external.requestTitle
import io.github.flexarr.ui.external.titleKey
import io.github.flexarr.ui.external.titleState
import io.github.flexarr.ui.external.unrequestTitle
import io.github.flexarr.core.searchMissing
import io.github.flexarr.core.showMissing
import io.github.flexarr.core.effectiveDuration
import io.github.flexarr.core.episodeCode
import io.github.flexarr.core.episodeSubtitle
import io.github.flexarr.core.facts
import io.github.flexarr.core.scores
import io.github.flexarr.core.isResumable
import io.github.flexarr.core.progress
import io.github.flexarr.ui.components.isWide
import io.github.flexarr.ui.components.ActionButton
import io.github.flexarr.ui.downloads.DownloadAction
import io.github.flexarr.ui.downloads.SeasonDownloadLink
import io.github.flexarr.ui.downloads.ShowDownloadAction
import io.github.flexarr.ui.downloads.TitleDownloadButton
import io.github.flexarr.ui.components.Edge
import io.github.flexarr.ui.components.FactsLine
import io.github.flexarr.ui.components.ItemMenu
import io.github.flexarr.ui.components.itemActions
import io.github.flexarr.ui.components.menuActions
import io.github.flexarr.ui.components.LabeledAction
import io.github.flexarr.ui.components.LocalClient
import io.github.flexarr.ui.components.LocalNavigator
import io.github.flexarr.ui.subtitles.SubtitleSearchSheet
import io.github.flexarr.ui.playon.PlayOnSheet
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.TextButton
import androidx.compose.material3.RadioButton
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.clearAndSetSemantics
import androidx.compose.ui.semantics.role
import androidx.compose.ui.semantics.selected
import androidx.compose.ui.semantics.semantics
import io.github.flexarr.ui.components.PersonCard
import io.github.flexarr.ui.components.PillButton
import io.github.flexarr.ui.components.PosterCard
import io.github.flexarr.ui.components.PosterWidth
import io.github.flexarr.ui.components.ProgressBar
import io.github.flexarr.ui.components.RefreshWhenStale
import io.github.flexarr.ui.components.RemoteImage
import io.github.flexarr.ui.components.RowSpacing
import io.github.flexarr.ui.components.SectionHeading
import io.github.flexarr.ui.components.StatusView
import io.github.flexarr.ui.components.TitleArt
import io.github.flexarr.ui.components.WatchedBadge
import io.github.flexarr.ui.components.pressable
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.RoomBackground
import io.github.flexarr.ui.theme.glowColor
import io.github.flexarr.ui.theme.marquee
import java.util.Locale
import java.util.UUID
import io.github.flexarr.ui.theme.Tokens
import io.github.flexarr.ui.theme.FlexarrIcons

/**
 * Detail for a film, show or episode (a season opens its show with that season picked). The backdrop sits at the top,
 * washed in the title's ambient colour, with the actions and episodes beneath it.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun DetailScreen(model: AppModel, id: String) {
    val client = model.client ?: return
    val navigator = LocalNavigator.current
    var detail by remember(id) { mutableStateOf<ItemDetail?>(null) }
    // The episode shown now, once watching from this page has moved it on from [id].
    var shownId by rememberSaveable(id) { mutableStateOf<String?>(null) }
    // Given to the player with Play, so this page knows the sitting was started here (AppModel.takeEnding).
    val pageToken = rememberSaveable(id) { UUID.randomUUID().toString() }
    var error by remember(id) { mutableStateOf<String?>(null) }
    var nextUp by remember(id) { mutableStateOf<ItemSummary?>(null) }
    var similar by remember(id) { mutableStateOf(emptyList<ItemSummary>()) }
    var selectedSeasonId by rememberSaveable(id) { mutableStateOf<String?>(null) }
    val seasons = remember(id) { mutableStateMapOf<String, ItemDetail>() }
    // A show's gaps (seasons and episodes it lacks, what's coming), and the seasons asked for here since.
    var missing by remember(id) { mutableStateOf(ShowMissing.None) }
    var asked by remember(id) { mutableStateOf(emptySet<Int>()) }
    var attempt by remember { mutableStateOf(0) }
    var refreshing by remember { mutableStateOf(false) }
    var loadedAt by remember(id) { mutableStateOf<Long?>(null) }
    RefreshWhenStale(loadedAt) { attempt++ }

    LaunchedEffect(id, model.watchStateVersion, attempt) {
        try {
            // Back from watching an episode started here: on to the next one (or the one it stopped part way through),
            // settled before anything is fetched, so a reload that cuts in can't lose it.
            val before = shownId
            model.takeEnding(from = pageToken)?.moveOn(shownId ?: id)?.let { shownId = it }
            var loaded = try {
                client.item(shownId ?: id)
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                if (shownId == before) throw e
                shownId = before // The next episode couldn't be had (gone, or not for this profile): stay.
                client.item(shownId ?: id)
            }
            // A season opens as its show, with that season selected.
            val parent = loaded.item.parentId
            if (loaded.item.kind == MediaKind.Season && parent != null) {
                seasons[loaded.item.id] = loaded
                if (selectedSeasonId == null) selectedSeasonId = loaded.item.id
                loaded = client.item(parent)
            }
            detail = loaded
            error = null
            loadedAt = System.currentTimeMillis()
            val item = loaded.item
            coroutineScope {
                // More like this comes alongside the episodes, rather than after them.
                val alike = if (similar.isEmpty() && item.kind != MediaKind.Episode) async { runCatching { client.similar(item.id) }.getOrNull() } else null
                when (item.kind) {
                    MediaKind.Series -> {
                        // On the effect's own scope, not this one: the page (and pull to refresh) doesn't wait for Sonarr.
                        this@LaunchedEffect.launch {
                            runCatching { client.showMissing(item.id) }.onSuccess {
                                missing = it
                                // Whether Seerr is answering, so a season's Request and Requested can wait while it isn't.
                                if (it.canRequest) runCatching { model.titleRequestStatus() }
                            }
                        }
                        nextUp = runCatching { client.nextUp(item.id) }.getOrNull()
                        if (selectedSeasonId == null) selectedSeasonId = nextUp?.parentId ?: loaded.children.firstOrNull()?.id
                    }
                    MediaKind.Episode -> item.parentId?.let { selectedSeasonId = it }
                    else -> Unit
                }
                selectedSeasonId?.let { season -> runCatching { client.item(season) }.onSuccess { seasons[season] = it } }
                alike?.await()?.let { similar = it }
            }
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            if (detail == null) error = e.message
        } finally {
            refreshing = false
        }
    }
    LaunchedEffect(selectedSeasonId) {
        val season = selectedSeasonId ?: return@LaunchedEffect
        if (season !in seasons) runCatching { client.item(season) }.onSuccess { seasons[season] = it }
    }

    val loaded = detail
    val item = loaded?.item
    // A book reached by a link or an older route opens as itself, never as a film.
    if (item?.kind == MediaKind.Audiobook) return BookScreen(model, id)
    // So does an album or artist (an album-ready notification opens item/<id>): the music screens, not a film's.
    if (item?.kind == MediaKind.Album) return AlbumScreen(model, id)
    if (item?.kind == MediaKind.Artist) return ArtistScreen(model, id)
    val series = item?.let { if (it.kind == MediaKind.Series) it else loaded.ancestors.firstOrNull { a -> a.kind == MediaKind.Series } }
    val ambient = item?.let { glowColor(it.accentColor) } ?: Flexarr.silver
    // A show's every episode, season by season (those already fetched for the page as they are), for its Download.
    val allEpisodes: suspend () -> List<ItemSummary> = {
        loaded?.children.orEmpty().flatMap { season -> (seasons[season.id] ?: client.item(season.id).also { seasons[season.id] = it }).children }
    }

    CompositionLocalProvider(LocalPlayFrom provides pageToken) {
    RoomBackground(ambient = ambient, intensity = 0.22f) {
        when {
            loaded != null && item != null -> PullToRefreshBox(
                isRefreshing = refreshing,
                onRefresh = {
                    refreshing = true
                    attempt++
                },
            ) {
                val episodeColumns = if (isWide) 2 else 1
                LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 40.dp), verticalArrangement = Arrangement.spacedBy(28.dp)) {
                    item(key = "header") { Header(item, series, loaded, ambient, nextUp, seasons[selectedSeasonId], allEpisodes) }
                    sections(item, loaded, seasons[selectedSeasonId], selectedSeasonId, { selectedSeasonId = it }, similar, ambient, columns = episodeColumns, missing = missing) {
                        if (item.kind == MediaKind.Series) {
                            showMissingSections(
                                missing,
                                librarySeasons = loaded.children,
                                showPoster = item.artwork.poster,
                                asked = asked,
                                requestState = { season ->
                                    val tmdb = loaded.tmdbId?.toIntOrNull()
                                    if (tmdb == null) TitleRequestState.None
                                    else model.titleState(titleKey("tv", tmdb, season.seasonNumber), if (season.requested) Availability.Requested else Availability.None)
                                },
                                canAsk = model.canAskForTitles,
                                onRequest = { season ->
                                    loaded.tmdbId?.toIntOrNull()?.let { navigator.requestTitle(model, "tv", it, season.seasonNumber) }
                                },
                                onUnrequest = { season ->
                                    loaded.tmdbId?.toIntOrNull()?.let { navigator.unrequestTitle(model, "tv", it, season.seasonNumber) }
                                },
                                onSearch = { season, episodes ->
                                    asked = asked + season
                                    navigator.watchlistChange(t("Couldn’t search for season {season}", "season" to season)) {
                                        try {
                                            client.searchMissing(item.id, season, episodes)
                                        } catch (e: Exception) {
                                            asked = asked - season
                                            throw e
                                        }
                                    }
                                },
                            )
                        }
                    }
                }
            }
            error != null -> StatusView(error, retry = { attempt++ })
            else -> StatusView()
        }
        IconButton(
            onClick = { navigator.back() },
            modifier = Modifier.statusBarsPadding().padding(8.dp).clip(CircleShape).background(Flexarr.room.copy(alpha = 0.55f)),
        ) { Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = t("Back"), tint = Flexarr.screen) }
    }
    }
}

@Composable
private fun Header(item: ItemSummary, series: ItemSummary?, detail: ItemDetail, ambient: Color, nextUp: ItemSummary?, season: ItemDetail?, allEpisodes: suspend () -> List<ItemSummary>) {
    var overviewExpanded by remember { mutableStateOf(false) }
    Column(verticalArrangement = Arrangement.spacedBy(14.dp)) {
        // Taller than usual only when large text needs it.
        Box(Modifier.fillMaxWidth().heightIn(min = if (isWide) 520.dp else 420.dp)) {
            RemoteImage(item.artwork.backdrop ?: series?.artwork?.backdrop, 1000, Modifier.matchParentSize().fadeOut()) {
                Box(Modifier.fillMaxSize().background(Flexarr.room))
            }
            Box(
                Modifier.matchParentSize().fadeOut()
                    .background(Brush.linearGradient(listOf(ambient.copy(alpha = 0.1f), ambient.copy(alpha = 0.35f))))
                    .background(Brush.verticalGradient(0.3f to Color.Transparent, 1f to Flexarr.room.copy(alpha = 0.6f))),
            )
            // Clear of Back above it, however tall large text makes it.
            Column(Modifier.align(Alignment.BottomStart).statusBarsPadding().padding(top = 64.dp).padding(horizontal = Edge), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                if (item.kind == MediaKind.Episode && series != null) {
                    TitleArt(series, maxWidth = 240.dp, maxHeight = 80.dp, fontSize = 40)
                    // Said by TalkBack when the page moves on to the next episode.
                    Text(item.episodeSubtitle ?: item.title, style = marquee(28).copy(fontWeight = FontWeight.Bold), modifier = Modifier.semantics { liveRegion = LiveRegionMode.Polite })
                } else {
                    TitleArt(item, maxWidth = 300.dp, maxHeight = 120.dp, fontSize = 52)
                }
                // No scores for a child.
                val isKids = LocalContext.current.appModel.isKids
                FactsLine(item.facts(scores = !isKids, versions = detail.versions), scores = if (isKids) null else item.scores)
                if (detail.genres.isNotEmpty()) {
                    Text(detail.genres.take(3).joinToString(", "), fontSize = 15.sp, fontWeight = FontWeight.Medium, color = Flexarr.usher)
                }
            }
        }
        Actions(item, series, nextUp, season, ambient, detail, allEpisodes)
        detail.overview?.takeIf { it.isNotEmpty() }?.let { overview ->
            Text(
                overview,
                fontSize = 15.sp,
                lineHeight = 21.sp,
                color = Flexarr.screen.copy(alpha = 0.88f),
                maxLines = if (overviewExpanded) Int.MAX_VALUE else 4,
                overflow = TextOverflow.Ellipsis,
                modifier = Modifier.fillMaxWidth().pressable({ overviewExpanded = !overviewExpanded }).padding(horizontal = Edge).animateContentSize(),
            )
        }
    }
}

/**
 * Play on a line of its own, so "Resume from 1:02:35" never wraps; the rest underneath, named, in the order every
 * client shares ([DetailActions]); ⋯ holds the rest.
 */
@Composable
private fun Actions(item: ItemSummary, series: ItemSummary?, nextUp: ItemSummary?, season: ItemDetail?, ambient: Color, detail: ItemDetail, allEpisodes: suspend () -> List<ItemSummary>) {
    val from = LocalPlayFrom.current
    val navigator = LocalNavigator.current
    val model = androidx.compose.ui.platform.LocalContext.current.appModel
    // Subtitles from the server's Bazarr, for the version chosen or this copy's best file; saying so once they're saved.
    var searchingSubtitlesFor by remember(item.id) { mutableStateOf<String?>(null) }
    var savedSubtitles by remember { mutableStateOf<String?>(null) }
    var choosingScreen by remember(item.id) { mutableStateOf(false) }
    var showingTrailer by remember(item.id) { mutableStateOf(false) }
    var more by remember(item.id) { mutableStateOf(false) }
    var choosingVersion by remember(item.id) { mutableStateOf(false) }
    // What Play starts: the item itself, or for a show the next-up episode (falling back to the selected season's first).
    val playable = when (item.kind) {
        MediaKind.Movie, MediaKind.Episode -> item
        MediaKind.Series -> nextUp ?: season?.children?.firstOrNull()
        else -> null
    }
    GetsReadyAfterAMoment(playable)
    val watched = item.userData.watched || item.userData.unwatchedCount == 0
    val kind = when (item.kind) {
        MediaKind.Series -> DetailActions.Kind.SHOW
        MediaKind.Episode -> DetailActions.Kind.EPISODE
        else -> DetailActions.Kind.MOVIE
    }
    val file = detail.files.firstOrNull()
    val trailer = detail.trailerId
    // Only what the server can put on a watchlist: films and shows, from servers that have one (0.56).
    // What was tapped here (the row's button or ⋯), until the page loads again and the server says.
    var watchlistChanged by remember(detail) { mutableStateOf<Boolean?>(null) }
    val onWatchlist = detail.onWatchlist.takeIf { item.kind == MediaKind.Movie || item.kind == MediaKind.Series }?.let { watchlistChanged ?: it }
    val toggleWatchlist = { on: Boolean ->
        watchlistChanged = !on
        navigator.setOnWatchlist(item, !on, onFailed = { watchlistChanged = on })
    }
    val actions = DetailActions.of(
        DetailActions.Input(
            kind = kind,
            // A show's Resume already starts its episode; Start over and Mark as unwatched are for the title itself.
            hasProgress = playable?.isResumable == true && item.kind != MediaKind.Series,
            isWatched = watched,
            hasTrailer = trailer != null,
            isKids = model.isKids,
            platform = DetailActions.Platform.PHONE,
            // A show downloads every episode of every season.
            canDownload = item.kind == MediaKind.Movie || item.kind == MediaKind.Episode || item.kind == MediaKind.Series,
            canSearchSubtitles = detail.canSearchSubtitles == true && file != null,
            canChooseVersion = (detail.versions?.size ?: 0) > 1,
            canWatchlist = onWatchlist != null,
        ),
    )
    // Another screen: a film or episode as it is, a show the episode that screen's profile is up to. The phone's own
    // addition to the menu.
    val playsOn = item.kind == MediaKind.Movie || item.kind == MediaKind.Episode || item.kind == MediaKind.Series
    val buttons = if (playsOn && DetailActions.Button.MORE !in actions.buttons) actions.buttons + DetailActions.Button.MORE else actions.buttons
    Column(Modifier.padding(horizontal = Edge).widthIn(max = 520.dp), verticalArrangement = Arrangement.spacedBy(18.dp)) {
        if (playable != null) {
            ActionButton(playLabel(playable, item), onClick = { navigator.play(playable, from = from) }, icon = FlexarrIcons.play, prominent = true, ambient = ambient, modifier = Modifier.fillMaxWidth())
        }
        // Sharing the width, until large text would shrink the names to fit: then each its own size, scrolling sideways.
        val crowded = LocalDensity.current.fontScale > 1.3f
        Row(if (crowded) Modifier.horizontalScroll(rememberScrollState()) else Modifier, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            val slot = if (crowded) Modifier.widthIn(min = 72.dp) else Modifier.weight(1f)
            buttons.forEach { button ->
                when (button) {
                    DetailActions.Button.PLAY -> Unit
                    DetailActions.Button.START_OVER -> playable?.let { target ->
                        LabeledAction(t(Words.startOver), FlexarrIcons.startOver, { navigator.play(target, fromBeginning = true, from = from) }, slot)
                    }
                    DetailActions.Button.SHUFFLE -> if (item.childCount > 0) LabeledAction(t(Words.shuffle), FlexarrIcons.shuffle, { navigator.shuffle(item) }, slot)
                    DetailActions.Button.TRAILER -> LabeledAction(t(Words.trailer), FlexarrIcons.trailer, { showingTrailer = true }, slot)
                    // A toggle like Download: the tick fills once it's watched, and the name stays put.
                    DetailActions.Button.WATCHED -> LabeledAction(
                        t("Watched"),
                        if (watched) FlexarrIcons.isWatched else FlexarrIcons.watched,
                        { navigator.setWatched(item, !watched) },
                        slot.semantics { contentDescription = if (watched) t("Watched. Mark as unwatched") else t(Words.markAsWatched) },
                        maxLines = 2,
                    )
                    DetailActions.Button.WATCHLIST -> onWatchlist?.let { on -> WatchlistAction(on, { toggleWatchlist(on) }, slot) }
                    DetailActions.Button.DOWNLOAD ->
                        if (item.kind == MediaKind.Series) ShowDownloadAction(model, item, allEpisodes, slot)
                        else DownloadAction(model, item, slot, detail.copyIds.orEmpty())
                    DetailActions.Button.MORE -> Box(slot) {
                        LabeledAction(t("More"), FlexarrIcons.more, { more = true }, Modifier.fillMaxWidth())
                        DropdownMenu(more, onDismissRequest = { more = false; choosingVersion = false }) {
                            // Version's list takes the menu's place, so there's no second popup to open as the first one closes.
                            if (choosingVersion) {
                                val versions = detail.versions.orEmpty()
                                val choose = { fileId: String? -> more = false; choosingVersion = false; navigator.chooseVersion(item, fileId) }
                                // Read out as a choice, and which one is on: the row says so rather than its circle.
                                val choice = { on: Boolean -> Modifier.semantics { role = Role.RadioButton; selected = on } }
                                DropdownMenuItem(
                                    text = { Text(t(Words.automatic)) },
                                    leadingIcon = { RadioButton(versions.none { it.chosen }, onClick = null, Modifier.clearAndSetSemantics {}) },
                                    onClick = { choose(null) },
                                    modifier = choice(versions.none { it.chosen }),
                                )
                                versions.forEach { version ->
                                    DropdownMenuItem(
                                        text = { Text(version.name(versions)) },
                                        leadingIcon = { RadioButton(version.chosen, onClick = null, Modifier.clearAndSetSemantics {}) },
                                        onClick = { choose(version.fileId) },
                                        modifier = choice(version.chosen),
                                    )
                                }
                                return@DropdownMenu
                            }
                            actions.menu.forEach { entry ->
                                when (entry) {
                                    is DetailActions.MenuItem.Moved -> when (entry.button) {
                                        DetailActions.Button.SHUFFLE -> if (item.childCount > 0) {
                                            DropdownMenuItem(text = { Text(t(Words.shuffle)) }, onClick = { more = false; navigator.shuffle(item) })
                                        }
                                        DetailActions.Button.TRAILER ->
                                            DropdownMenuItem(text = { Text(t(Words.trailer)) }, onClick = { more = false; showingTrailer = true })
                                        DetailActions.Button.WATCHED ->
                                            DropdownMenuItem(
                                                text = { Text(if (watched) t(Words.markAsUnwatched) else t(Words.markAsWatched)) },
                                                onClick = { more = false; navigator.setWatched(item, !watched) },
                                            )
                                        DetailActions.Button.WATCHLIST -> onWatchlist?.let { on ->
                                            DropdownMenuItem(
                                                text = { Text(if (on) t(Words.removeFromWatchlist) else t(Words.addToWatchlist)) },
                                                onClick = { more = false; toggleWatchlist(on) },
                                            )
                                        }
                                        else -> Unit
                                    }
                                    DetailActions.MenuItem.Version ->
                                        DropdownMenuItem(text = { Text(t(Words.version)) }, onClick = { choosingVersion = true })
                                    DetailActions.MenuItem.Subtitles -> detail.subtitleTargetId?.let { found ->
                                        DropdownMenuItem(text = { Text(t("Search for subtitles")) }, onClick = { more = false; searchingSubtitlesFor = found })
                                    }
                                    DetailActions.MenuItem.GoToShow -> series?.let { show ->
                                        DropdownMenuItem(text = { Text(t(Words.goToShow)) }, onClick = { more = false; navigator.openItem(show.id) })
                                    }
                                }
                            }
                            if (playsOn) DropdownMenuItem(text = { Text(t("Play on another screen")) }, onClick = { more = false; choosingScreen = true })
                        }
                    }
                }
            }
        }
    }
    if (showingTrailer && trailer != null) TrailerSheet(trailer, t("{title} trailer", "title" to item.title), onClose = { showingTrailer = false })
    if (choosingScreen) PlayOnSheet(LocalClient.current, item, onClose = { choosingScreen = false })
    searchingSubtitlesFor?.let { fileId ->
        SubtitleSearchSheet(
            LocalClient.current,
            item.id,
            fileId,
            onDownloaded = { saved ->
                searchingSubtitlesFor = null
                savedSubtitles = t("{language} subtitles saved", "language" to saved.label)
            },
            onClose = { searchingSubtitlesFor = null },
        )
    }
    savedSubtitles?.let { message ->
        AlertDialog(
            onDismissRequest = { savedSubtitles = null },
            confirmButton = { TextButton(onClick = { savedSubtitles = null }) { Text(t("OK")) } },
            text = { Text(message) },
            containerColor = Flexarr.seat,
        )
    }
}

/** "Watchlist", its bookmark filled once it's there; tapping puts it on or takes it off. */
@Composable
private fun WatchlistAction(on: Boolean, onClick: () -> Unit, modifier: Modifier) {
    LabeledAction(
        t(Words.watchlist),
        if (on) FlexarrIcons.onWatchlist else FlexarrIcons.watchlist,
        onClick,
        modifier.semantics { contentDescription = if (on) t("On watchlist. Remove from watchlist") else t(Words.addToWatchlist) },
    )
}

private fun playLabel(target: ItemSummary, container: ItemSummary): String {
    val resume = target.isResumable
    val code = target.episodeCode
    if (container.kind == MediaKind.Series && code != null) return if (resume) t(Words.resumeEpisode, "episode" to code) else t("Play {episode}", "episode" to code)
    return if (resume) t(Words.resumeFrom, "time" to Format.timestamp(target.userData.positionSeconds)) else t(Words.play)
}

private fun androidx.compose.foundation.lazy.LazyListScope.sections(
    item: ItemSummary,
    detail: ItemDetail,
    season: ItemDetail?,
    selectedSeasonId: String?,
    onSeason: (String) -> Unit,
    similar: List<ItemSummary>,
    ambient: Color,
    /** Episodes side by side on a tablet. */
    columns: Int,
    /** A show's gaps: "8 of 10" on its seasons. */
    missing: ShowMissing = ShowMissing.None,
    /** After the episodes, before the cast: what the library lacks of a show. */
    gaps: androidx.compose.foundation.lazy.LazyListScope.() -> Unit = {},
) {
    if (item.kind == MediaKind.Series && detail.children.isNotEmpty()) {
        item(key = "seasons") { SeasonPicker(detail.children, selectedSeasonId, onSeason, ambient, season) { missing.countLabel(it.seasonNumber) } }
        if (season == null) {
            item(key = "season-loading") { Box(Modifier.fillMaxWidth().height(200.dp), contentAlignment = Alignment.Center) { CircularProgressIndicator(color = Flexarr.screen) } }
        } else {
            items(season.children.chunked(columns), key = { "episode-${it.first().id}" }) { row ->
                EpisodeColumns(row, columns) { episode -> EpisodeRow(episode, season.childOverviews[episode.id], isCurrent = false, playsOnTap = true) }
            }
        }
    } else if (item.kind == MediaKind.Episode && season != null && season.children.size > 1) {
        item(key = "season-heading") { SectionHeading(season.item.title) }
        items(season.children.chunked(columns), key = { "episode-${it.first().id}" }) { row ->
            EpisodeColumns(row, columns) { episode ->
                EpisodeRow(episode, season.childOverviews[episode.id], isCurrent = episode.sameEpisode(item), playsOnTap = episode.id == item.id)
            }
        }
    } else if (item.kind != MediaKind.Movie && item.kind != MediaKind.Episode && detail.children.isNotEmpty()) {
        // Anything else that holds titles (an album, say, until music has its own screens) lists them.
        item(key = "children") {
            Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                SectionHeading(t("In this"))
                LazyRow(contentPadding = PaddingValues(horizontal = Edge), horizontalArrangement = Arrangement.spacedBy(RowSpacing)) {
                    items(detail.children, key = { it.id }) { PosterCard(it) }
                }
            }
        }
    }

    gaps()

    val rank = mapOf(CreditRole.Actor to 0, CreditRole.Creator to 1, CreditRole.Director to 2, CreditRole.Writer to 3)
    val cast = detail.credits.sortedBy { rank[it.role] ?: 4 }
    if (cast.isNotEmpty()) {
        item(key = "cast") {
            Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                SectionHeading(t(Words.castAndCrew))
                LazyRow(contentPadding = PaddingValues(horizontal = Edge), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    items(cast.take(30)) { credit ->
                        val navigator = LocalNavigator.current
                        PersonCard(credit.name, credit.character ?: credit.role.raw, credit.image) { navigator.openPerson(credit.personId, credit.name) }
                    }
                }
            }
        }
    }
    if (similar.isNotEmpty()) {
        item(key = "similar") {
            Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                SectionHeading(t(Words.moreLikeThis))
                LazyRow(contentPadding = PaddingValues(horizontal = Edge), horizontalArrangement = Arrangement.spacedBy(RowSpacing)) {
                    items(similar, key = { it.id }) { PosterCard(it, width = PosterWidth) }
                }
            }
        }
    }
    detail.files.firstOrNull()?.let { file -> item(key = "file") { FileInfo(file, detail.shownVersion, detail.studio) } }
}

@Composable
private fun SeasonPicker(seasons: List<ItemSummary>, selected: String?, onSeason: (String) -> Unit, ambient: Color, season: ItemDetail?, count: (ItemSummary) -> String? = { null }) {
    val model = LocalContext.current.appModel
    // "Download Season 2", on the pills' line at the end, outside their scrolling so it stays in view.
    val link: @Composable () -> Unit = {
        season?.takeIf { it.item.id == selected }?.let { SeasonDownloadLink(model, it.item, it.children, Modifier.padding(end = Edge - 4.dp)) }
    }
    if (seasons.size == 1) {
        val only = seasons.first()
        Row(verticalAlignment = Alignment.CenterVertically) {
            SectionHeading(count(only)?.let { "${only.title}: $it" } ?: only.title, Modifier.weight(1f))
            link()
        }
        return
    }
    val state = rememberLazyListState()
    // The chosen season fully in view, clear of the start and of the fade at the end, moving the row only as far as it
    // must: when it's pressed, and once it's known (the show's next-up episode decides it).
    LaunchedEffect(selected) {
        val index = seasons.indexOfFirst { it.id == selected }
        if (index < 0) return@LaunchedEffect
        // Offsets count from the end of the start padding; the clear room ends where the end padding (the fade) begins.
        fun room() = state.layoutInfo.viewportEndOffset - state.layoutInfo.afterContentPadding
        var pill = state.layoutInfo.visibleItemsInfo.firstOrNull { it.index == index }
        if (pill == null) {
            // Out of sight: brought to the start, then (if it lay beyond the end) back to rest against the end.
            val after = index > (state.layoutInfo.visibleItemsInfo.lastOrNull()?.index ?: -1)
            state.scrollToItem(index)
            pill = state.layoutInfo.visibleItemsInfo.firstOrNull { it.index == index } ?: return@LaunchedEffect
            if (after) {
                val back = pill.offset + pill.size - room()
                if (back < 0) state.scrollBy(back.toFloat())
            }
            return@LaunchedEffect
        }
        val overStart = pill.offset
        val overEnd = pill.offset + pill.size - room()
        when {
            overStart < 0 -> state.animateScrollBy(overStart.toFloat())
            overEnd > 0 -> state.animateScrollBy(overEnd.coerceAtMost(overStart).toFloat())
        }
    }
    // With the link beside them, the pills fade out before it (room for the last to scroll clear of the fade).
    val hasLink = season != null && season.item.id == selected && season.children.any { it.kind == MediaKind.Episode }
    val fade = 56.dp
    Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(16.dp)) {
        LazyRow(
            Modifier.weight(1f).then(if (hasLink) Modifier.fadeEnd(fade) else Modifier),
            state = state,
            contentPadding = PaddingValues(start = Edge, end = if (hasLink) fade else Edge),
            horizontalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            items(seasons, key = { it.id }) { season -> PillButton(season.title, season.id == selected, { onSeason(season.id) }, ambient, detail = count(season)) }
        }
        link()
    }
}

/** An episode in a season list: still with progress beside the number, title and time, then the overview. */
/** A row of [columns] episodes, sharing the width (one on a phone). */
@Composable
private fun EpisodeColumns(episodes: List<ItemSummary>, columns: Int, content: @Composable (ItemSummary) -> Unit) {
    if (columns == 1) return content(episodes.single())
    Row {
        episodes.forEach { Box(Modifier.weight(1f)) { content(it) } }
        repeat(columns - episodes.size) { Box(Modifier.weight(1f)) }
    }
}

@Composable
private fun EpisodeRow(episode: ItemSummary, overview: String?, isCurrent: Boolean, playsOnTap: Boolean) {
    val from = LocalPlayFrom.current
    val navigator = LocalNavigator.current
    var menu by remember { mutableStateOf(false) }
    val actions = itemActions(episode, showsDetails = true)
    Box(Modifier.padding(horizontal = Edge)) {
        Column(
            Modifier.fillMaxWidth().pressable(
                onClick = { if (playsOnTap) navigator.play(episode, from = from) else navigator.openItem(episode.id) },
                onLongClick = { menu = true },
            ).menuActions(actions),
            verticalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            Row(horizontalArrangement = Arrangement.spacedBy(12.dp), verticalAlignment = Alignment.CenterVertically) {
                Box(Modifier.width(140.dp).aspectRatio(16f / 9f).clip(RoundedCornerShape(Tokens.radiusArt)).background(Flexarr.seat)) {
                    RemoteImage(episode.artwork.thumb ?: episode.artwork.backdrop, 400, Modifier.fillMaxSize()) {
                        Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) { Icon(FlexarrIcons.play, contentDescription = null, tint = Flexarr.usher) }
                    }
                    episode.progress?.let { ProgressBar(it, Modifier.align(Alignment.BottomCenter).padding(6.dp)) }
                    if (episode.userData.watched) WatchedBadge(Modifier.align(Alignment.TopEnd).padding(5.dp))
                }
                Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(3.dp)) {
                    Text(
                        episode.episodeNumber?.let { t("Episode {number}", "number" to it) } ?: t("Special"),
                        fontSize = 12.sp,
                        fontWeight = FontWeight.SemiBold,
                        color = if (isCurrent) Flexarr.screen else Flexarr.usher,
                    )
                    Text(episode.title, fontSize = 15.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen, maxLines = 2, overflow = TextOverflow.Ellipsis)
                    episodeTime(episode)?.let { Text(it, fontSize = 12.sp, color = Flexarr.usher) }
                }
                // Its download, like Download on its own page; tapping it doesn't open or play the episode.
                TitleDownloadButton(LocalContext.current.appModel, episode)
            }
            overview?.takeIf { it.isNotEmpty() }?.let { Text(it, fontSize = 13.sp, color = Flexarr.usher, maxLines = 3, overflow = TextOverflow.Ellipsis) }
        }
        ItemMenu(actions, menu, onDismiss = { menu = false })
    }
}

private fun episodeTime(episode: ItemSummary): String? {
    val duration = episode.effectiveDuration
    if (episode.progress != null && duration != null) return Format.remaining(duration - episode.userData.positionSeconds)
    return episode.runtimeMinutes?.let(Format::runtime) ?: episode.durationSeconds?.let(Format::runtime)
}

@Composable
private fun FileInfo(file: MediaFile, version: TitleVersion?, studio: String?) {
    // With versions, the one that plays or is chosen, which may be in another copy than the one opened.
    val video = version?.let { listOfNotNull(it.label, it.videoCodec?.uppercase(Locale.ROOT)).joinToString(" ") }
        ?: listOfNotNull(file.height?.let { "${it}p" }, file.videoCodec?.uppercase(Locale.ROOT), file.pictureLabel).joinToString(" ")
    val audioCodec = if (version == null) file.audioCodec else version.audioCodec
    val channels = if (version == null) file.audioChannels else version.audioChannels
    val audio = listOfNotNull(audioCodec?.uppercase(Locale.ROOT), channels?.let(::channelsLabel)).joinToString(" ")
    val facts = listOfNotNull(
        video.takeIf { it.isNotEmpty() }?.let { t("Video") to it },
        audio.takeIf { it.isNotEmpty() }?.let { t("Audio") to it },
        studio?.let { t("Studio") to it },
        t("Size") to fileSizeLabel(version?.size ?: file.size),
    )
    Column(Modifier.padding(horizontal = Edge), verticalArrangement = Arrangement.spacedBy(14.dp)) {
        facts.chunked(2).forEach { pair ->
            Row(Modifier.fillMaxWidth()) {
                pair.forEach { (label, value) ->
                    Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                        Text(label, fontSize = 12.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.usher)
                        Text(value, fontSize = 15.sp, color = Flexarr.screen.copy(alpha = 0.85f))
                    }
                }
                if (pair.size == 1) Spacer(Modifier.weight(1f))
            }
        }
    }
}

/** "5.1", "Stereo", "Mono". */
internal fun channelsLabel(count: Int) = when (count) {
    1 -> t("Mono")
    2 -> t("Stereo")
    6 -> "5.1"
    8 -> "7.1"
    else -> t("{count} ch", "count" to count)
}

internal fun fileSizeLabel(bytes: Long): String {
    val gb = bytes / 1_000_000_000.0
    return if (gb >= 1) String.format(io.github.flexarr.core.i18n.Localization.locale, "%.1f GB", gb) else String.format(io.github.flexarr.core.i18n.Localization.locale, "%.0f MB", bytes / 1_000_000.0)
}

/** Drawn into its own layer, then faded to nothing over its last [width] at the end (the right, or the left right to left). */
private fun Modifier.fadeEnd(width: androidx.compose.ui.unit.Dp) = graphicsLayer(compositingStrategy = CompositingStrategy.Offscreen).drawWithContent {
    drawContent()
    val edge = width.toPx().coerceAtMost(size.width)
    val rtl = layoutDirection == androidx.compose.ui.unit.LayoutDirection.Rtl
    val (from, to) = if (rtl) edge to 0f else (size.width - edge) to size.width
    drawRect(Brush.horizontalGradient(listOf(Color.Black, Color.Transparent), startX = from, endX = to), blendMode = BlendMode.DstIn)
}

/** Drawn into its own layer, then faded to nothing over the bottom of it, so there's no seam with the wash behind. */
private fun Modifier.fadeOut() = graphicsLayer(compositingStrategy = CompositingStrategy.Offscreen).drawWithContent {
    drawContent()
    drawRect(Brush.verticalGradient(0.55f to Color.Black, 1f to Color.Transparent), blendMode = BlendMode.DstIn)
}
