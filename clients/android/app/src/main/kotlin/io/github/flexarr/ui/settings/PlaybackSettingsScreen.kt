package io.github.flexarr.ui.settings

import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.UnfoldMore
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.github.flexarr.AppModel
import io.github.flexarr.core.FlexarrException
import io.github.flexarr.core.PlaybackPreferences
import io.github.flexarr.core.SubtitlePosition
import io.github.flexarr.core.SubtitleSize
import io.github.flexarr.core.forSaving
import io.github.flexarr.core.playbackPreferences
import io.github.flexarr.core.shownSubtitlePosition
import io.github.flexarr.core.shownSubtitleSize
import io.github.flexarr.data.PlayerSettings
import io.github.flexarr.data.SoundSettings
import io.github.flexarr.ui.components.LocalNavigator
import io.github.flexarr.ui.components.PageTitle
import io.github.flexarr.ui.components.pressable
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.RoomBackground
import kotlinx.coroutines.launch

/**
 * The profile's auto-play, skips and subtitle defaults (on every device), and how far this phone's player jumps. As
 * the iPhone's PlaybackSettingsView; the profile's part shows once the server has answered.
 */
@Composable
fun PlaybackSettingsScreen(model: AppModel) {
    val client = model.client ?: return
    val navigator = LocalNavigator.current
    val context = LocalContext.current
    var preferences by remember { mutableStateOf<PlaybackPreferences?>(null) }
    var skipSeconds by remember { mutableIntStateOf(PlayerSettings.skipSeconds(context)) }
    val sound = SoundSettings.flow(context).collectAsState().value ?: SoundSettings.current(context)
    // Night mode's hours, on the hour.
    val hours = remember { (0 until 24).map { it * 60 } }

    LaunchedEffect(model.credentials) {
        try {
            preferences = client.playbackPreferences()
        } catch (_: FlexarrException) {
            // A server that can't be asked: only this phone's part shows.
        }
    }

    /** Saved as soon as it changes, and put back if the server says no. [refreshesHome]: Home's rows follow it. */
    fun save(changed: PlaybackPreferences, over: PlaybackPreferences, refreshesHome: Boolean = false) {
        preferences = changed
        model.scope.launch {
            try {
                client.setPlaybackPreferences(changed.forSaving())
                if (refreshesHome) model.watchStateChanged()
            } catch (_: FlexarrException) {
                preferences = over
            }
        }
    }

    RoomBackground(ambient = null) {
        LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 32.dp), verticalArrangement = Arrangement.spacedBy(28.dp)) {
            item { PageTitle("Playback", onBack = { navigator.back() }) }
            preferences?.let { current ->
                item {
                    Group(
                        header = "Your profile, on every device",
                        footer = "Auto-play counts down five seconds, then starts the next episode. Subtitles that run early or late can be moved on Apple TV or the web. In the black bars puts subtitles below the picture of films wider than the screen.",
                    ) {
                        Toggle("Auto-play the next episode", current.autoPlayNext, { save(current.copy(autoPlayNext = it), current) })
                        Divider()
                        Toggle("Skip intros", current.skipIntros, { save(current.copy(skipIntros = it), current) })
                        Divider()
                        Toggle("Skip recaps", current.skipRecaps, { save(current.copy(skipRecaps = it), current) })
                        Divider()
                        Toggle("Skip credits", current.skipCredits, { save(current.copy(skipCredits = it), current) })
                        Divider()
                        Picker("Subtitle size", SubtitleSize.choices, current.shownSubtitleSize, { it.label }) {
                            save(current.copy(subtitleSize = it), current)
                        }
                        Divider()
                        Picker("Subtitle position", SubtitlePosition.choices, current.shownSubtitlePosition, { it.label }) {
                            save(current.copy(subtitlePosition = it), current)
                        }
                        Divider()
                        Toggle("Subtitles in the black bars", current.subtitlesInBlackBars ?: false, { save(current.copy(subtitlesInBlackBars = it), current) })
                    }
                }
            }
            item {
                Group(header = "This phone") {
                    Picker("Skip back and forward", PlayerSettings.skipChoices, skipSeconds, { "$it seconds" }) {
                        skipSeconds = it
                        PlayerSettings.setSkipSeconds(context, it)
                    }
                }
            }
            item {
                Group(header = "Sound") {
                    Picker("Dialogue boost", SoundSettings.DialogueBoost.entries, sound.dialogueBoost, { it.name }) { boost ->
                        SoundSettings.update(context) { it.copy(dialogueBoost = boost) }
                    }
                    Divider()
                    Picker("Night mode", SoundSettings.NightMode.entries, sound.nightMode, { if (it == SoundSettings.NightMode.Scheduled) "On a schedule" else it.name }) { mode ->
                        SoundSettings.update(context) { it.copy(nightMode = mode) }
                    }
                    if (sound.nightMode == SoundSettings.NightMode.Scheduled) {
                        Divider()
                        Picker("From", hours, sound.nightStart, SoundSettings::clock) { at -> SoundSettings.update(context) { it.copy(nightStart = at) } }
                        Divider()
                        Picker("Until", hours, sound.nightEnd, SoundSettings::clock) { at -> SoundSettings.update(context) { it.copy(nightEnd = at) } }
                    }
                    Divider()
                    Picker("Speakers", SoundSettings.Speakers.entries, sound.speakers, { it.label }) { speakers ->
                        SoundSettings.update(context) { it.copy(speakers = speakers) }
                    }
                }
            }
        }
    }
}

/** A row that shows its choice on the right and offers the others in a menu, as an iPhone list's Picker. */
@Composable
internal fun <T> Picker(label: String, choices: List<T>, selected: T, name: (T) -> String, onChoose: (T) -> Unit) {
    var open by remember { mutableStateOf(false) }
    Box {
        Row(
            Modifier.fillMaxWidth().pressable({ open = true }).padding(horizontal = 16.dp, vertical = 14.dp),
            horizontalArrangement = Arrangement.spacedBy(6.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Text(label, fontSize = 17.sp, color = Flexarr.screen, modifier = Modifier.weight(1f))
            Text(name(selected), fontSize = 17.sp, color = Flexarr.usher)
            Icon(Icons.Filled.UnfoldMore, contentDescription = null, tint = Flexarr.usher)
        }
        DropdownMenu(open, onDismissRequest = { open = false }, containerColor = Flexarr.seat, modifier = Modifier.align(Alignment.TopEnd)) {
            choices.forEach { choice ->
                DropdownMenuItem(
                    text = { Text(name(choice), color = Flexarr.screen) },
                    trailingIcon = if (choice == selected) ({ Icon(Icons.Filled.Check, contentDescription = null, tint = Flexarr.silver) }) else null,
                    onClick = {
                        open = false
                        if (choice != selected) onChoose(choice)
                    },
                )
            }
        }
    }
}
