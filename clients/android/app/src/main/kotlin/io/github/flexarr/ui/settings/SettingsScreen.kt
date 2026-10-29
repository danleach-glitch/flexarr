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
import androidx.compose.material.icons.filled.Dns
import androidx.compose.material.icons.filled.KeyboardArrowDown
import androidx.compose.material.icons.filled.KeyboardArrowUp
import androidx.compose.material.icons.filled.SwapVert
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.ModalBottomSheet
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
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.github.flexarr.AppModel
import io.github.flexarr.BuildConfig
import io.github.flexarr.core.FlexarrClient
import io.github.flexarr.core.FlexarrException
import io.github.flexarr.core.Library
import io.github.flexarr.core.libraryOrder
import io.github.flexarr.core.ServerVersion
import io.github.flexarr.ui.components.Edge
import io.github.flexarr.ui.components.Load
import io.github.flexarr.ui.components.Loaded
import io.github.flexarr.ui.components.LocalNavigator
import io.github.flexarr.ui.components.PageTitle
import io.github.flexarr.ui.components.pressable
import io.github.flexarr.ui.components.rememberLoader
import io.github.flexarr.ui.library.icon
import io.github.flexarr.ui.library.inMenu
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
 * the details live a tap away. Playback, downloads and notifications join it as the app gains them.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun SettingsScreen(model: AppModel, onServer: () -> Unit, onLibraryOrder: () -> Unit) {
    var confirmSignOut by remember { mutableStateOf(false) }
    var choosingProfile by remember { mutableStateOf(false) }

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
            // Only from servers that can save an order (0.54 on).
            model.currentProfile?.libraryOrder?.let { order ->
                item {
                    Group {
                        SettingsLink("Library order", Icons.Filled.SwapVert, value = if (order.isEmpty()) "By name" else "Custom", onClick = onLibraryOrder)
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
            text = { Text("This device will need to connect again to use the server.") },
            confirmButton = { TextButton(onClick = model::signOut) { Text("Sign out", color = Flexarr.danger) } },
            dismissButton = { TextButton(onClick = { confirmSignOut = false }) { Text("Cancel", color = Flexarr.screen) } },
            containerColor = Flexarr.seat,
        )
    }
    if (choosingProfile) {
        ModalBottomSheet(onDismissRequest = { choosingProfile = false }, containerColor = Flexarr.room) {
            ProfilePicker(model, marksCurrent = true, onChosen = { choosingProfile = false })
        }
    }
}

/** "192.168.1.20:8383" for "http://192.168.1.20:8383/". */
private fun shortAddress(address: String?): String = address.orEmpty().replace(Regex("^https?://"), "").trimEnd('/')

/** Address, server version and what it runs on. */
@Composable
fun ServerSettingsScreen(model: AppModel) {
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
        }
    }
}

/**
 * The current profile's libraries in the order the Libraries tab shows them, each moved up or down a place at a time.
 * Every move saves straight away, so the order follows the profile to every device.
 */
@Composable
fun LibraryOrderSettingsScreen(model: AppModel) {
    val client = model.client ?: return
    val navigator = LocalNavigator.current
    val loader = rememberLoader(model.credentials) { client.libraries().filter { it.inMenu } }
    // Moved here but maybe not saved yet; null shows the server's order.
    var moved by remember { mutableStateOf<List<Library>?>(null) }
    var problem by remember { mutableStateOf<String?>(null) }
    // One save at a time, in the order the moves were made, so the last move is the one that stays.
    val saving = remember { Mutex() }

    fun save(libraries: List<Library>?) {
        moved = libraries
        problem = null
        model.scope.launch {
            saving.withLock {
                try {
                    val saved = model.currentProfile?.libraryOrder.orEmpty()
                    model.setLibraryOrder(if (libraries == null) emptyList() else libraryOrder(saved, libraries.map { it.id }))
                    if (libraries == null) loader.retry()
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
            Loaded(loader) { loaded ->
                val libraries = moved ?: loaded
                LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(top = 8.dp, bottom = 32.dp), verticalArrangement = Arrangement.spacedBy(28.dp)) {
                    item {
                        Group(footer = problem ?: "This order follows ${model.currentProfile?.name ?: "this profile"} to every device. Libraries added later go at the end.") {
                            libraries.forEachIndexed { index, library ->
                                if (index > 0) Divider()
                                OrderRow(
                                    library,
                                    onUp = if (index > 0) ({ save(libraries.moved(index, index - 1)) }) else null,
                                    onDown = if (index < libraries.lastIndex) ({ save(libraries.moved(index, index + 1)) }) else null,
                                )
                            }
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

@Composable
private fun OrderRow(library: Library, onUp: (() -> Unit)?, onDown: (() -> Unit)?) {
    Row(Modifier.fillMaxWidth().padding(start = 16.dp, end = 4.dp, top = 2.dp, bottom = 2.dp), horizontalArrangement = Arrangement.spacedBy(14.dp), verticalAlignment = Alignment.CenterVertically) {
        Icon(library.icon, contentDescription = null, tint = Flexarr.silver, modifier = Modifier.size(22.dp))
        Text(library.name, fontSize = 17.sp, color = Flexarr.screen, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f))
        IconButton(onClick = { onUp?.invoke() }, enabled = onUp != null) {
            Icon(Icons.Filled.KeyboardArrowUp, contentDescription = "Move ${library.name} up", tint = if (onUp != null) Flexarr.screen else Flexarr.aisle)
        }
        IconButton(onClick = { onDown?.invoke() }, enabled = onDown != null) {
            Icon(Icons.Filled.KeyboardArrowDown, contentDescription = "Move ${library.name} down", tint = if (onDown != null) Flexarr.screen else Flexarr.aisle)
        }
    }
}

private fun <T> List<T>.moved(from: Int, to: Int): List<T> = toMutableList().apply { add(to, removeAt(from)) }

/** A group of rows on the seat, with an optional heading above and note below, as iPhone's grouped lists. */
@Composable
private fun Group(header: String? = null, footer: String? = null, content: @Composable () -> Unit) {
    Column(Modifier.padding(horizontal = Edge), verticalArrangement = Arrangement.spacedBy(8.dp)) {
        header?.let { Text(it.uppercase(), fontSize = 13.sp, color = Flexarr.usher, modifier = Modifier.padding(horizontal = 16.dp)) }
        Column(Modifier.fillMaxWidth().clip(RoundedCornerShape(12.dp)).background(Flexarr.seat)) { content() }
        footer?.let { Text(it, fontSize = 13.sp, color = Flexarr.usher, modifier = Modifier.padding(horizontal = 16.dp)) }
    }
}

@Composable
private fun Divider() = HorizontalDivider(Modifier.padding(start = 16.dp), color = Flexarr.aisle.copy(alpha = 0.6f))

/** A row on the Settings list: an icon in a tile, the page's name and, on the right, how it's set now. */
@Composable
private fun SettingsLink(title: String, icon: ImageVector, value: String?, onClick: () -> Unit) {
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
private fun Fact(label: String, value: String) {
    Row(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 12.dp), horizontalArrangement = Arrangement.spacedBy(16.dp)) {
        Text(label, fontSize = 17.sp, color = Flexarr.screen)
        Text(value, fontSize = 17.sp, color = Flexarr.usher, textAlign = TextAlign.End, maxLines = 2, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f))
    }
}

/** "29 Oct 2026, 09:14" from the server's start time (with or without its offset). */
private fun upSince(text: String): String? {
    val time = runCatching { OffsetDateTime.parse(text).atZoneSameInstant(ZoneId.systemDefault()) }.getOrNull()
        ?: runCatching { LocalDateTime.parse(text).atZone(ZoneId.of("UTC")).withZoneSameInstant(ZoneId.systemDefault()) }.getOrNull()
        ?: return null
    return time.format(DateTimeFormatter.ofLocalizedDateTime(FormatStyle.MEDIUM, FormatStyle.SHORT))
}
