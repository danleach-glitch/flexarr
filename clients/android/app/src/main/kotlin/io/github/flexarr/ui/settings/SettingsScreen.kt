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
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
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
import io.github.flexarr.core.ServerVersion
import io.github.flexarr.ui.components.Edge
import io.github.flexarr.ui.components.Load
import io.github.flexarr.ui.components.LocalNavigator
import io.github.flexarr.ui.components.PageTitle
import io.github.flexarr.ui.components.pressable
import io.github.flexarr.ui.components.rememberLoader
import io.github.flexarr.ui.profiles.ProfileAvatar
import io.github.flexarr.ui.profiles.ProfilePicker
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.RoomBackground
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
fun SettingsScreen(model: AppModel, onServer: () -> Unit) {
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
            item {
                Group {
                    val host = model.credentials?.address?.let { FlexarrClient.normalizeAddress(it)?.host }
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
                Group(footer = "To use Flexarr away from home, turn on remote access with Tailscale in Settings › Access on the web.") {
                    Fact("Address", model.credentials?.address.orEmpty().replace(Regex("^https?://"), "").trimEnd('/'))
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
