package io.github.flexarr.ui.tv

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.Logout
import androidx.compose.material.icons.filled.Dns
import androidx.compose.material.icons.filled.Menu
import androidx.compose.material.icons.filled.Sync
import androidx.compose.material.icons.filled.VideoLibrary
import androidx.compose.material.icons.outlined.PlayCircle
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.window.Dialog
import androidx.tv.material3.Text
import io.github.flexarr.AppModel
import io.github.flexarr.BuildConfig
import io.github.flexarr.core.FlexarrClient
import io.github.flexarr.core.Library
import io.github.flexarr.core.TraktStatus
import io.github.flexarr.core.traktStatus
import io.github.flexarr.ui.components.Load
import io.github.flexarr.ui.components.Loader
import io.github.flexarr.ui.components.rememberLoader
import io.github.flexarr.ui.library.LibraryMenuEntry
import io.github.flexarr.ui.theme.Flexarr

/** Settings' pages, opened from the first and from each other; Back goes to the one before. */
internal enum class TvSettingsPageId { Main, Playback, Libraries, Sidebar, Trakt, Server, Diagnostics }

/**
 * Settings, as the Apple TV's SettingsView: a short list of pages, each row saying how it's set now, the details a
 * press away, and Sign out on its own at the end. The pages are a stack of their own inside the Settings tab, so Back
 * goes up a page, and from the first brings in the sidebar.
 */
@Composable
fun TvSettingsScreen(model: AppModel, onSwitchProfile: () -> Unit) {
    val stack = remember { mutableStateListOf(TvSettingsPageId.Main) }
    // The page Back just left, so focus goes back to the row that opened it.
    var returningFrom by remember { mutableStateOf<TvSettingsPageId?>(null) }
    // Here rather than on a page, so the Sidebar row is there at once when Back comes to it.
    val libraries = rememberLoader(model.credentials, model.librariesVersion) { model.client?.libraries().orEmpty() }
    // Null until known, and from servers without Trakt (before 0.57) or on kids profiles: then there's no Trakt row.
    // Asked again on coming back to the first page, so the row says how it's set now.
    var trakt by remember { mutableStateOf<TraktStatus?>(null) }
    val onMain = stack.last() == TvSettingsPageId.Main
    LaunchedEffect(model.credentials, model.currentProfile?.id, onMain) {
        if (onMain) trakt = runCatching { model.client?.traktStatus() }.getOrNull()
    }

    fun open(page: TvSettingsPageId) {
        returningFrom = null
        stack.add(page)
    }

    BackHandler(enabled = stack.size > 1) { returningFrom = stack.removeAt(stack.lastIndex) }

    when (stack.last()) {
        TvSettingsPageId.Main -> MainPage(model, libraries, trakt?.takeIf { it.available }, returningFrom, onSwitchProfile, ::open)
        TvSettingsPageId.Playback -> TvPlaybackSettings(model)
        TvSettingsPageId.Libraries -> TvLibrarySettings()
        TvSettingsPageId.Sidebar -> TvSidebarSettings(model, libraries)
        TvSettingsPageId.Trakt -> TvTraktSettings(model)
        TvSettingsPageId.Server -> TvServerSettings(model, libraries, returningFrom, onDiagnostics = { open(TvSettingsPageId.Diagnostics) })
        TvSettingsPageId.Diagnostics -> TvDiagnosticsSettings(model)
    }
}

/** The places in the sidebar between Home and Search, in the profile's order: its libraries and Live TV, not Podcasts. */
internal fun sidebarEntries(model: AppModel, libraries: Loader<List<Library>>): List<LibraryMenuEntry> =
    (libraries.state as? Load.Loaded)?.value?.let { list -> LibraryMenuEntry.ordered(model, list).filter { it !is LibraryMenuEntry.Podcasts } }.orEmpty()

@Composable
private fun MainPage(
    model: AppModel,
    libraries: Loader<List<Library>>,
    trakt: TraktStatus?,
    returningFrom: TvSettingsPageId?,
    onSwitchProfile: () -> Unit,
    open: (TvSettingsPageId) -> Unit,
) {
    val context = LocalContext.current
    var confirmSignOut by remember { mutableStateOf(false) }
    val profile = model.currentProfile
    // A guest stays on its invite's profile.
    val switches = profile != null && model.profiles.size > 1 && !model.isGuest
    val owner = model.credentials?.guest?.ownerName?.ifEmpty { null }
    // Only from servers that keep an order with the profile (0.54 on): it's kept there, not on this TV.
    val sidebarFirst = if (profile?.libraryOrder != null) sidebarEntries(model, libraries).firstOrNull() else null
    // Coming back, the row that opened the page; otherwise the first row.
    val focusOn = returningFrom ?: if (switches) null else TvSettingsPageId.Playback
    val focus = remember { FocusRequester() }
    TakeFocus(focus, key = focusOn, ready = (focusOn != TvSettingsPageId.Sidebar || sidebarFirst != null) && (focusOn != TvSettingsPageId.Trakt || trakt != null))

    fun Modifier.focusFor(page: TvSettingsPageId?) = if (page == focusOn) focusRequester(focus) else this

    TvSettingsPage("Settings", summary = "Flexarr for Android TV ${BuildConfig.VERSION_NAME}") {
        if (profile != null) {
            item { TvSettingsGroupLabel("Watching as") }
            item {
                if (switches) {
                    TvSettingsRow(
                        profile.name, onClick = onSwitchProfile, modifier = Modifier.focusFor(null), profile = model to profile, value = "Switch",
                        help = "Switch to another profile. Each has its own Continue Watching, watch history and playback settings.",
                    )
                } else {
                    TvSettingsFact("Profile", profile.name, profile = model to profile)
                }
            }
        }
        item {
            TvSettingsRow(
                "Playback", onClick = { open(TvSettingsPageId.Playback) },
                modifier = Modifier.padding(top = if (profile == null) 0.dp else 18.dp).focusFor(TvSettingsPageId.Playback),
                icon = Icons.Outlined.PlayCircle, accessory = TvSettingAccessory.Chevron,
                help = "Auto-play, skipping intros and credits, subtitle size, and how far the remote skips.",
            )
        }
        item {
            TvSettingsRow(
                "Libraries", onClick = { open(TvSettingsPageId.Libraries) }, modifier = Modifier.focusFor(TvSettingsPageId.Libraries),
                icon = Icons.Filled.VideoLibrary, value = "Open on ${TvLibraryOpening.saved(context).label}", accessory = TvSettingAccessory.Chevron,
                help = "Whether a library opens on its highlights (Browse), on every title, A to Z, or on its genres.",
            )
        }
        if (sidebarFirst != null) {
            item {
                TvSettingsRow(
                    "Sidebar", onClick = { open(TvSettingsPageId.Sidebar) }, modifier = Modifier.focusFor(TvSettingsPageId.Sidebar),
                    icon = Icons.Filled.Menu, value = "${sidebarFirst.title} first", accessory = TvSettingAccessory.Chevron,
                    help = "The order of your libraries and Live TV in the sidebar, between Home and Discover.",
                )
            }
        }
        if (trakt != null) {
            item {
                TvSettingsRow(
                    "Trakt", onClick = { open(TvSettingsPageId.Trakt) }, modifier = Modifier.focusFor(TvSettingsPageId.Trakt),
                    icon = Icons.Filled.Sync, value = trakt.settingsValue, accessory = TvSettingAccessory.Chevron,
                    help = "Keep what this profile watches, and its watchlist, in step with Trakt.",
                )
            }
        }
        item {
            // With an address for away from home, which of the two it's using; otherwise the server's name.
            val connection = if (model.isGuest) {
                "By invite"
            } else if (model.credentials?.remoteAddress != null) {
                if (model.client?.addresses?.isAway == true) "Away" else "At home"
            } else {
                model.credentials?.address?.let { FlexarrClient.normalizeAddress(it)?.host }
            }
            TvSettingsRow(
                "Server", onClick = { open(TvSettingsPageId.Server) }, modifier = Modifier.focusFor(TvSettingsPageId.Server),
                icon = Icons.Filled.Dns, value = connection, accessory = TvSettingAccessory.Chevron,
                help = "The server this TV uses: its address, how it’s reached and what it’s running.",
            )
        }
        item {
            if (model.isGuest) {
                TvSettingsRow(
                    "Leave", onClick = { confirmSignOut = true }, modifier = Modifier.padding(top = 18.dp),
                    icon = Icons.AutoMirrored.Filled.Logout, destructive = true,
                    help = "Stop watching ${owner?.let { "$it’s" } ?: "this"} Flexarr on this TV. Coming back takes a new invite.",
                )
            } else {
                TvSettingsRow(
                    "Sign out", onClick = { confirmSignOut = true }, modifier = Modifier.padding(top = 18.dp),
                    icon = Icons.AutoMirrored.Filled.Logout, destructive = true,
                    help = "Disconnect this TV from the server. You’ll need to connect it again to watch.",
                )
            }
        }
    }

    if (confirmSignOut) {
        val cancel = remember { FocusRequester() }
        Dialog(onDismissRequest = { confirmSignOut = false }) {
            Column(Modifier.width(400.dp).background(Flexarr.seat, RoundedCornerShape(14.dp)).padding(24.dp), verticalArrangement = Arrangement.spacedBy(14.dp)) {
                val guest = model.isGuest
                Text(
                    if (guest) owner?.let { "Leave $it’s Flexarr?" } ?: "Leave this Flexarr?" else "Sign out of this server?",
                    fontSize = 18.sp,
                    fontWeight = FontWeight.Bold,
                )
                Text(if (guest) "Coming back takes a new invite." else "This TV will need to connect again to use the server.", fontSize = 14.sp, color = Flexarr.usher)
                Row(Modifier.fillMaxWidth().height(44.dp), horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                    TvActionButton(if (guest) "Leave" else "Sign out", onClick = {
                        confirmSignOut = false
                        model.signOut()
                    }, prominent = true, ambient = Flexarr.danger)
                    TvActionButton("Cancel", onClick = { confirmSignOut = false }, modifier = Modifier.focusRequester(cancel))
                }
            }
        }
        TakeFocus(cancel)
    }
}
