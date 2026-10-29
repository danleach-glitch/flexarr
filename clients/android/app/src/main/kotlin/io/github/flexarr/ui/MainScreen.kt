package io.github.flexarr.ui

import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.padding
import androidx.compose.runtime.LaunchedEffect
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
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
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
import io.github.flexarr.AppLink
import io.github.flexarr.core.podcastEpisode
import io.github.flexarr.ui.settings.DiagnosticsSettingsScreen
import io.github.flexarr.ui.settings.NotificationSettingsScreen
import io.github.flexarr.ui.settings.DownloadSettingsScreen
import io.github.flexarr.ui.settings.LibraryOrderSettingsScreen
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
    val client = model.client ?: return
    val nav = rememberNavController()
    val context = LocalContext.current
    val navigator = remember(nav, model) { Navigator(nav, model, context) }
    // The tab stays lit while a title opened from it is on top.
    var currentTab by rememberSaveable { mutableStateOf(Tab.Home) }

    // Whether this profile has podcasts (Libraries lists them) and how it listens to them.
    LaunchedEffect(model.credentials) { model.loadPodcastStatus() }
    // Whether the server has Live TV (Libraries lists it).
    LaunchedEffect(model.credentials) { model.loadLiveTvStatus() }
    // Signed in (or as someone else): commands for this device come to this sign-in.
    LaunchedEffect(model.credentials) { model.listenForCommands() }
    // Downloads follow the server and profile signed in.
    LaunchedEffect(model.credentials, model.currentProfile?.id) { model.downloads.attach(client, model.currentProfile?.id) }

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
        // A notification, shortcut or the widget asked to go somewhere.
        LaunchedEffect(model.pendingLink) {
            val link = model.pendingLink ?: return@LaunchedEffect
            model.pendingLink = null
            when (link) {
                is AppLink.Item -> navigator.openItem(link.id)
                is AppLink.Play -> {
                    val item = runCatching { client.item(link.id) }.getOrNull()
                    if (item != null) navigator.playBest(item.item) else navigator.openItem(link.id)
                }
                is AppLink.Podcast -> nav.navigate("podcast/${link.id}")
                is AppLink.PodcastEpisode -> runCatching { client.podcastEpisode(link.id) }.onSuccess {
                    model.podcasts.play(it)
                    model.showsNowPlaying = true
                }
                AppLink.Search -> select(Tab.Search)
                AppLink.Downloads -> select(Tab.Downloads)
                AppLink.Home -> select(Tab.Home)
            }
        }
        // With no connection to the server, the app opens on what's downloaded.
        LaunchedEffect(model.serverUnreachable) {
            if (model.serverUnreachable && model.downloads.downloads.isNotEmpty() && currentTab == Tab.Home) select(Tab.Downloads)
        }
        // A tablet has the tabs down the side, as iPad's sidebar-style tab bar keeps the width for the screens.
        val rail = isWide
        Scaffold(
            containerColor = Flexarr.room,
            bottomBar = {
              Column {
                // Music or a podcast carries on under every screen; the bar sits above the tabs and opens Now Playing.
                if (model.music.current != null) MusicNowPlayingBar(model) else NowPlayingBar(model)
                if (!rail) {
                    NavigationBar(containerColor = Flexarr.room.copy(alpha = 0.96f)) {
                        Tab.entries.forEach { tab ->
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
                    Tab.entries.forEach { tab ->
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
                        onDownloads = { nav.navigate("settings/downloads") },
                        onNotifications = { nav.navigate("settings/notifications") },
                    )
                }
                composable("settings/downloads") { DownloadSettingsScreen(model) }
                composable("settings/server") { ServerSettingsScreen(model, onDiagnostics = { nav.navigate("settings/diagnostics") }) }
                composable("settings/diagnostics") { DiagnosticsSettingsScreen(model) }
                composable("settings/notifications") { NotificationSettingsScreen(model) }
                composable("settings/library-order") { LibraryOrderSettingsScreen(model) }
                composable(
                    "library/{id}?sort={sort}",
                    arguments = listOf(navArgument("sort") { type = NavType.StringType; defaultValue = "" }),
                ) { entry ->
                    val sort = LibrarySort.entries.firstOrNull { it.raw == entry.arguments?.getString("sort") } ?: LibrarySort.Title
                    LibraryScreen(model, entry.arguments?.getString("id")!!, sort)
                }
                composable("livetv") { LiveTvScreen(model) }
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
