package io.github.flexarr.ui.settings

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Notifications
import androidx.compose.material.icons.filled.Remove
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.github.flexarr.AppModel
import io.github.flexarr.audio.PodcastPlayer
import io.github.flexarr.core.FlexarrException
import io.github.flexarr.core.PodcastPreferencesUpdate
import io.github.flexarr.core.podcastPreferences
import io.github.flexarr.core.setPodcastPreferences
import io.github.flexarr.ui.components.LocalNavigator
import io.github.flexarr.ui.components.PageTitle
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.RoomBackground
import kotlinx.coroutines.launch

/**
 * How the profile listens to podcasts, on every device: auto-downloads, how many episodes they keep, and the speed.
 * As the iPhone's PodcastSettingsView.
 */
@Composable
fun PodcastSettingsScreen(model: AppModel, onNotifications: () -> Unit) {
    val client = model.client ?: return
    val navigator = LocalNavigator.current

    LaunchedEffect(model.credentials) {
        if (model.podcastPreferences == null) {
            try {
                model.podcastPreferences = client.podcastPreferences()
            } catch (_: FlexarrException) {
                // Only the notifications row shows.
            }
        }
    }

    /** Shown at once; the server's answer follows. Downloads catch up with what it now wants. */
    fun update(change: PodcastPreferencesUpdate, reconciles: Boolean = false) {
        model.podcastPreferences?.let { known ->
            model.podcastPreferences = known.copy(
                autoDownload = change.autoDownload ?: known.autoDownload,
                keepEpisodes = change.keepEpisodes ?: known.keepEpisodes,
                speed = change.speed ?: known.speed,
            )
        }
        model.scope.launch {
            try {
                model.podcastPreferences = client.setPodcastPreferences(change)
            } catch (_: FlexarrException) {
                return@launch
            }
            if (reconciles) model.downloads.reconcilePodcasts()
        }
    }

    RoomBackground(ambient = null) {
        LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 32.dp), verticalArrangement = Arrangement.spacedBy(28.dp)) {
            item { PageTitle("Podcasts", onBack = { navigator.back() }) }
            model.podcastPreferences?.let { podcasts ->
                item {
                    Group(
                        header = "Your profile, on every device",
                        footer = "Auto-download keeps the newest unplayed episodes of every show you follow, and deletes them once they’re played. Picking a speed in the player changes it here too.",
                    ) {
                        Toggle("Auto-download new episodes", podcasts.autoDownload, { update(PodcastPreferencesUpdate(autoDownload = it), reconciles = true) })
                        Divider()
                        Stepper("Keep the newest ${podcasts.keepEpisodes}", podcasts.keepEpisodes, 1..50, enabled = podcasts.autoDownload) {
                            update(PodcastPreferencesUpdate(keepEpisodes = it), reconciles = true)
                        }
                        Divider()
                        Picker("Speed", PodcastPlayer.speeds, podcasts.speed, PodcastPlayer::speedLabel) {
                            update(PodcastPreferencesUpdate(speed = it))
                        }
                    }
                }
            }
            item {
                Group(footer = "New episodes can come as notifications, along with films and shows.") {
                    SettingsLink("Notifications", Icons.Filled.Notifications, value = null, onClick = onNotifications)
                }
            }
        }
    }
}

/** A number taken down or up one at a time, within [range], as an iPhone list's Stepper. */
@Composable
internal fun Stepper(label: String, value: Int, range: IntRange, enabled: Boolean = true, onChange: (Int) -> Unit) {
    Row(Modifier.fillMaxWidth().padding(start = 16.dp, end = 4.dp, top = 2.dp, bottom = 2.dp), verticalAlignment = Alignment.CenterVertically) {
        Text(label, fontSize = 17.sp, color = if (enabled) Flexarr.screen else Flexarr.usher, modifier = Modifier.weight(1f))
        val canLower = enabled && value > range.first
        val canRaise = enabled && value < range.last
        IconButton(onClick = { onChange(value - 1) }, enabled = canLower) {
            Icon(Icons.Filled.Remove, contentDescription = "Fewer", tint = if (canLower) Flexarr.screen else Flexarr.aisle)
        }
        IconButton(onClick = { onChange(value + 1) }, enabled = canRaise) {
            Icon(Icons.Filled.Add, contentDescription = "More", tint = if (canRaise) Flexarr.screen else Flexarr.aisle)
        }
    }
}
