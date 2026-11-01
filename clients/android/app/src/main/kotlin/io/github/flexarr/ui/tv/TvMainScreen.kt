package io.github.flexarr.ui.tv

import androidx.activity.compose.BackHandler
import androidx.activity.compose.LocalActivity
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.core.animateDpAsState
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInHorizontally
import androidx.compose.animation.slideOutHorizontally
import androidx.compose.foundation.background
import androidx.compose.foundation.focusGroup
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.material.icons.outlined.AutoAwesome
import androidx.compose.material.icons.outlined.GraphicEq
import androidx.compose.material.icons.outlined.Search
import androidx.compose.material.icons.outlined.SettingsInputAntenna
import io.github.flexarr.core.Library
import io.github.flexarr.core.LibrarySort
import io.github.flexarr.core.LibraryType
import io.github.flexarr.core.MenuItem
import io.github.flexarr.core.liveChannels
import io.github.flexarr.ui.components.Load
import io.github.flexarr.ui.components.rememberLoader
import io.github.flexarr.ui.library.LibraryMenuEntry
import io.github.flexarr.ui.library.icon
import io.github.flexarr.ui.library.onTv
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.Home
import androidx.compose.material.icons.outlined.Settings
import androidx.compose.material3.SnackbarHost
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.ExperimentalComposeUiApi
import androidx.compose.ui.Modifier
import androidx.navigation.NavController
import android.annotation.SuppressLint
import android.net.Uri
import androidx.navigation.NavGraph
import androidx.navigation.NavBackStackEntry
import androidx.compose.ui.draw.drawWithContent
import androidx.compose.ui.focus.FocusDirection
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusProperties
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.focus.focusRestorer
import androidx.compose.ui.focus.onFocusChanged
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.input.key.Key
import androidx.compose.ui.input.key.KeyEventType
import androidx.compose.ui.input.key.key
import androidx.compose.ui.input.key.onKeyEvent
import androidx.compose.ui.input.key.type
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalFocusManager
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.navigation.compose.NavHost
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import io.github.flexarr.AppLink
import androidx.navigation.compose.composable
import androidx.navigation.compose.currentBackStackEntryAsState
import androidx.navigation.compose.rememberNavController
import androidx.tv.material3.ClickableSurfaceDefaults
import androidx.tv.material3.Icon
import androidx.tv.material3.Surface
import androidx.tv.material3.Text
import io.github.flexarr.AppModel
import io.github.flexarr.BuildConfig
import io.github.flexarr.ui.Navigator
import io.github.flexarr.ui.components.BrandMark
import io.github.flexarr.ui.components.LocalClient
import io.github.flexarr.ui.components.LocalNavigator
import io.github.flexarr.ui.profiles.ProfileAvatar
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.marquee
import io.github.flexarr.ui.theme.Tokens

/** A screen of its own in the sidebar: Home, a library, Discover, Search or Settings. [route] is its first screen's. */
private data class TvTab(val route: String, val title: String, val icon: ImageVector) {
    companion object {
        val Home = TvTab("home", "Home", Icons.Outlined.Home)
        val LiveTv = TvTab("tab/livetv", "Live TV", Icons.Outlined.SettingsInputAntenna)
        val Discover = TvTab("discover", "Discover", Icons.Outlined.AutoAwesome)
        val Search = TvTab("search", "Search", Icons.Outlined.Search)
        val Settings = TvTab("settings", "Settings", Icons.Outlined.Settings)

        fun library(library: Library) = TvTab("tab/library/${library.id}", library.name, library.icon)
    }
}

/**
 * The app, as the Apple TV's MainTabView: a sidebar (who's watching, Home, the libraries, then Search and Settings) over
 * the screens. It's out of the way while browsing; Back on a tab's first screen, or left from the screen's left edge,
 * brings it in, and Back on it leaves the app. The libraries and Live TV are in the order the profile chose (Settings ›
 * Sidebar), with Now Playing while music plays.
 */
@OptIn(ExperimentalComposeUiApi::class)
@Composable
fun TvMainScreen(model: AppModel) {
    val client = model.client ?: return
    val context = LocalContext.current
    val nav = rememberNavController()
    val navigator = remember(nav) { Navigator(nav, model, context) }
    var tabRoute by rememberSaveable { mutableStateOf(TvTab.Home.route) }
    val libraries = rememberLoader(model.credentials, model.librariesVersion) { client.libraries() }
    val libraryList = (libraries.state as? Load.Loaded)?.value.orEmpty()
    // The libraries and Live TV in the profile's order. Podcasts stay on the phone and the web, and the DVR's Recordings
    // library has no tab of its own once Live TV has one (Live TV › Recordings).
    val libraryTabs = LibraryMenuEntry.ordered(model, libraryList).mapNotNull { entry ->
        when (entry) {
            is LibraryMenuEntry.Shelf -> entry.library.takeIf { it.onTv && !(model.hasLiveTv && it.isRecordings) }?.let(TvTab::library)
            // Left out of the menu, Live TV is still on Home and its links.
            LibraryMenuEntry.LiveTv -> TvTab.LiveTv.takeUnless { model.currentProfile?.hidesFromMenu(MenuItem.LIVE_TV) == true }
            is LibraryMenuEntry.Podcasts -> null
        }
    }
    var sidebarOpen by remember { mutableStateOf(false) }
    // The Now Playing bar's room at the foot, so the last row isn't under it.
    var barShown by remember { mutableStateOf(false) }
    val barRoom by animateDpAsState(if (barShown) NOW_PLAYING_BAR_ROOM else 0.dp, label = "bar room")
    var choosingProfile by remember { mutableStateOf(false) }
    // Now Playing over everything, as Play or Shuffle opens it (the Music app's way): Back closes it, back to where
    // it was pressed.
    var showingNowPlaying by remember { mutableStateOf(false) }
    var nowPlayingSeen by remember { mutableIntStateOf(model.nowPlayingRequests) }
    LaunchedEffect(model.nowPlayingRequests) {
        if (model.nowPlayingRequests != nowPlayingSeen && model.music.current != null) showingNowPlaying = true
        nowPlayingSeen = model.nowPlayingRequests
    }
    // The screen a tab opens on takes focus (Home's Play, a library's first title, Search's field) as the app opens and
    // as a tab is chosen, so it's ready to browse. Named by route, so the tab being left (still there as the new one
    // comes) doesn't take it.
    var arrivingAt by remember { mutableStateOf<String?>(TvTab.Home.route) }
    val content = remember { FocusRequester() }
    val focusManager = LocalFocusManager.current
    val entry by nav.currentBackStackEntryAsState()
    // Choosing a tab clears the screens before it, so its first screen is the only one.
    val atTabRoot = entry != null && nav.previousBackStackEntry == null

    // Signed in (or as someone else): commands for this device ("play on the living room TV") come to this sign-in.
    LaunchedEffect(model.credentials) { model.listenForCommands() }
    // Whether the server has Live TV, for its place in the sidebar.
    LaunchedEffect(model.credentials, model.isKids) { model.loadLiveTvStatus() }

    fun closeSidebar() {
        sidebarOpen = false
        runCatching { content.requestFocus() }
    }

    // Each tab's screens beyond its first (Movies › a genre › a title), kept as it's left and put back as it's chosen
    // again, as the Apple TV keeps a stack per tab.
    val tabStacks = remember { mutableMapOf<String, List<String>>() }

    fun select(chosen: TvTab) {
        if (chosen.route == tabRoute && atTabRoot) return closeSidebar()
        // The tab being left keeps its screens; choosing the tab that's showing goes back to its first.
        if (chosen.route != tabRoute) {
            tabStacks[tabRoute] = nav.screensAboveTab()
        }
        val saved = tabStacks.remove(chosen.route).takeIf { chosen.route != tabRoute }.orEmpty()
        tabRoute = chosen.route
        // Coming back to a screen deeper in, that screen takes focus as it opens, not the tab's first.
        arrivingAt = chosen.route.takeIf { saved.isEmpty() }
        nav.navigate(chosen.route) {
            popUpTo(nav.graph.id) { inclusive = true }
            launchSingleTop = true
        }
        saved.forEach { nav.navigate(it) }
        closeSidebar()
    }

    // Back to a screen (from a title, say): if nothing on it has focus, its last focused item or first takes it, so
    // the next press isn't spent finding it.
    var contentHasFocus by remember { mutableStateOf(false) }
    LaunchedEffect(entry?.id) {
        delay(150)
        if (!contentHasFocus && !sidebarOpen && !showingNowPlaying && !choosingProfile && arrivingAt == null) {
            runCatching { content.requestFocus() }
        }
    }

    // Now Playing and "Who's watching?" keep focus to themselves while they're up (see trapsFocus); once one has gone,
    // focus comes back to the screen underneath, to where it was.
    val overlayUp = showingNowPlaying || choosingProfile
    var overlayWasUp by remember { mutableStateOf(false) }
    LaunchedEffect(overlayUp) {
        if (overlayUp) {
            overlayWasUp = true
        } else if (overlayWasUp) {
            overlayWasUp = false
            runCatching { content.requestFocus() }
        }
    }

    // A flexarr:// link, the Assistant ("play Fargo on Flexarr") or play-on from a phone to a profile that needed
    // choosing asked to go somewhere, as on the phone's MainScreen. Podcasts stay on the phone and the web.
    val linkScope = rememberCoroutineScope()
    LaunchedEffect(model.pendingLink) {
        val link = model.pendingLink ?: return@LaunchedEffect
        // Cleared at once, which restarts this effect: what it does runs on in a scope of its own.
        model.pendingLink = null
        showingNowPlaying = false
        choosingProfile = false
        sidebarOpen = false
        linkScope.launch { when (link) {
            is AppLink.Item -> navigator.openItem(link.id)
            is AppLink.Play -> {
                val item = if (link.shuffle) null else runCatching { client.item(link.id) }.getOrNull()
                when {
                    link.shuffle -> navigator.play(link.id, shuffle = true)
                    item == null -> navigator.openItem(link.id)
                    link.startAt != null && item.item.kind.isPlayable -> navigator.play(link.id, startAt = link.startAt)
                    else -> navigator.playBest(item.item)
                }
            }
            is AppLink.Live -> {
                // Every channel to change through, favourites first; one that's gone leaves the guide open.
                val channels = runCatching { client.liveChannels() }.getOrNull().orEmpty()
                val channel = channels.firstOrNull { it.id == link.channelId }
                if (channel != null) {
                    navigator.watch(channel, channels.filter { it.isFavourite } + channels.filterNot { it.isFavourite })
                } else if (model.hasLiveTv) {
                    select(TvTab.LiveTv)
                }
            }
            AppLink.LiveTv -> if (model.hasLiveTv) select(TvTab.LiveTv)
            is AppLink.AsProfile -> {
                val profile = model.profiles.firstOrNull { it.id == link.profile }
                    ?: model.profiles.firstOrNull { it.name.equals(link.profile, ignoreCase = true) }
                when {
                    // An unknown profile, or the one watching: the link goes ahead as things are.
                    profile == null || profile.isCurrent -> model.pendingLink = link.link
                    !link.askFirst && !profile.takesPin && runCatching { model.chooseProfile(profile) }.isSuccess ->
                        model.pendingLink = link.link
                    else -> {
                        // "Who's watching?" (with the PIN, if it has one); the link goes ahead once someone's chosen.
                        model.commands.active?.stop()
                        model.pendingLink = link.link
                        model.switchProfile()
                    }
                }
            }
            AppLink.Search -> select(TvTab.Search)
            AppLink.Home -> select(TvTab.Home)
            // Podcasts, downloads and the watchlist aren't on the TV yet, and nothing here sends it a pairing or PIN notification.
            is AppLink.Podcast, is AppLink.PodcastEpisode, AppLink.Downloads, is AppLink.Pairing, AppLink.Watchlist, AppLink.Settings, is AppLink.Join -> Unit
        } }
    }
    // Words said to the Assistant ("search Flexarr for Fargo"): Search takes them in.
    LaunchedEffect(model.pendingSearch) {
        if (model.pendingSearch != null && tabRoute != TvTab.Search.route) select(TvTab.Search)
    }

    // Back on a tab's first screen brings in the sidebar; on the sidebar, it leaves the app.
    BackHandler(enabled = !sidebarOpen && atTabRoot) { sidebarOpen = true }

    CompositionLocalProvider(LocalClient provides client, LocalNavigator provides navigator) {
        Box(Modifier.fillMaxSize()) {
            Box(
                // Under Now Playing, which covers the screen, the tab isn't drawn: on a 2 GB stick the light behind it
                // needs what's spare.
                Modifier.fillMaxSize().drawWithContent { if (!showingNowPlaying) drawContent() }.padding(bottom = barRoom).focusRequester(content).onFocusChanged { contentHasFocus = it.hasFocus }.focusRestorer().focusGroup().onKeyEvent { event ->
                    // The remote's play/pause runs the music from any screen (the players and Now Playing take it while
                    // they're up).
                    if (event.type == KeyEventType.KeyDown && event.key == Key.MediaPlayPause && model.music.current != null) {
                        model.music.togglePlayPause()
                        return@onKeyEvent true
                    }
                    // Left with nothing further left on the screen: the sidebar.
                    if (sidebarOpen || event.type != KeyEventType.KeyDown || event.key != Key.DirectionLeft) return@onKeyEvent false
                    if (!focusManager.moveFocus(FocusDirection.Left)) sidebarOpen = true
                    true
                },
            ) {
                // A library that isn't in the list: still loading, failed to load (with Try again), or gone (another
                // profile can't see it, or it was removed).
                @Composable
                fun MissingLibrary() = when (val state = libraries.state) {
                    is Load.Failed -> TvStatusView(state.message, retry = libraries.retry)
                    is Load.Loaded -> TvStatusView("This library isn’t here any more.", empty = true)
                    Load.Loading -> TvStatusView()
                }
                fun arrives(route: String) = arrivingAt == route
                fun arrived(route: String): () -> Unit = { if (arrivingAt == route) arrivingAt = null }
                NavHost(nav, startDestination = TvTab.Home.route) {
                    composable(TvTab.Home.route) { TvHomeScreen(model, takeFocus = arrives(TvTab.Home.route), onFocusTaken = arrived(TvTab.Home.route)) }
                    composable("tab/library/{id}") { entry ->
                        val id = entry.arguments?.getString("id")
                        val route = "tab/library/$id"
                        // A type the TV doesn't show (one it doesn't know, or audiobooks) is never a film grid.
                        when (val library = libraryList.firstOrNull { it.id == id && it.onTv }) {
                            null -> MissingLibrary()
                            // Music has a screen of its own: artists, new albums and playlists.
                            else -> if (library.type == LibraryType.Music) {
                                TvMusicLibraryScreen(model, library, takeFocus = arrives(route), onFocusTaken = arrived(route))
                            } else {
                                TvLibraryTab(model, library, takeFocus = arrives(route), onFocusTaken = arrived(route))
                            }
                        }
                    }
                    composable(TvTab.LiveTv.route) { TvLiveTvScreen(model, takeFocus = arrives(TvTab.LiveTv.route), onFocusTaken = arrived(TvTab.LiveTv.route)) }
                    composable(TvTab.Discover.route) { TvDiscoverScreen(model) }
                    composable(TvTab.Search.route) { TvSearchScreen(model, takeFocus = arrives(TvTab.Search.route), onFocusTaken = arrived(TvTab.Search.route)) }
                    composable(TvTab.Settings.route) { TvSettingsScreen(model, onSwitchProfile = { choosingProfile = true }) }
                    composable("item/{id}") { TvDetailScreen(model, it.arguments!!.getString("id")!!) }
                    composable("coming-soon") { TvComingSoonScreen(model) }
                    composable("artist/{id}") { TvArtistScreen(model, it.arguments!!.getString("id")!!) }
                    composable("album/{id}") { TvAlbumScreen(model, it.arguments!!.getString("id")!!) }
                    composable("playlist/{id}") { TvPlaylistScreen(model, it.arguments!!.getString("id")!!) }
                    composable("person/{id}?name={name}") { entry ->
                        TvPersonScreen(model, entry.arguments!!.getString("id")!!, entry.arguments?.getString("name").orEmpty())
                    }
                    // A row's "See all" or a genre: the library's titles in a grid, on top, so Back comes back.
                    composable("library/{id}?sort={sort}&genre={genre}") { entry ->
                        val id = entry.arguments?.getString("id")
                        val sort = LibrarySort.entries.firstOrNull { it.raw == entry.arguments?.getString("sort") } ?: LibrarySort.Title
                        val genre = entry.arguments?.getString("genre")?.takeIf { it.isNotEmpty() }
                        libraryList.firstOrNull { it.id == id && it.onTv }?.let { TvLibraryGrid(model, it, initialSort = sort, initialGenre = genre) } ?: MissingLibrary()
                    }
                }
            }
            // Music carries on under every screen; the bar says what's playing.
            TvNowPlayingBar(model, Modifier.align(Alignment.BottomCenter), onShown = { barShown = it })
            val showsDiscover = model.currentProfile?.hidesFromMenu(MenuItem.DISCOVER) != true
            val tabs = listOf(TvTab.Home) + libraryTabs + listOfNotNull(TvTab.Discover.takeIf { showsDiscover }, TvTab.Search, TvTab.Settings)
            Sidebar(model, sidebarOpen, tabs, tabRoute, onSelect = ::select, onProfile = {
                closeSidebar()
                choosingProfile = true
            }, onNowPlaying = {
                sidebarOpen = false
                showingNowPlaying = true
            }, onClose = ::closeSidebar, onFocusLeft = { sidebarOpen = false }, closesOnBack = !atTabRoot)
            SnackbarHost(navigator.notices, Modifier.align(Alignment.BottomCenter).padding(bottom = 30.dp))
        }
        if (showingNowPlaying) {
            Box(Modifier.trapsFocus()) { TvNowPlayingScreen(model, onClose = { showingNowPlaying = false }) }
        }
        if (choosingProfile) {
            Box(Modifier.trapsFocus()) { TvProfilePicker(model, marksCurrent = true, onDone = { choosingProfile = false }) }
        }
    }
}

/**
 * The sidebar, over the left of the screen: who's watching (with several profiles), the tabs, and the logo and
 * version at its foot. Choosing a tab, Right or Back (on a screen opened from a tab) puts it away.
 */
@Composable
private fun Sidebar(model: AppModel, open: Boolean, tabs: List<TvTab>, selected: String, onSelect: (TvTab) -> Unit, onProfile: () -> Unit, onNowPlaying: () -> Unit, onClose: () -> Unit, onFocusLeft: () -> Unit, closesOnBack: Boolean) {
    AnimatedVisibility(open, enter = fadeIn(), exit = fadeOut()) {
        Box(Modifier.fillMaxSize().background(Brush.horizontalGradient(0f to Color.Black.copy(alpha = 0.7f), 0.5f to Color.Transparent)))
    }
    AnimatedVisibility(open, enter = slideInHorizontally { -it } + fadeIn(), exit = slideOutHorizontally { -it } + fadeOut()) {
        val first = remember { FocusRequester() }
        var hasFocus by remember { mutableStateOf(false) }
        val list = rememberLazyListState()
        val nowPlaying = model.music.current != null
        // Focus starts on the tab that's showing (the first, if it's gone from the list).
        val focusRoute = tabs.firstOrNull { it.route == selected }?.route ?: tabs.firstOrNull()?.route
        LaunchedEffect(Unit) {
            // With many libraries the tab may be below the sidebar's fold, with no row to focus until it's scrolled to.
            val index = tabs.indexOfFirst { it.route == focusRoute }
            if (index >= 0) runCatching { list.scrollToItem(index + if (nowPlaying) 1 else 0) }
            // The row's placed a frame or two after the sidebar starts sliding in: keep asking until it has focus.
            for (attempt in 0 until 10) {
                runCatching { first.requestFocus() }
                delay(50)
                if (hasFocus) break
            }
        }
        // Back on the sidebar: it goes (over a screen opened from a tab) or the app does (over a tab's first screen),
        // rather than reaching a screen underneath that goes back through pages of its own (Settings').
        val activity = LocalActivity.current
        BackHandler { if (closesOnBack) onClose() else activity?.finish() }
        Column(
            Modifier.fillMaxHeight().width(230.dp).padding(12.dp).background(Flexarr.seat.copy(alpha = 0.92f), RoundedCornerShape(Tokens.radiusPanel)).padding(vertical = 18.dp, horizontal = 10.dp)
                .onFocusChanged {
                    // Focus gone from it by any other way than choosing (up past the top, or Now Playing taking it):
                    // put it away too, leaving focus where it went.
                    if (hasFocus && !it.hasFocus) onFocusLeft()
                    hasFocus = it.hasFocus
                }
                .onKeyEvent { event ->
                    (event.type == KeyEventType.KeyDown && event.key == Key.DirectionRight).also { if (it) onClose() }
                },
            verticalArrangement = Arrangement.spacedBy(4.dp),
        ) {
            if (model.profiles.size > 1) {
                model.currentProfile?.let { current ->
                    SidebarRow(current.name, selected = false, onClick = onProfile, icon = { ProfileAvatar(model, current, size = 22) })
                    Spacer(Modifier.height(8.dp))
                }
            }
            // However many libraries there are, the list scrolls rather than pushing Search and Settings out of reach.
            LazyColumn(Modifier.weight(1f), state = list, verticalArrangement = Arrangement.spacedBy(4.dp)) {
                // Music that's playing gets a place of its own, as the Music app does: the surest way to reach it.
                if (nowPlaying) {
                    item(key = "now-playing") {
                        SidebarRow("Now Playing", selected = false, onClick = onNowPlaying, icon = { Icon(Icons.Outlined.GraphicEq, contentDescription = null, modifier = Modifier.size(20.dp)) })
                    }
                }
                items(tabs, key = { it.route }) { tab ->
                    if (tab == (if (TvTab.Discover in tabs) TvTab.Discover else TvTab.Search)) {
                        // A rule between the libraries and the rest.
                        Box(Modifier.padding(horizontal = 12.dp, vertical = 8.dp).fillMaxWidth().height(1.dp).background(Flexarr.aisle))
                    }
                    SidebarRow(
                        tab.title,
                        selected = tab.route == selected,
                        onClick = { onSelect(tab) },
                        modifier = if (tab.route == focusRoute) Modifier.focusRequester(first) else Modifier,
                        icon = { Icon(tab.icon, contentDescription = null, modifier = Modifier.size(20.dp)) },
                    )
                }
            }
            // The logo and the app's version, quietly at the foot.
            Row(Modifier.padding(horizontal = 12.dp), horizontalArrangement = Arrangement.spacedBy(6.dp), verticalAlignment = Alignment.CenterVertically) {
                BrandMark(13.dp)
                Text("flexarr", style = marquee(11, Flexarr.screen.copy(alpha = 0.8f)))
                Text(BuildConfig.VERSION_NAME, fontSize = 11.sp, fontWeight = FontWeight.Medium, color = Flexarr.usher.copy(alpha = 0.7f), modifier = Modifier.padding(start = 6.dp))
            }
        }
    }
}

@Composable
private fun SidebarRow(title: String, selected: Boolean, onClick: () -> Unit, modifier: Modifier = Modifier, icon: @Composable () -> Unit) {
    Surface(
        onClick = onClick,
        modifier = modifier.fillMaxWidth().height(40.dp),
        shape = ClickableSurfaceDefaults.shape(RoundedCornerShape(Tokens.radiusControl)),
        colors = ClickableSurfaceDefaults.colors(
            containerColor = if (selected) Flexarr.screen.copy(alpha = 0.14f) else Color.Transparent,
            contentColor = if (selected) Flexarr.screen else Flexarr.screen.copy(alpha = 0.8f),
            focusedContainerColor = Flexarr.screen,
            focusedContentColor = Flexarr.room,
        ),
        scale = ClickableSurfaceDefaults.scale(focusedScale = 1.03f),
    ) {
        Row(Modifier.fillMaxSize().padding(horizontal = 12.dp), horizontalArrangement = Arrangement.spacedBy(12.dp), verticalAlignment = Alignment.CenterVertically) {
            icon()
            Text(title, fontSize = 15.sp, fontWeight = FontWeight.SemiBold, maxLines = 1)
        }
    }
}

/**
 * Keeps focus inside something drawn over the screens (Now Playing, "Who's watching?"): the screens underneath stay
 * focusable, and without this, moving past the overlay's last control would land on them, out of sight.
 */
private fun Modifier.trapsFocus() = focusProperties { onExit = { cancelFocusChange() } }.focusGroup()

/**
 * The routes of the screens above the tab's first, to open again later. The back stack is the only list of them; the
 * phone's MainScreen reads it the same way (navigation marks it for its own library's use, but has no other).
 */
@SuppressLint("RestrictedApi")
private fun NavController.screensAboveTab(): List<String> =
    currentBackStack.value.filter { it.destination !is NavGraph }.drop(1).map { it.concreteRoute() }

/** The route that opened [this] screen, its arguments filled in, to open it again ("item/123", "library/9?sort=title&genre=Drama"). */
private fun NavBackStackEntry.concreteRoute(): String = fillRoute(destination.route.orEmpty()) { arguments?.getString(it) }

/** [pattern] ("person/{id}?name={name}") with each argument's value put in, encoded; a missing one is left empty. */
internal fun fillRoute(pattern: String, argument: (String) -> String?): String =
    Regex("\\{(\\w+)\\}").replace(pattern) { match -> Uri.encode(argument(match.groupValues[1]).orEmpty()) }

/** What the Now Playing bar takes at the foot of the screen: its 44 points and the gap beneath. */
private val NOW_PLAYING_BAR_ROOM = 56.dp

/** The library the server makes for the DVR's recordings when the first one's done: a TV library called Recordings. */
private val Library.isRecordings: Boolean get() = type == LibraryType.TvShows && name == "Recordings"
