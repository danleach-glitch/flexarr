package io.github.flexarr.ui.tv

import androidx.activity.compose.BackHandler
import androidx.compose.animation.AnimatedVisibility
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
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.Home
import androidx.compose.material.icons.outlined.Settings
import androidx.compose.material3.SnackbarHost
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.ExperimentalComposeUiApi
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.FocusDirection
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.focus.focusRestorer
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

/** A screen of its own in the sidebar. */
private enum class TvTab(val route: String, val title: String, val icon: ImageVector) {
    Home("home", "Home", Icons.Outlined.Home),
    Settings("settings", "Settings", Icons.Outlined.Settings),
}

/**
 * The app, as the Apple TV's MainTabView: a sidebar (who's watching, Home, the libraries, then Search and Settings) over
 * the screens. It's out of the way while browsing; Back on a tab's first screen, or left from the screen's left edge,
 * brings it in, and Back on it leaves the app. The libraries, Search and Discover join it with their screens
 * (docs/android-tv.md).
 */
@OptIn(ExperimentalComposeUiApi::class)
@Composable
fun TvMainScreen(model: AppModel) {
    val client = model.client ?: return
    val context = LocalContext.current
    val nav = rememberNavController()
    val navigator = remember(nav) { Navigator(nav, model, context) }
    var tab by rememberSaveable { mutableStateOf(TvTab.Home) }
    var sidebarOpen by remember { mutableStateOf(false) }
    var choosingProfile by remember { mutableStateOf(false) }
    // Home's Play takes focus as the app opens, so it's ready to browse.
    var startFocus by remember { mutableStateOf(true) }
    val content = remember { FocusRequester() }
    val focusManager = LocalFocusManager.current
    val entry by nav.currentBackStackEntryAsState()
    val atTabRoot = entry?.destination?.route == tab.route

    // Signed in (or as someone else): commands for this device ("play on the living room TV") come to this sign-in.
    LaunchedEffect(model.credentials) { model.listenForCommands() }

    fun closeSidebar() {
        sidebarOpen = false
        runCatching { content.requestFocus() }
    }

    fun select(chosen: TvTab) {
        if (chosen != tab || !atTabRoot) {
            tab = chosen
            nav.navigate(chosen.route) {
                popUpTo(nav.graph.id) { inclusive = true }
                launchSingleTop = true
            }
        }
        closeSidebar()
    }

    // Back on a tab's first screen brings in the sidebar; on the sidebar, it leaves the app.
    BackHandler(enabled = !sidebarOpen && atTabRoot) { sidebarOpen = true }

    CompositionLocalProvider(LocalClient provides client, LocalNavigator provides navigator) {
        Box(Modifier.fillMaxSize()) {
            Box(
                Modifier.fillMaxSize().focusRequester(content).focusRestorer().focusGroup().onKeyEvent { event ->
                    // Left with nothing further left on the screen: the sidebar.
                    if (sidebarOpen || event.type != KeyEventType.KeyDown || event.key != Key.DirectionLeft) return@onKeyEvent false
                    if (!focusManager.moveFocus(FocusDirection.Left)) sidebarOpen = true
                    true
                },
            ) {
                NavHost(nav, startDestination = TvTab.Home.route) {
                    composable(TvTab.Home.route) { TvHomeScreen(model, takeFocus = startFocus, onFocusTaken = { startFocus = false }) }
                    composable(TvTab.Settings.route) { TvSettingsScreen(model, onSwitchProfile = { choosingProfile = true }) }
                    composable("item/{id}") { TvDetailScreen(model, it.arguments!!.getString("id")!!) }
                }
            }
            Sidebar(model, sidebarOpen, tab, onSelect = ::select, onProfile = {
                closeSidebar()
                choosingProfile = true
            }, onClose = ::closeSidebar, closesOnBack = !atTabRoot)
            SnackbarHost(navigator.notices, Modifier.align(Alignment.BottomCenter).padding(bottom = 30.dp))
        }
        if (choosingProfile) {
            TvProfilePicker(model, marksCurrent = true, onDone = {
                choosingProfile = false
                runCatching { content.requestFocus() }
            })
        }
    }
}

/**
 * The sidebar, over the left of the screen: who's watching (with several profiles), the tabs, and the logo and
 * version at its foot. Choosing a tab, Right or Back (on a screen opened from a tab) puts it away.
 */
@Composable
private fun Sidebar(model: AppModel, open: Boolean, selected: TvTab, onSelect: (TvTab) -> Unit, onProfile: () -> Unit, onClose: () -> Unit, closesOnBack: Boolean) {
    AnimatedVisibility(open, enter = fadeIn(), exit = fadeOut()) {
        Box(Modifier.fillMaxSize().background(Brush.horizontalGradient(0f to Color.Black.copy(alpha = 0.7f), 0.5f to Color.Transparent)))
    }
    AnimatedVisibility(open, enter = slideInHorizontally { -it } + fadeIn(), exit = slideOutHorizontally { -it } + fadeOut()) {
        val first = remember { FocusRequester() }
        LaunchedEffect(Unit) { runCatching { first.requestFocus() } }
        BackHandler(enabled = closesOnBack, onBack = onClose)
        Column(
            Modifier.fillMaxHeight().width(230.dp).padding(12.dp).background(Flexarr.seat.copy(alpha = 0.92f), RoundedCornerShape(16.dp)).padding(vertical = 18.dp, horizontal = 10.dp)
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
            TvTab.entries.forEach { tab ->
                if (tab == TvTab.Settings) {
                    // A rule between the libraries and the rest.
                    Box(Modifier.padding(horizontal = 12.dp, vertical = 8.dp).fillMaxWidth().height(1.dp).background(Flexarr.aisle))
                }
                SidebarRow(
                    tab.title,
                    selected = tab == selected,
                    onClick = { onSelect(tab) },
                    modifier = if (tab == selected) Modifier.focusRequester(first) else Modifier,
                    icon = { Icon(tab.icon, contentDescription = null, modifier = Modifier.size(20.dp)) },
                )
            }
            Spacer(Modifier.weight(1f))
            // The logo and the app's version, quietly at the foot.
            Row(Modifier.padding(horizontal = 12.dp), horizontalArrangement = Arrangement.spacedBy(6.dp), verticalAlignment = Alignment.CenterVertically) {
                BrandMark(13.dp)
                Text("flexarr", style = marquee(11, Flexarr.screen.copy(alpha = 0.8f)))
                Text(BuildConfig.VERSION_NAME, fontSize = 10.sp, fontWeight = FontWeight.Medium, color = Flexarr.usher.copy(alpha = 0.7f), modifier = Modifier.padding(start = 6.dp))
            }
        }
    }
}

@Composable
private fun SidebarRow(title: String, selected: Boolean, onClick: () -> Unit, modifier: Modifier = Modifier, icon: @Composable () -> Unit) {
    Surface(
        onClick = onClick,
        modifier = modifier.fillMaxWidth().height(40.dp),
        shape = ClickableSurfaceDefaults.shape(RoundedCornerShape(10.dp)),
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
