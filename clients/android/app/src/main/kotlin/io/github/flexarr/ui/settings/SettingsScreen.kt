package io.github.flexarr.ui.settings

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.KeyboardArrowRight
import androidx.compose.material.icons.automirrored.filled.Logout
import androidx.compose.material.icons.automirrored.filled.OpenInNew
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.Devices
import androidx.compose.material.icons.filled.Dns
import androidx.compose.material.icons.filled.DownloadForOffline
import androidx.compose.material3.Switch
import androidx.compose.material3.SwitchDefaults
import androidx.compose.foundation.selection.toggleable
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.selected
import androidx.compose.ui.semantics.semantics
import androidx.compose.foundation.gestures.detectDragGestures
import androidx.compose.material.icons.filled.DragHandle
import androidx.compose.material.icons.filled.MedicalServices
import androidx.compose.material.icons.filled.NotificationsActive
import androidx.compose.material.icons.filled.Star
import androidx.compose.runtime.key
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.semantics.CustomAccessibilityAction
import androidx.compose.ui.semantics.customActions
import androidx.compose.ui.zIndex
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LifecycleEventEffect
import io.github.flexarr.ReviewPrompt
import io.github.flexarr.ui.library.LibraryMenuEntry
import androidx.compose.material.icons.filled.Mic
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.PrivacyTip
import androidx.compose.material.icons.filled.Notifications
import androidx.compose.material.icons.filled.SwapVert
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalUriHandler
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.github.flexarr.AppModel
import io.github.flexarr.BuildConfig
import io.github.flexarr.core.DownloadQuality
import io.github.flexarr.core.FlexarrClient
import io.github.flexarr.core.Format
import io.github.flexarr.core.bytes
import io.github.flexarr.core.FlexarrException
import io.github.flexarr.core.libraryOrder
import io.github.flexarr.core.ServerVersion
import io.github.flexarr.logging.ErrorReports
import io.github.flexarr.ui.components.Edge
import io.github.flexarr.ui.components.Load
import io.github.flexarr.ui.components.Loaded
import io.github.flexarr.ui.components.LocalNavigator
import io.github.flexarr.ui.components.PageTitle
import io.github.flexarr.ui.components.pressable
import io.github.flexarr.ui.components.rememberLoader
import io.github.flexarr.ui.profiles.ProfileAvatar
import io.github.flexarr.ui.profiles.ProfilePicker
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.RoomBackground
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import java.time.LocalDateTime
import java.time.OffsetDateTime
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.time.format.FormatStyle

/**
 * Settings as a short list of pages, the way the system's own Settings works: each row says where it's set now, and
 * the details live a tap away.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun SettingsScreen(
    model: AppModel,
    onServer: () -> Unit,
    onLibraryOrder: () -> Unit,
    onPlayback: () -> Unit,
    onDownloads: () -> Unit,
    onPodcasts: () -> Unit,
    onNotifications: () -> Unit,
    onWaitingDevices: () -> Unit,
) {
    var confirmSignOut by remember { mutableStateOf(false) }
    var choosingProfile by remember { mutableStateOf(false) }
    val context = LocalContext.current
    val libraries = rememberLoader(model.credentials, model.librariesVersion) { model.client?.libraries().orEmpty() }
    val notifications = model.notifications
    // Android lets Flexarr post notifications, so if they're off it's this server that isn't sending them.
    var notificationsAllowed by remember { mutableStateOf(notifications.isAllowed) }
    LifecycleEventEffect(Lifecycle.Event.ON_RESUME) { notificationsAllowed = notifications.isAllowed }
    var turningOnNotifications by remember { mutableStateOf(false) }
    var notificationProblem by remember { mutableStateOf<String?>(null) }
    val scope = rememberCoroutineScope()

    fun turnOnNotifications() {
        val credentials = model.credentials ?: return
        turningOnNotifications = true
        scope.launch {
            try {
                notifications.turnOn(credentials)
            } catch (e: Exception) {
                notificationProblem = e.message ?: "Something went wrong."
            } finally {
                turningOnNotifications = false
            }
        }
    }

    RoomBackground(ambient = null) {
        LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 32.dp), verticalArrangement = Arrangement.spacedBy(28.dp)) {
            item { PageTitle("Settings") }
            model.currentProfile?.let { current ->
                item {
                    Group("Watching as") {
                        Row(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 12.dp), horizontalArrangement = Arrangement.spacedBy(14.dp), verticalAlignment = Alignment.CenterVertically) {
                            ProfileAvatar(model, current, size = 44)
                            Text(current.name, fontSize = 17.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen, modifier = Modifier.weight(1f))
                            if (model.profiles.size > 1) {
                                Text("Switch", fontSize = 15.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.silver, modifier = Modifier.pressable({ choosingProfile = true }).padding(6.dp))
                            }
                        }
                    }
                }
            }
            if (notifications.isAvailable && notificationsAllowed && !notifications.isOn && !notifications.turnedOff) {
                item {
                    Group(footer = "This phone isn’t getting notifications from this server, so you won’t hear when something new is ready.") {
                        Row(
                            Modifier.fillMaxWidth().pressable(::turnOnNotifications, enabled = !turningOnNotifications).padding(horizontal = 16.dp, vertical = 14.dp),
                            horizontalArrangement = Arrangement.spacedBy(14.dp),
                            verticalAlignment = Alignment.CenterVertically,
                        ) {
                            Icon(Icons.Filled.NotificationsActive, contentDescription = null, tint = Flexarr.silver)
                            Text("Turn on notifications", fontSize = 17.sp, color = Flexarr.silver)
                        }
                    }
                }
            }
            item {
                Group {
                    SettingsLink("Playback", Icons.Filled.PlayArrow, value = null, onClick = onPlayback)
                    Divider()
                    val downloads = model.downloads
                    SettingsLink("Downloads", Icons.Filled.DownloadForOffline, value = if (downloads.bytesUsed > 0) Format.bytes(downloads.bytesUsed) else downloads.quality.shortLabel, onClick = onDownloads)
                    if (model.podcastStatus?.available == true) {
                        Divider()
                        SettingsLink("Podcasts", Icons.Filled.Mic, value = null, onClick = onPodcasts)
                    }
                    // Only from servers that can save an order (0.54 on), and with more than one place to put in order.
                    val order = model.currentProfile?.libraryOrder
                    val places = (libraries.state as? Load.Loaded)?.value?.let { LibraryMenuEntry.ordered(model, it).size } ?: 0
                    if (order != null && places > 1) {
                        Divider()
                        SettingsLink("Library order", Icons.Filled.SwapVert, value = if (order.isEmpty()) "By name" else "Your own", onClick = onLibraryOrder)
                    }
                    Divider()
                    SettingsLink("Notifications", Icons.Filled.Notifications, value = if (model.notifications.isOn) "On" else "Off", onClick = onNotifications)
                }
            }
            // The main profile lets in TVs, phones and tablets showing a code.
            if (model.isMainProfile) {
                item {
                    Group {
                        val waiting = model.pairing.waiting.size
                        SettingsLink("Devices waiting to connect", Icons.Filled.Devices, value = if (waiting > 0) "$waiting" else null, onClick = onWaitingDevices)
                    }
                }
            }
            item {
                Group {
                    // With an address for away from home, which of the two it's using; otherwise the server's name.
                    val host = if (model.credentials?.remoteAddress != null) {
                        if (model.client?.addresses?.isAway == true) "Away" else "At home"
                    } else {
                        model.credentials?.address?.let { FlexarrClient.normalizeAddress(it)?.host }
                    }
                    SettingsLink("Server", Icons.Filled.Dns, value = host, onClick = onServer)
                }
            }
            item {
                Group {
                    ExternalLink("Rate Flexarr", Icons.Filled.Star) { ReviewPrompt.openStoreListing(context) }
                }
            }
            item {
                Group {
                    val uri = LocalUriHandler.current
                    ExternalLink("Privacy policy", Icons.Filled.PrivacyTip) { uri.openUri(PRIVACY_POLICY_URL) }
                }
            }
            item {
                Group(footer = "Flexarr for Android ${BuildConfig.VERSION_NAME}") {
                    Row(
                        Modifier.fillMaxWidth().pressable({ confirmSignOut = true }).padding(horizontal = 16.dp, vertical = 14.dp),
                        horizontalArrangement = Arrangement.spacedBy(14.dp),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        Icon(Icons.AutoMirrored.Filled.Logout, contentDescription = null, tint = Flexarr.danger)
                        Text("Sign out", fontSize = 17.sp, color = Flexarr.danger)
                    }
                }
            }
        }
    }

    if (confirmSignOut) {
        AlertDialog(
            onDismissRequest = { confirmSignOut = false },
            title = { Text("Sign out of this server?") },
            text = { Text("This device will need to connect again to use the server, and its downloads will be deleted.") },
            confirmButton = { TextButton(onClick = model::signOut) { Text("Sign out", color = Flexarr.danger) } },
            dismissButton = { TextButton(onClick = { confirmSignOut = false }) { Text("Cancel", color = Flexarr.screen) } },
            containerColor = Flexarr.seat,
        )
    }
    if (choosingProfile) {
        ModalBottomSheet(onDismissRequest = { choosingProfile = false }, sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true), containerColor = Flexarr.room) {
            ProfilePicker(model, marksCurrent = true, onChosen = { choosingProfile = false })
        }
    }
    notificationProblem?.let { problem ->
        AlertDialog(
            onDismissRequest = { notificationProblem = null },
            title = { Text("Couldn’t turn on notifications") },
            text = { Text(problem) },
            confirmButton = { TextButton(onClick = { notificationProblem = null }) { Text("OK", color = Flexarr.screen) } },
            containerColor = Flexarr.seat,
        )
    }
}

/** What Flexarr's apps collect and why, on flexarr.io. */
private const val PRIVACY_POLICY_URL = "https://flexarr.io/privacy"

/** A row that leaves the app for a web page, which the browser opens: an icon in a tile, its name, and an arrow out. */
@Composable
private fun ExternalLink(title: String, icon: ImageVector, onClick: () -> Unit) {
    Row(
        Modifier.fillMaxWidth().pressable(onClick).padding(horizontal = 16.dp, vertical = 10.dp),
        horizontalArrangement = Arrangement.spacedBy(14.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Box(Modifier.size(29.dp).clip(RoundedCornerShape(7.dp)).background(Flexarr.aisle), contentAlignment = Alignment.Center) {
            Icon(icon, contentDescription = null, tint = Flexarr.screen, modifier = Modifier.size(17.dp))
        }
        Text(title, fontSize = 17.sp, color = Flexarr.screen, modifier = Modifier.weight(1f))
        Icon(Icons.AutoMirrored.Filled.OpenInNew, contentDescription = null, tint = Flexarr.usher, modifier = Modifier.size(18.dp))
    }
}

/** "Automatic", "Original", "Small": the size downloads come at, as Settings' row says it. */
private val DownloadQuality.shortLabel: String
    get() = when (this) {
        DownloadQuality.Medium -> "Automatic"
        DownloadQuality.Small -> "Small"
        else -> label
    }

/** The size films and episodes come at, what the phone fetches by itself and when, and the space it all takes. */
@Composable
fun DownloadSettingsScreen(model: AppModel) {
    val navigator = LocalNavigator.current
    val downloads = model.downloads
    var confirmDeleteAll by remember { mutableStateOf(false) }

    RoomBackground(ambient = null) {
        LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 32.dp), verticalArrangement = Arrangement.spacedBy(28.dp)) {
            item { PageTitle("Downloads", onBack = { navigator.back() }) }
            item {
                Group(
                    header = "Films and episodes",
                    footer = "The size Download starts with; a title’s page offers the others too. Original keeps the full picture and sound but takes the most space. Podcasts always come as their files are.",
                ) {
                    listOf(DownloadQuality.Medium to "Automatic (720p)", DownloadQuality.Original to "Original", DownloadQuality.Small to "Small (480p)").forEachIndexed { index, (quality, label) ->
                        if (index > 0) Divider()
                        Choice(label, downloads.quality == quality) { downloads.chooseQuality(quality) }
                    }
                }
            }
            item {
                Group(
                    header = "Automatic",
                    footer = if (downloads.automaticOnWiFiOnly) {
                        "Downloads this phone starts by itself, like the next episode or new podcast episodes, wait for Wi-Fi and never come while Data Saver is on. Ones you start yourself use mobile data too."
                    } else {
                        "Downloads this phone starts by itself, like the next episode or new podcast episodes, use mobile data too, but never while Data Saver is on."
                    },
                ) {
                    Toggle("Download the next episode", downloads.downloadsNext, downloads::changeDownloadsNext)
                    Divider()
                    Toggle("Automatic downloads on Wi-Fi only", downloads.automaticOnWiFiOnly, downloads::changeAutomaticOnWiFiOnly)
                }
            }
            item {
                Group(header = "Storage", footer = "Episodes are deleted a day after you finish them, and podcast episodes once they’re played.") {
                    Fact("Space used", if (downloads.bytesUsed == 0L) "None" else Format.bytes(downloads.bytesUsed))
                    if (downloads.downloads.isNotEmpty()) {
                        Divider()
                        Text(
                            "Delete all downloads",
                            fontSize = 17.sp,
                            color = Flexarr.danger,
                            modifier = Modifier.fillMaxWidth().pressable({ confirmDeleteAll = true }).padding(horizontal = 16.dp, vertical = 14.dp),
                        )
                    }
                }
            }
        }
    }

    if (confirmDeleteAll) {
        AlertDialog(
            onDismissRequest = { confirmDeleteAll = false },
            title = { Text("Delete all downloads?") },
            text = { Text("Everything downloaded to this phone goes. What you watched offline is sent to the server first.") },
            confirmButton = { TextButton(onClick = { confirmDeleteAll = false; downloads.deleteAll() }) { Text("Delete all", color = Flexarr.danger) } },
            dismissButton = { TextButton(onClick = { confirmDeleteAll = false }) { Text("Cancel", color = Flexarr.screen) } },
            containerColor = Flexarr.seat,
        )
    }
}

/** One of a few choices, ticked when it's the one. */
@Composable
internal fun Choice(label: String, selected: Boolean, onClick: () -> Unit) {
    Row(
        Modifier.fillMaxWidth().pressable(onClick).padding(horizontal = 16.dp, vertical = 14.dp).semantics { this.selected = selected },
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(label, fontSize = 17.sp, color = Flexarr.screen, modifier = Modifier.weight(1f))
        if (selected) Icon(Icons.Filled.Check, contentDescription = null, tint = Flexarr.silver)
    }
}

@Composable
internal fun Toggle(label: String, on: Boolean, onChange: (Boolean) -> Unit, detail: String? = null, enabled: Boolean = true) {
    Row(
        Modifier.fillMaxWidth().toggleable(on, enabled = enabled, onValueChange = onChange, role = Role.Switch).padding(horizontal = 16.dp, vertical = 8.dp),
        horizontalArrangement = Arrangement.spacedBy(12.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Column(Modifier.weight(1f)) {
            Text(label, fontSize = 17.sp, color = Flexarr.screen)
            detail?.let { Text(it, fontSize = 13.sp, color = Flexarr.usher) }
        }
        Switch(
            checked = on,
            onCheckedChange = null,
            colors = SwitchDefaults.colors(checkedTrackColor = Flexarr.downloaded, checkedThumbColor = Flexarr.screen, uncheckedTrackColor = Flexarr.aisle, uncheckedThumbColor = Flexarr.usher, uncheckedBorderColor = Flexarr.aisle),
        )
    }
}

/** "192.168.1.20:8383" for "http://192.168.1.20:8383/". */
internal fun shortAddress(address: String?): String = address.orEmpty().replace(Regex("^https?://"), "").trimEnd('/')

/** Address, server version and what it runs on. */
@Composable
fun ServerSettingsScreen(model: AppModel, onDiagnostics: () -> Unit) {
    val client = model.client ?: return
    val navigator = LocalNavigator.current
    val status = rememberLoader(model.credentials) { client.systemStatus() }
    val libraries = rememberLoader(model.credentials) { client.libraries() }

    RoomBackground(ambient = null) {
        LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 32.dp), verticalArrangement = Arrangement.spacedBy(28.dp)) {
            item { PageTitle("Server", onBack = { navigator.back() }) }
            item {
                val remote = model.credentials?.remoteAddress
                Group(
                    footer = if (remote == null) {
                        "To use Flexarr away from home, turn on remote access with Tailscale in Settings › Access on the web; this phone picks the address up by itself."
                    } else {
                        "Away from home, this phone reaches the server over Tailscale, which needs to be connected on it."
                    },
                ) {
                    Fact("Address", shortAddress(model.credentials?.address))
                    if (remote != null) {
                        Divider()
                        Fact("Away from home", shortAddress(remote))
                        Divider()
                        Fact("Connected", if (client.addresses.isAway) "Away, over Tailscale" else "At home")
                    }
                }
            }
            item {
                Group {
                    when (val state = status.state) {
                        is Load.Loaded -> {
                            Fact("Version", ServerVersion.short(state.value.version))
                            Divider()
                            Fact("Running on", state.value.osDescription)
                            state.value.startTime?.let(::upSince)?.let {
                                Divider()
                                Fact("Up since", it)
                            }
                        }
                        is Load.Failed -> Fact("Status", state.message)
                        Load.Loading -> Fact("Status", "Checking")
                    }
                    Divider()
                    Fact("Libraries", (libraries.state as? Load.Loaded)?.value?.joinToString(", ") { it.name }.orEmpty())
                }
            }
            item {
                // As the server says now, or as this phone last heard while it can't be asked.
                val reportsOn = (status.state as? Load.Loaded)?.value?.let { it.errorReports?.enabled == true } ?: ErrorReports.isOn
                Group(footer = ErrorReports.PRIVACY_NOTE) {
                    Fact("Error reports", if (reportsOn) "On" else "Off")
                }
            }
            item {
                Group(footer = "Send this phone’s recent log to your server, to look into a problem.") {
                    SettingsLink("Send diagnostics", Icons.Filled.MedicalServices, value = null, onClick = onDiagnostics)
                }
            }
        }
    }
}

/**
 * The order of the current profile's places in the Libraries tab (its libraries, Podcasts and Live TV), dragged by the
 * handles, as on iPhone. Every move saves straight away, so the order follows the profile to its other devices.
 */
@Composable
fun LibraryOrderSettingsScreen(model: AppModel) {
    val client = model.client ?: return
    val navigator = LocalNavigator.current
    val loader = rememberLoader(model.credentials) { client.libraries() }
    // Moved here but maybe not saved yet; null shows the profile's order.
    var moved by remember { mutableStateOf<List<LibraryMenuEntry>?>(null) }
    var problem by remember { mutableStateOf<String?>(null) }
    // One save at a time, in the order the moves were made, so the last move is the one that stays.
    val saving = remember { Mutex() }

    fun save(entries: List<LibraryMenuEntry>?) {
        moved = entries
        problem = null
        model.scope.launch {
            saving.withLock {
                try {
                    val saved = model.currentProfile?.libraryOrder.orEmpty()
                    model.setLibraryOrder(if (entries == null) emptyList() else libraryOrder(saved, entries.map { it.id }))
                    if (entries == null) loader.retry()
                } catch (e: FlexarrException) {
                    problem = "Couldn’t save the order. ${e.message}"
                    moved = null
                    loader.retry()
                }
            }
        }
    }

    RoomBackground(ambient = null) {
        Column {
            PageTitle("Library order", onBack = { navigator.back() })
            Loaded(loader) { libraries ->
                val entries = moved ?: LibraryMenuEntry.ordered(model, libraries)
                LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(top = 8.dp, bottom = 32.dp), verticalArrangement = Arrangement.spacedBy(28.dp)) {
                    item {
                        Group(footer = problem ?: "${model.currentProfile?.name ?: "This profile"}’s own. The web, the Apple TV and iPhone list your libraries in this order too.") {
                            ReorderableEntries(entries, onMove = ::save)
                        }
                    }
                    if (model.currentProfile?.libraryOrder?.isNotEmpty() == true) {
                        item {
                            Group {
                                Text(
                                    "Sort by name",
                                    fontSize = 17.sp,
                                    color = Flexarr.screen,
                                    modifier = Modifier.fillMaxWidth().pressable({ save(null) }).padding(horizontal = 16.dp, vertical = 14.dp),
                                )
                            }
                        }
                    }
                }
            }
        }
    }
}

/**
 * The places, each dragged up or down by its handle and dropped where it goes ([onMove] then has the new order).
 * TalkBack offers Move up and Move down on each instead.
 */
@Composable
private fun ReorderableEntries(entries: List<LibraryMenuEntry>, onMove: (List<LibraryMenuEntry>) -> Unit) {
    var order by remember(entries) { mutableStateOf(entries) }
    var dragging by remember { mutableStateOf<String?>(null) }
    var offset by remember { mutableFloatStateOf(0f) }
    var rowHeight by remember { mutableIntStateOf(0) }

    fun step(id: String, by: Int) {
        val index = order.indexOfFirst { it.id == id }
        val to = index + by
        if (index < 0 || to !in order.indices) return
        order = order.moved(index, to)
    }

    Column {
        order.forEachIndexed { index, entry ->
            key(entry.id) {
                if (index > 0) Divider()
                val lifted = dragging == entry.id
                Row(
                    Modifier.fillMaxWidth()
                        .onSizeChanged { rowHeight = it.height }
                        .zIndex(if (lifted) 1f else 0f)
                        .graphicsLayer { translationY = if (lifted) offset else 0f }
                        .background(if (lifted) Flexarr.aisle else Color.Transparent)
                        .padding(start = 16.dp, end = 4.dp)
                        .semantics(mergeDescendants = true) {
                            customActions = listOfNotNull(
                                if (index > 0) CustomAccessibilityAction("Move up") { step(entry.id, -1); onMove(order); true } else null,
                                if (index < order.lastIndex) CustomAccessibilityAction("Move down") { step(entry.id, 1); onMove(order); true } else null,
                            )
                        },
                    horizontalArrangement = Arrangement.spacedBy(14.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Icon(entry.icon, contentDescription = null, tint = Flexarr.silver, modifier = Modifier.size(22.dp))
                    Text(entry.title, fontSize = 17.sp, color = Flexarr.screen, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f))
                    Box(
                        Modifier.size(48.dp).pointerInput(entry.id) {
                            detectDragGestures(
                                onDragStart = {
                                    dragging = entry.id
                                    offset = 0f
                                },
                                onDrag = { change, amount ->
                                    change.consume()
                                    offset += amount.y
                                    // Past half the next row: it changes places, and the lifted row carries on from there.
                                    if (offset > rowHeight / 2f && order.last().id != entry.id) {
                                        step(entry.id, 1)
                                        offset -= rowHeight
                                    } else if (offset < -rowHeight / 2f && order.first().id != entry.id) {
                                        step(entry.id, -1)
                                        offset += rowHeight
                                    }
                                },
                                onDragEnd = {
                                    dragging = null
                                    offset = 0f
                                    if (order != entries) onMove(order)
                                },
                                onDragCancel = {
                                    dragging = null
                                    offset = 0f
                                    order = entries
                                },
                            )
                        },
                        contentAlignment = Alignment.Center,
                    ) {
                        Icon(Icons.Filled.DragHandle, contentDescription = null, tint = Flexarr.usher)
                    }
                }
            }
        }
    }
}

private fun <T> List<T>.moved(from: Int, to: Int): List<T> = toMutableList().apply { add(to, removeAt(from)) }

/** A group of rows on the seat, with an optional heading above and note below, as iPhone's grouped lists. */
@Composable
internal fun Group(header: String? = null, footer: String? = null, content: @Composable () -> Unit) {
    Column(Modifier.padding(horizontal = Edge), verticalArrangement = Arrangement.spacedBy(8.dp)) {
        header?.let { Text(it.uppercase(), fontSize = 13.sp, color = Flexarr.usher, modifier = Modifier.padding(horizontal = 16.dp)) }
        Column(Modifier.fillMaxWidth().clip(RoundedCornerShape(12.dp)).background(Flexarr.seat)) { content() }
        footer?.let { Text(it, fontSize = 13.sp, color = Flexarr.usher, modifier = Modifier.padding(horizontal = 16.dp)) }
    }
}

@Composable
internal fun Divider() = HorizontalDivider(Modifier.padding(start = 16.dp), color = Flexarr.aisle.copy(alpha = 0.6f))

/** A row on the Settings list: an icon in a tile, the page's name and, on the right, how it's set now. */
@Composable
internal fun SettingsLink(title: String, icon: ImageVector, value: String?, onClick: () -> Unit) {
    Row(
        Modifier.fillMaxWidth().pressable(onClick).padding(horizontal = 16.dp, vertical = 10.dp),
        horizontalArrangement = Arrangement.spacedBy(14.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Box(Modifier.size(29.dp).clip(RoundedCornerShape(7.dp)).background(Flexarr.aisle), contentAlignment = Alignment.Center) {
            Icon(icon, contentDescription = null, tint = Flexarr.screen, modifier = Modifier.size(17.dp))
        }
        Text(title, fontSize = 17.sp, color = Flexarr.screen, modifier = Modifier.weight(1f))
        value?.let { Text(it, fontSize = 17.sp, color = Flexarr.usher, maxLines = 1, overflow = TextOverflow.Ellipsis) }
        Icon(Icons.AutoMirrored.Filled.KeyboardArrowRight, contentDescription = null, tint = Flexarr.usher)
    }
}

@Composable
internal fun Fact(label: String, value: String) {
    Row(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 12.dp), horizontalArrangement = Arrangement.spacedBy(16.dp)) {
        Text(label, fontSize = 17.sp, color = Flexarr.screen)
        Text(value, fontSize = 17.sp, color = Flexarr.usher, textAlign = TextAlign.End, maxLines = 2, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f))
    }
}

/** "29 Oct 2026, 09:14" from the server's start time (with or without its offset). */
internal fun upSince(text: String): String? {
    val time = runCatching { OffsetDateTime.parse(text).atZoneSameInstant(ZoneId.systemDefault()) }.getOrNull()
        ?: runCatching { LocalDateTime.parse(text).atZone(ZoneId.of("UTC")).withZoneSameInstant(ZoneId.systemDefault()) }.getOrNull()
        ?: return null
    return time.format(DateTimeFormatter.ofLocalizedDateTime(FormatStyle.MEDIUM, FormatStyle.SHORT))
}
