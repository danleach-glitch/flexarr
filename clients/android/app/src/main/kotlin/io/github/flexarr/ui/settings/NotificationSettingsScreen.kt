package io.github.flexarr.ui.settings

import android.Manifest
import android.content.Intent
import android.os.Build
import android.provider.Settings
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LifecycleEventEffect
import io.github.flexarr.AppModel
import io.github.flexarr.core.NotificationDevice
import io.github.flexarr.core.NotificationSettings
import io.github.flexarr.ui.components.LocalNavigator
import io.github.flexarr.ui.components.PageTitle
import io.github.flexarr.ui.components.pressable
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.RoomBackground
import kotlinx.coroutines.launch
import java.time.LocalTime
import java.time.format.DateTimeFormatter
import java.time.format.FormatStyle

/** Notifications: on or off, for which profile, and what about. As the iPhone's NotificationSettingsView. */
@Composable
fun NotificationSettingsScreen(model: AppModel) {
    val navigator = LocalNavigator.current
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val notifications = model.notifications
    var device by remember { mutableStateOf<NotificationDevice?>(null) }
    var allowed by remember { mutableStateOf(notifications.isAllowed) }
    var working by remember { mutableStateOf(false) }
    var problem by remember { mutableStateOf<Pair<String, String>?>(null) }
    var testSent by remember { mutableStateOf(false) }

    fun attempt(title: String = "Couldn’t change notifications", action: suspend () -> Unit) {
        val credentials = model.credentials ?: return
        if (working) return
        working = true
        scope.launch {
            try {
                action()
            } catch (e: Exception) {
                problem = title to (e.message ?: "Something went wrong.")
            } finally {
                working = false
                allowed = notifications.isAllowed
            }
        }
    }

    fun turnOn() = attempt { device = notifications.turnOn(model.credentials!!) }

    // Android 13 on asks before the app may post notifications.
    val permission = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        allowed = notifications.isAllowed
        if (granted) turnOn()
    }

    fun turn(on: Boolean) {
        if (!on) return attempt { notifications.turnOff(model.credentials); device = null }
        if (!notifications.isAllowed && Build.VERSION.SDK_INT >= 33) permission.launch(Manifest.permission.POST_NOTIFICATIONS) else turnOn()
    }

    fun save(changed: NotificationSettings) = attempt { device = notifications.update(changed, model.credentials!!) }

    LaunchedEffect(model.credentials) {
        val credentials = model.credentials ?: return@LaunchedEffect
        if (notifications.isAvailable) device = runCatching { notifications.load(credentials) }.getOrNull()
    }
    // Back from Android's settings, say.
    LifecycleEventEffect(Lifecycle.Event.ON_RESUME) { allowed = notifications.isAllowed }

    RoomBackground(ambient = null) {
        LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 32.dp), verticalArrangement = Arrangement.spacedBy(28.dp)) {
            item { PageTitle("Notifications", onBack = { navigator.back() }) }
            if (!notifications.isAvailable) {
                item {
                    Group(footer = "This build of Flexarr was made without Google’s notification service, so it can’t get notifications. Versions from Google Play can.") {
                        Fact("Notifications", "Not available")
                    }
                }
                return@LazyColumn
            }
            if (!allowed && notifications.isOn) {
                item {
                    Group(footer = "Notifications are off for Flexarr in Android's settings. Allow them there first.") {
                        Text(
                            "Open Settings",
                            fontSize = 17.sp,
                            color = Flexarr.silver,
                            modifier = Modifier.fillMaxWidth().pressable({
                                context.startActivity(Intent(Settings.ACTION_APP_NOTIFICATION_SETTINGS).putExtra(Settings.EXTRA_APP_PACKAGE, context.packageName))
                            }).padding(horizontal = 16.dp, vertical = 14.dp),
                        )
                    }
                }
            }
            item {
                Group(
                    footer = when {
                        device?.relay == false -> "Your server has notifications turned off, so none will arrive. They can be turned on in its configuration."
                        !notifications.isOn -> "Hear when something new is ready. This phone gets them for one profile."
                        else -> null
                    },
                ) {
                    Toggle("Notifications", notifications.isOn, ::turn, enabled = !working)
                }
            }
            if (notifications.isOn) {
                item {
                    Group {
                        Fact("For", notifications.profileName ?: "Your profile")
                        val current = model.currentProfile
                        if (current != null && notifications.profileId != null && current.id != notifications.profileId) {
                            Divider()
                            Text(
                                "Get notifications for ${current.name} instead",
                                fontSize = 17.sp,
                                color = Flexarr.silver,
                                modifier = Modifier.fillMaxWidth().pressable({ attempt { device = notifications.move(model.credentials!!) } }, enabled = !working)
                                    .padding(horizontal = 16.dp, vertical = 14.dp),
                            )
                        }
                    }
                }
                item {
                    val settings = notifications.settings
                    Group(header = "Tell me about") {
                        Toggle("Films", settings.films, { save(settings.copy(films = it)) })
                        Divider()
                        Toggle("Shows", settings.shows, { save(settings.copy(shows = it)) })
                        if (model.podcastStatus?.available != false) {
                            Divider()
                            Toggle("Podcasts", settings.podcasts, { save(settings.copy(podcasts = it)) })
                        }
                        if (model.hasLiveTv) {
                            Divider()
                            Toggle("Recordings", settings.recordings, { save(settings.copy(recordings = it)) })
                        }
                        Divider()
                        Toggle("Requests", settings.requests, { save(settings.copy(requests = it)) }, detail = "When something you requested is ready")
                    }
                }
                item {
                    val settings = notifications.settings
                    Group {
                        Toggle("Upcoming", settings.upcoming, { save(settings.copy(upcoming = it)) }, detail = "Each morning, what’s airing today")
                        if (settings.upcoming) {
                            Divider()
                            HourPicker(settings.upcomingHour) { save(settings.copy(upcomingHour = it)) }
                        }
                    }
                }
                item {
                    Group {
                        Text(
                            "Send test notification",
                            fontSize = 17.sp,
                            color = Flexarr.silver,
                            modifier = Modifier.fillMaxWidth()
                                .pressable({ attempt("Couldn’t send a test notification") { notifications.sendTest(model.credentials!!); testSent = true } }, enabled = !working)
                                .padding(horizontal = 16.dp, vertical = 14.dp),
                        )
                    }
                }
            }
        }
    }

    problem?.let { (title, message) ->
        AlertDialog(
            onDismissRequest = { problem = null },
            title = { Text(title) },
            text = { Text(message) },
            confirmButton = { TextButton(onClick = { problem = null }) { Text("OK", color = Flexarr.screen) } },
            containerColor = Flexarr.seat,
        )
    }
    if (testSent) {
        AlertDialog(
            onDismissRequest = { testSent = false },
            title = { Text("Test notification sent") },
            text = { Text("It should arrive in a moment.") },
            confirmButton = { TextButton(onClick = { testSent = false }) { Text("OK", color = Flexarr.screen) } },
            containerColor = Flexarr.seat,
        )
    }
}

/** "At 9:00 AM", as the phone tells the time, choosing from the day's hours. */
@Composable
private fun HourPicker(hour: Int, onChange: (Int) -> Unit) {
    var open by remember { mutableStateOf(false) }
    val format = DateTimeFormatter.ofLocalizedTime(FormatStyle.SHORT)
    androidx.compose.foundation.layout.Box {
        androidx.compose.foundation.layout.Row(Modifier.fillMaxWidth().pressable({ open = true }).padding(horizontal = 16.dp, vertical = 12.dp)) {
            Text("At", fontSize = 17.sp, color = Flexarr.screen, modifier = Modifier.weight(1f))
            Text(LocalTime.of(hour, 0).format(format), fontSize = 17.sp, color = Flexarr.usher)
        }
        DropdownMenu(open, onDismissRequest = { open = false }, containerColor = Flexarr.seat) {
            (0..23).forEach { h ->
                DropdownMenuItem(text = { Text(LocalTime.of(h, 0).format(format), color = Flexarr.screen) }, onClick = { open = false; onChange(h) })
            }
        }
    }
}
