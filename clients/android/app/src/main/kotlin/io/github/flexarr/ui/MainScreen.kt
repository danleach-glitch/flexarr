package io.github.flexarr.ui

import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.padding
import androidx.activity.compose.LocalActivity
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.DownloadForOffline
import androidx.compose.material.icons.filled.GridView
import androidx.compose.material.icons.filled.Search
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material3.Icon
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.NavigationBarItemDefaults
import androidx.compose.material3.NavigationRail
import androidx.compose.material3.NavigationRailItem
import androidx.compose.material3.NavigationRailItemDefaults
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import io.github.flexarr.ui.components.isWide
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Snackbar
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.sp
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.painter.Painter
import androidx.compose.ui.graphics.vector.rememberVectorPainter
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.painterResource
import androidx.navigation.NavGraph.Companion.findStartDestination
import androidx.navigation.NavType
import androidx.navigation.compose.NavHost
import androidx.navigation.compose.composable
import androidx.navigation.compose.currentBackStackEntryAsState
import androidx.navigation.compose.rememberNavController
import androidx.navigation.navArgument
import io.github.flexarr.AppModel
import io.github.flexarr.R
import io.github.flexarr.core.LibrarySort
import io.github.flexarr.ui.components.LocalClient
import io.github.flexarr.ui.components.LocalNavigator
import io.github.flexarr.ui.detail.DetailScreen
import io.github.flexarr.ui.downloads.DownloadsScreen
import io.github.flexarr.ui.home.HomeScreen
import io.github.flexarr.ui.library.LibrariesScreen
import io.github.flexarr.ui.library.LibraryScreen
import io.github.flexarr.ui.livetv.LiveRecordingsScreen
import io.github.flexarr.ui.livetv.LiveTvScreen
import io.github.flexarr.ui.music.AlbumScreen
import io.github.flexarr.ui.music.ArtistScreen
import io.github.flexarr.ui.music.MusicLibraryScreen
import io.github.flexarr.ui.music.MusicNowPlayingBar
import io.github.flexarr.ui.music.PlaylistScreen
import io.github.flexarr.ui.podcasts.NowPlayingBar
import io.github.flexarr.ui.podcasts.PodcastBrowseScreen
import io.github.flexarr.ui.podcasts.PodcastScreen
import io.github.flexarr.ui.podcasts.PodcastsScreen
import io.github.flexarr.ui.search.PersonScreen
import io.github.flexarr.ui.search.SearchScreen
import io.github.flexarr.ui.watchlist.WatchlistScreen
import io.github.flexarr.ui.pairing.PairingPrompt
import io.github.flexarr.AppLink
import io.github.flexarr.PairingPrompts
import io.github.flexarr.ReviewPrompt
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import io.github.flexarr.core.liveChannels
import io.github.flexarr.core.podcastEpisode
import io.github.flexarr.ui.settings.DiagnosticsSettingsScreen
import io.github.flexarr.ui.settings.NotificationSettingsScreen
import io.github.flexarr.ui.settings.WaitingDevicesScreen
import io.github.flexarr.ui.settings.DownloadSettingsScreen
import io.github.flexarr.ui.settings.LibraryOrderSettingsScreen
import io.github.flexarr.ui.settings.PlaybackSettingsScreen
import io.github.flexarr.ui.settings.PodcastSettingsScreen
import io.github.flexarr.ui.settings.ServerSettingsScreen
import io.github.flexarr.ui.settings.SettingsScreen
import io.github.flexarr.ui.theme.Flexarr

private enum class Tab(val route: String, val label: String) {
    Home("home", "Home"),
    Libraries("libraries", "Libraries"),
    Downloads("downloads", "Downloads"),
    Search("search", "Search"),
    Settings("settings", "Settings"),
}

/** Home, Libraries, Downloads, Search and Settings along the bottom, each keeping its own place; titles open on top of them. */
@Composable
fun MainScreen(model: AppModel) {
    // One client for the sign-in, rather than a new one each time the screen recomposes.
    val credentials = model.credentials ?: return
    val client = remember(credentials) { model.client } ?: return
    val nav = rememberNavController()
    val context = LocalContext.current
    val navigator = remember(nav, model) { Navigator(nav, model, context) }
    // The tab stays lit while a title opened from it is on top.
    var currentTab by rememberSaveable { mutableStateOf(Tab.Home) }

    // Whether this profile has podcasts (Libraries lists them) and how it listens to them.
    LaunchedEffect(model.credentials, model.isKids) { model.loadPodcastStatus() }
    // Whether the server has Live TV (Libraries lists it).
    LaunchedEffect(model.credentials, model.isKids) { model.loadLiveTvStatus() }
    // Signed in (or as someone else): commands for this device come to this sign-in.
    LaunchedEffect(model.credentials) { model.listenForCommands() }
    // Whether this profile may manage the server, and so is asked to let devices in.
    LaunchedEffect(model.credentials) { model.loadSignIn() }
    // Whether the server keeps watchlists, and what's on this profile's (cards' menus offer it).
    LaunchedEffect(model.credentials, model.isKids) { model.refreshWatchlist() }
    // Downloads follow the server and profile signed in.
    LaunchedEffect(model.credentials, model.currentProfile?.id) { model.downloads.attach(client, model.currentProfile?.id) }
    // Google Play's rating prompt, once the player has closed and the app is back in front (ReviewPrompt says when).
    val activity = LocalActivity.current
    val lifecycle by LocalLifecycleOwner.current.lifecycle.currentStateFlow.collectAsState()
    val inFront = lifecycle.isAtLeast(Lifecycle.State.RESUMED)
    LaunchedEffect(ReviewPrompt.isDue, inFront) {
        if (!ReviewPrompt.isDue || !inFront || activity == null) return@LaunchedEffect
        delay(1_500)
        ReviewPrompt.ask(activity)
    }

    CompositionLocalProvider(LocalClient provides client, LocalNavigator provides navigator) {
        // Back can leave a tab's page for another's: the lit tab follows whichever tab's page is showing.
        val entry by nav.currentBackStackEntryAsState()
        LaunchedEffect(entry) {
            Tab.entries.firstOrNull { it.route == entry?.destination?.route }?.let { currentTab = it }
        }
        fun select(tab: Tab) {
            if (tab == currentTab && nav.currentBackStack.value.any { it.destination.route == tab.route }) {
                // Tapping the tab you're on goes back to its top, as on iPhone.
                nav.popBackStack(tab.route, inclusive = false)
            } else {
                currentTab = tab
                nav.navigate(tab.route) {
                    popUpTo(nav.graph.findStartDestination().id) { saveState = true }
                    launchSingleTop = true
                    restoreState = true
                }
            }
        }
        // A child chosen (or the device locked) from the picker in Settings: Settings isn't theirs, so they go Home.
        LaunchedEffect(model.isKids) {
            if (model.isKids && currentTab == Tab.Settings) {
                currentTab = Tab.Home
                nav.navigate(Tab.Home.route) {
                    popUpTo(nav.graph.findStartDestination().id)
                    launchSingleTop = true
                }
            }
        }
        // A notification, shortcut, the widget or another app's flexarr:// link asked to go somewhere.
        val linkScope = rememberCoroutineScope()
        LaunchedEffect(model.pendingLink) {
            val link = model.pendingLink ?: return@LaunchedEffect
            // Cleared at once, which restarts this effect: what it does runs on in a scope of its own.
            model.pendingLink = null
            // A kids profile has no podcasts, watchlist or Live TV: links to them open Home.
            val notForKids = link is AppLink.Podcast || link is AppLink.PodcastEpisode || link == AppLink.Watchlist ||
                link is AppLink.Live || link == AppLink.LiveTv
            linkScope.launch { when {
                model.isKids && notForKids -> select(Tab.Home)
                else -> when (link) {
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
                    } else {
                        navigator.openLiveTv()
                    }
                }
                AppLink.LiveTv -> navigator.openLiveTv()
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
                is AppLink.Podcast -> nav.navigate("podcast/${link.id}")
                is AppLink.PodcastEpisode -> runCatching { client.podcastEpisode(link.id) }.onSuccess {
                    model.podcasts.play(it)
                    model.showsNowPlaying = true
                }
                AppLink.Search -> select(Tab.Search)
                AppLink.Downloads -> select(Tab.Downloads)
                AppLink.Home -> select(Tab.Home)
                AppLink.Settings -> if (!model.isKids) select(Tab.Settings)
                // On Home, so Back leaves it for Home.
                AppLink.Watchlist -> {
                    select(Tab.Home)
                    navigator.openWatchlist()
                }
                // The prompt to allow it comes up by itself; a device that's gone is said so.
                is AppLink.Pairing -> when (model.pairing.open(client, link.code)) {
                    PairingPrompts.Opened.Asking -> Unit
                    PairingPrompts.Opened.Gone -> navigator.notices.showSnackbar("That device isn’t waiting any more. If it shows a new code, it asks again.")
                    PairingPrompts.Opened.NotMainProfile -> navigator.notices.showSnackbar("Switch to the main profile to allow it.")
                }
                // Asked about over every screen (InviteDialog), never sent here.
                is AppLink.Join -> Unit
            } } }
        }
        // With no connection to the server, the app opens on what's downloaded.
        LaunchedEffect(model.serverUnreachable) {
            if (model.serverUnreachable && model.downloads.downloads.isNotEmpty() && currentTab == Tab.Home) select(Tab.Downloads)
        }
        // A tablet has the tabs down the side, as iPad's sidebar-style tab bar keeps the width for the screens.
        val rail = isWide
        Scaffold(
            containerColor = Flexarr.room,
            snackbarHost = {
                SnackbarHost(navigator.notices) { Snackbar(it, containerColor = Flexarr.seat, contentColor = Flexarr.screen) }
            },
            bottomBar = {
              Column {
                // Music or a podcast carries on under every screen; the bar sits above the tabs and opens Now Playing.
                if (model.music.current != null) MusicNowPlayingBar(model) else NowPlayingBar(model)
                if (!rail) {
                    NavigationBar(containerColor = Flexarr.room.copy(alpha = 0.96f)) {
                        Tab.entries.filter { it != Tab.Settings || !model.isKids }.forEach { tab ->
                            NavigationBarItem(
                                selected = tab == currentTab,
                                onClick = { select(tab) },
                                icon = { Icon(tab.icon(), contentDescription = null) },
                                label = { TabLabel(tab.label) },
                                colors = NavigationBarItemDefaults.colors(
                                    selectedIconColor = Flexarr.screen,
                                    selectedTextColor = Flexarr.screen,
                                    unselectedIconColor = Flexarr.usher,
                                    unselectedTextColor = Flexarr.usher,
                                    indicatorColor = Color.Transparent,
                                ),
                            )
                        }
                    }
                }
              }
            },
        ) { padding ->
          Row {
            if (rail) {
                NavigationRail(containerColor = Flexarr.room, modifier = Modifier.padding(bottom = padding.calculateBottomPadding())) {
                    Spacer(Modifier.weight(1f))
                    Tab.entries.filter { it != Tab.Settings || !model.isKids }.forEach { tab ->
                        NavigationRailItem(
                            selected = tab == currentTab,
                            onClick = { select(tab) },
                            icon = { Icon(tab.icon(), contentDescription = null) },
                            label = { TabLabel(tab.label) },
                            colors = NavigationRailItemDefaults.colors(
                                selectedIconColor = Flexarr.screen,
                                selectedTextColor = Flexarr.screen,
                                unselectedIconColor = Flexarr.usher,
                                unselectedTextColor = Flexarr.usher,
                                indicatorColor = Flexarr.seat,
                            ),
                        )
                    }
                    Spacer(Modifier.weight(1f))
                }
            }
            NavHost(nav, startDestination = Tab.Home.route, modifier = Modifier.padding(bottom = padding.calculateBottomPadding())) {
                composable(Tab.Home.route) { HomeScreen(model) }
                composable(Tab.Libraries.route) { LibrariesScreen(model) }
                composable(Tab.Downloads.route) { DownloadsScreen(model) }
                composable(Tab.Search.route) { SearchScreen(model) }
                composable(Tab.Settings.route) {
                    SettingsScreen(
                        model,
                        onServer = { nav.navigate("settings/server") },
                        onLibraryOrder = { nav.navigate("settings/library-order") },
                        onPlayback = { nav.navigate("settings/playback") },
                        onDownloads = { nav.navigate("settings/downloads") },
                        onPodcasts = { nav.navigate("settings/podcasts") },
                        onNotifications = { nav.navigate("settings/notifications") },
                    )
                }
                composable("settings/playback") { PlaybackSettingsScreen(model) }
                composable("settings/downloads") { DownloadSettingsScreen(model) }
                composable("settings/podcasts") { PodcastSettingsScreen(model, onNotifications = { nav.navigate("settings/notifications") }) }
                composable("settings/server") { ServerSettingsScreen(model, onDiagnostics = { nav.navigate("settings/diagnostics") }) }
                composable("settings/diagnostics") { DiagnosticsSettingsScreen(model) }
                composable("settings/notifications") { NotificationSettingsScreen(model) }
                composable("settings/waiting-devices") { WaitingDevicesScreen(model) }
                composable("settings/library-order") { LibraryOrderSettingsScreen(model) }
                composable(
                    "library/{id}?sort={sort}&genre={genre}",
                    arguments = listOf(
                        navArgument("sort") { type = NavType.StringType; defaultValue = "" },
                        navArgument("genre") { type = NavType.StringType; defaultValue = "" },
                    ),
                ) { entry ->
                    val sort = LibrarySort.entries.firstOrNull { it.raw == entry.arguments?.getString("sort") } ?: LibrarySort.Title
                    LibraryScreen(model, entry.arguments?.getString("id")!!, sort, entry.arguments?.getString("genre")?.takeIf { it.isNotEmpty() })
                }
                composable("livetv") { LiveTvScreen(model) }
                composable("watchlist") { WatchlistScreen(model) }
                composable("livetv/recordings") { LiveRecordingsScreen(model) }
                composable("music/{id}") { MusicLibraryScreen(model, it.arguments?.getString("id")!!) }
                composable("artist/{id}") { ArtistScreen(model, it.arguments?.getString("id")!!) }
                composable("album/{id}") { AlbumScreen(model, it.arguments?.getString("id")!!) }
                composable("playlist/{id}") { PlaylistScreen(model, it.arguments?.getString("id")!!) }
                composable("podcasts") { PodcastsScreen(model) }
                composable("podcasts/browse") { PodcastBrowseScreen(model) }
                composable("podcast/{id}") { PodcastScreen(model, it.arguments?.getString("id")!!) }
                composable("item/{id}") { DetailScreen(model, it.arguments?.getString("id")!!) }
                composable(
                    "person/{id}?name={name}",
                    arguments = listOf(navArgument("name") { type = NavType.StringType; defaultValue = "" }),
                ) { entry -> PersonScreen(model, entry.arguments?.getString("id")!!, entry.arguments?.getString("name").orEmpty()) }
            }
          }
        }
        // A device asking to connect, over whichever screen is up.
        if (model.isMainProfile) PairingPrompt(model, client)
    }
}

/**
 * A tab's name on one line. It grows with the system's font size up to a third larger, then stops, so "Libraries"
 * still fits five across at the largest sizes (the screens' own text keeps growing).
 */
@Composable
private fun TabLabel(text: String) {
    val scale = LocalDensity.current.fontScale
    val size = if (scale > MAX_TAB_SCALE) 12.sp * (MAX_TAB_SCALE / scale) else 12.sp
    Text(text, fontSize = size, maxLines = 1, softWrap = false, overflow = TextOverflow.Ellipsis)
}

private const val MAX_TAB_SCALE = 1.3f

@Composable
private fun Tab.icon(): Painter = when (this) {
    // The Flexarr mark stands in for a house.
    Tab.Home -> painterResource(R.drawable.ic_brand_mark)
    Tab.Libraries -> rememberVectorPainter(Icons.Filled.GridView)
    Tab.Downloads -> rememberVectorPainter(Icons.Filled.DownloadForOffline)
    Tab.Search -> rememberVectorPainter(Icons.Filled.Search)
    Tab.Settings -> rememberVectorPainter(Icons.Filled.Settings)
}
