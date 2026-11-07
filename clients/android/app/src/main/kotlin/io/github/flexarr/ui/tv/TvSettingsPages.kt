package io.github.flexarr.ui.tv

import io.github.flexarr.core.i18n.Words
import io.github.flexarr.core.i18n.t
import androidx.compose.material.icons.filled.Language
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.relocation.BringIntoViewRequester
import androidx.compose.foundation.relocation.bringIntoViewRequester
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.Send
import androidx.compose.material.icons.filled.CallToAction
import androidx.compose.material.icons.filled.ClosedCaption
import androidx.compose.material.icons.filled.EditNote
import androidx.compose.material.icons.filled.FastForward
import androidx.compose.material.icons.filled.SurroundSound
import androidx.compose.material.icons.filled.HdrOn
import androidx.compose.material.icons.filled.Tv
import androidx.compose.material.icons.filled.Forward10
import androidx.compose.material.icons.filled.Forward30
import androidx.compose.material.icons.filled.GridView
import androidx.compose.material.icons.filled.Height
import androidx.compose.material.icons.filled.History
import androidx.compose.material.icons.filled.MedicalServices
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.SkipNext
import androidx.compose.material.icons.filled.Speaker
import androidx.compose.material.icons.filled.WbTwilight
import androidx.compose.material.icons.filled.NightsStay
import androidx.compose.material.icons.filled.Bedtime
import androidx.compose.material.icons.filled.RecordVoiceOver
import androidx.compose.material.icons.filled.TheaterComedy
import androidx.compose.material.icons.filled.Theaters
import androidx.compose.material.icons.filled.VideoLibrary
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.getValue
import androidx.compose.runtime.setValue
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.input.key.Key
import androidx.compose.ui.input.key.KeyEventType
import androidx.compose.ui.input.key.key
import androidx.compose.ui.input.key.onPreviewKeyEvent
import androidx.compose.ui.input.key.type
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.window.Dialog
import androidx.tv.material3.Text
import io.github.flexarr.AppModel
import io.github.flexarr.core.FlexarrException
import io.github.flexarr.core.Library
import io.github.flexarr.core.PlaybackPreferences
import io.github.flexarr.core.ServerVersion
import io.github.flexarr.core.SubtitlePosition
import io.github.flexarr.core.SubtitleSize
import io.github.flexarr.core.forSaving
import io.github.flexarr.core.libraryOrder
import io.github.flexarr.core.playbackPreferences
import io.github.flexarr.core.shownSubtitlePosition
import io.github.flexarr.core.shownSubtitleSize
import io.github.flexarr.data.PlayerSettings
import io.github.flexarr.data.SoundSettings
import io.github.flexarr.player.DeviceCapabilities
import io.github.flexarr.logging.ErrorReports
import io.github.flexarr.logging.LogHub
import io.github.flexarr.ui.components.Load
import io.github.flexarr.ui.components.Loader
import io.github.flexarr.ui.components.rememberLoader
import io.github.flexarr.ui.library.LibraryMenuEntry
import io.github.flexarr.ui.settings.diagnosticsHeader
import io.github.flexarr.ui.settings.shortAddress
import io.github.flexarr.ui.settings.upSince
import io.github.flexarr.ui.theme.Flexarr
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import io.github.flexarr.ui.theme.Tokens

// Settings' pages after the first, as the Apple TV's (FlexarrTV/Screens/Settings).

/** A Playback row that opens a page of choices. */
private enum class PlaybackChoice { SubtitleSize, SubtitlePosition, Skip, DialogueBoost, NightMode, NightFrom, NightUntil, Speakers }

/**
 * The profile's playback preferences (saved on the server, so they follow it to every device), then this TV's own:
 * how far the remote skips and how films sound in this room. As the Apple TV's PlaybackSettingsView.
 */
@Composable
internal fun TvPlaybackSettings(model: AppModel) {
    val client = model.client ?: return
    val context = LocalContext.current
    var preferences by remember { mutableStateOf<PlaybackPreferences?>(null) }
    var loaded by remember { mutableStateOf(false) }
    var saveFailed by remember { mutableStateOf(false) }
    var skipSeconds by remember { mutableIntStateOf(PlayerSettings.skipSeconds(context)) }
    var choosing by remember { mutableStateOf<PlaybackChoice?>(null) }
    val sound = SoundSettings.flow(context).collectAsState().value ?: SoundSettings.current(context)
    // The choice page Back or a choice just closed, so focus goes back to its row.
    var chose by remember { mutableStateOf<PlaybackChoice?>(null) }

    LaunchedEffect(model.credentials) {
        try {
            preferences = client.playbackPreferences()
        } catch (_: FlexarrException) {
            // A server that can't be asked: only this TV's part shows.
        }
        loaded = true
    }

    /** Saved as soon as it changes, and put back if the server says no. */
    fun save(changed: PlaybackPreferences, over: PlaybackPreferences) {
        preferences = changed
        saveFailed = false
        model.scope.launch {
            try {
                client.setPlaybackPreferences(changed.forSaving())
            } catch (_: FlexarrException) {
                preferences = over
                saveFailed = true
            }
        }
    }

    fun close() {
        chose = choosing
        choosing = null
    }

    val current = preferences
    when (choosing) {
        PlaybackChoice.SubtitleSize -> if (current != null) {
            BackHandler(onBack = ::close)
            TvSettingsChoicePage(
                t("Subtitle size"), t("For this profile on every device."), SubtitleSize.choices, { it.label }, current.shownSubtitleSize,
                choose = { save(current.copy(subtitleSize = it), current) }, onDone = ::close,
            )
            return
        }
        PlaybackChoice.SubtitlePosition -> if (current != null) {
            BackHandler(onBack = ::close)
            TvSettingsChoicePage(
                t("Subtitle position"), t("For this profile on every device."), SubtitlePosition.choices, { it.label }, current.shownSubtitlePosition,
                choose = { save(current.copy(subtitlePosition = it), current) }, onDone = ::close,
            )
            return
        }
        PlaybackChoice.Skip -> {
            BackHandler(onBack = ::close)
            TvSettingsChoicePage(
                t("Skip back and forward"), t("A press of left or right on the remote."), PlayerSettings.skipChoices, { t("{count, plural, one {# second} other {# seconds}}", "count" to it) }, skipSeconds,
                choose = {
                    skipSeconds = it
                    PlayerSettings.setSkipSeconds(context, it)
                },
                onDone = ::close,
            )
            return
        }
        PlaybackChoice.DialogueBoost -> {
            BackHandler(onBack = ::close)
            TvSettingsChoicePage(
                t("Dialogue boost"), null, SoundSettings.DialogueBoost.entries, ::dialogueBoostLabel, sound.dialogueBoost,
                choose = { boost -> SoundSettings.update(context) { it.copy(dialogueBoost = boost) } }, onDone = ::close,
            )
            return
        }
        PlaybackChoice.NightMode -> {
            BackHandler(onBack = ::close)
            TvSettingsChoicePage(
                t("Night mode"), null, SoundSettings.NightMode.entries, ::nightModeLabel, sound.nightMode,
                choose = { mode -> SoundSettings.update(context) { it.copy(nightMode = mode) } }, onDone = ::close,
            )
            return
        }
        PlaybackChoice.NightFrom -> {
            BackHandler(onBack = ::close)
            TvSettingsChoicePage(
                t("Night mode from"), null, NIGHT_HOURS, SoundSettings::clock, sound.nightStart,
                choose = { at -> SoundSettings.update(context) { it.copy(nightStart = at) } }, onDone = ::close,
            )
            return
        }
        PlaybackChoice.NightUntil -> {
            BackHandler(onBack = ::close)
            TvSettingsChoicePage(
                t("Night mode until"), null, NIGHT_HOURS, SoundSettings::clock, sound.nightEnd,
                choose = { at -> SoundSettings.update(context) { it.copy(nightEnd = at) } }, onDone = ::close,
            )
            return
        }
        PlaybackChoice.Speakers -> {
            BackHandler(onBack = ::close)
            TvSettingsChoicePage(
                t("Speakers"), null, SoundSettings.Speakers.entries, ::speakersLabel, sound.speakers,
                choose = { speakers -> SoundSettings.update(context) { it.copy(speakers = speakers) } }, onDone = ::close,
            )
            return
        }
        null -> {}
    }

    // The first row once the server's answered (the profile's half may not come), or the row a choice page came back to.
    val focusOn = chose ?: if (current != null) null else PlaybackChoice.Skip
    val focus = remember { FocusRequester() }
    TakeFocus(focus, key = focusOn, ready = loaded)
    fun Modifier.focusFor(row: PlaybackChoice?) = if (row == focusOn) focusRequester(focus) else this

    TvSettingsPage(t("Playback"), summary = if (saveFailed) t("That change couldn’t be saved on the server, so it’s been put back.") else null) {
        if (current != null) {
            item { TvSettingsGroupLabel(model.currentProfile?.let { t("{name}, on every device", "name" to it.name) } ?: t("Your profile, on every device")) }
            item {
                TvSettingsToggle(
                    t(Words.autoPlayNext), current.autoPlayNext, { save(current.copy(autoPlayNext = !current.autoPlayNext), current) },
                    modifier = Modifier.focusFor(null), icon = Icons.Filled.SkipNext,
                    help = t(Words.autoPlayNextHelp),
                )
            }
            item {
                TvSettingsToggle(
                    t("Skip intros"), current.skipIntros, { save(current.copy(skipIntros = !current.skipIntros), current) }, icon = Icons.Filled.FastForward,
                    help = t("Jumps past a show’s opening titles by itself. Off, a Skip intro button shows instead."),
                )
            }
            item {
                TvSettingsToggle(
                    t("Skip recaps"), current.skipRecaps, { save(current.copy(skipRecaps = !current.skipRecaps), current) }, icon = Icons.Filled.History,
                    help = t("Jumps past the “Previously on” at the start of an episode."),
                )
            }
            item {
                TvSettingsToggle(
                    t(Words.skipCredits), current.skipCredits, { save(current.copy(skipCredits = !current.skipCredits), current) }, icon = Icons.Filled.Theaters,
                    help = t(Words.skipCreditsHelp),
                )
            }
            item {
                TvSettingsRow(
                    t("Subtitle size"), onClick = { choosing = PlaybackChoice.SubtitleSize }, modifier = Modifier.focusFor(PlaybackChoice.SubtitleSize),
                    icon = Icons.Filled.ClosedCaption, value = current.shownSubtitleSize.label, accessory = TvSettingAccessory.Chevron,
                    help = t("How big subtitles are. Subtitles that run early or late can be moved from the player’s Subtitles menu."),
                )
            }
            item {
                TvSettingsRow(
                    t("Subtitle position"), onClick = { choosing = PlaybackChoice.SubtitlePosition }, modifier = Modifier.focusFor(PlaybackChoice.SubtitlePosition),
                    icon = Icons.Filled.Height, value = current.shownSubtitlePosition.label, accessory = TvSettingAccessory.Chevron,
                    help = t("Raise subtitles clear of text in the picture or the edge of the TV. Styled subtitles keep the places they were made for."),
                )
            }
            item {
                val inBars = current.subtitlesInBlackBars ?: false
                TvSettingsToggle(
                    t("Subtitles in the black bars"), inBars, { save(current.copy(subtitlesInBlackBars = !inBars), current) }, icon = Icons.Filled.CallToAction,
                    help = t("In movies wider than the TV, subtitles go in the black bar below the picture rather than over it."),
                )
            }
        } else if (!loaded) {
            // The server's half arrives a moment later; this keeps the rows below from jumping up and back.
            item { TvSettingsGroupLabel(t("Your profile, on every device")) }
            item { CircularProgressIndicator(Modifier.padding(start = 16.dp, top = 10.dp, bottom = 10.dp).size(20.dp), color = Flexarr.screen, strokeWidth = 2.dp) }
        }

        item { TvSettingsGroupLabel(t("This TV")) }
        item {
            TvSettingsRow(
                t("Skip back and forward"), onClick = { choosing = PlaybackChoice.Skip }, modifier = Modifier.focusFor(PlaybackChoice.Skip),
                icon = skipIcon(skipSeconds), value = t("{count, plural, one {# second} other {# seconds}}", "count" to skipSeconds), accessory = TvSettingAccessory.Chevron,
                help = t("How far a press of left or right on the remote moves while something plays."),
            )
        }
        item {
            TvSettingsRow(
                t("Dialogue boost"), onClick = { choosing = PlaybackChoice.DialogueBoost }, modifier = Modifier.focusFor(PlaybackChoice.DialogueBoost),
                icon = Icons.Filled.RecordVoiceOver, value = dialogueBoostLabel(sound.dialogueBoost), accessory = TvSettingAccessory.Chevron,
                help = t("Makes voices easier to follow under music and effects."),
            )
        }
        item {
            TvSettingsRow(
                t("Night mode"), onClick = { choosing = PlaybackChoice.NightMode }, modifier = Modifier.focusFor(PlaybackChoice.NightMode),
                icon = Icons.Filled.Bedtime, accessory = TvSettingAccessory.Chevron,
                value = if (sound.nightMode == SoundSettings.NightMode.Scheduled) sound.nightHours else nightModeLabel(sound.nightMode),
                help = t("Quietens loud scenes and lifts quiet ones."),
            )
        }
        if (sound.nightMode == SoundSettings.NightMode.Scheduled) {
            item {
                TvSettingsRow(
                    t("Night mode from"), onClick = { choosing = PlaybackChoice.NightFrom }, modifier = Modifier.focusFor(PlaybackChoice.NightFrom),
                    icon = Icons.Filled.NightsStay, value = SoundSettings.clock(sound.nightStart), accessory = TvSettingAccessory.Chevron,
                )
            }
            item {
                TvSettingsRow(
                    t("Night mode until"), onClick = { choosing = PlaybackChoice.NightUntil }, modifier = Modifier.focusFor(PlaybackChoice.NightUntil),
                    icon = Icons.Filled.WbTwilight, value = SoundSettings.clock(sound.nightEnd), accessory = TvSettingAccessory.Chevron,
                )
            }
        }
        item {
            TvSettingsRow(
                t("Speakers"), onClick = { choosing = PlaybackChoice.Speakers }, modifier = Modifier.focusFor(PlaybackChoice.Speakers),
                icon = Icons.Filled.Speaker, value = speakersLabel(sound.speakers), accessory = TvSettingAccessory.Chevron,
                help = t("Auto plays every channel what’s connected takes."),
            )
        }
        item {
            TvSettingsToggle(
                t("Lossless surround"), sound.losslessSurround, { SoundSettings.update(context) { it.copy(losslessSurround = !it.losslessSurround) } },
                icon = Icons.Filled.SurroundSound,
                help = if (sound.losslessSurround && sound.decodesSurround) {
                    t("Dialogue boost, night mode and speakers work on decoded sound, so while one of them is on, surround is decoded on this TV.")
                } else {
                    t("Dolby Digital, Atmos, DTS and TrueHD go to your amplifier or soundbar as they are, when it takes them. Off, they’re decoded on this TV.")
                },
            )
        }
    }
}

private fun nightModeLabel(mode: SoundSettings.NightMode) = when (mode) {
    SoundSettings.NightMode.Off -> t("Off")
    SoundSettings.NightMode.On -> t("On")
    SoundSettings.NightMode.Scheduled -> t("On a schedule")
}

private fun dialogueBoostLabel(boost: SoundSettings.DialogueBoost) = when (boost) {
    SoundSettings.DialogueBoost.Off -> t("Off")
    SoundSettings.DialogueBoost.Low -> t("Low")
    SoundSettings.DialogueBoost.Medium -> t("Medium")
    SoundSettings.DialogueBoost.High -> t("High")
}

/** "5.1" reads the same in every language. */
private fun speakersLabel(speakers: SoundSettings.Speakers) = when (speakers) {
    SoundSettings.Speakers.Auto -> t("Auto")
    SoundSettings.Speakers.Stereo -> t("Stereo")
    SoundSettings.Speakers.Surround -> speakers.label
}

/** Night mode's hours, on the hour. */
private val NIGHT_HOURS = (0 until 24).map { it * 60 }

private fun skipIcon(seconds: Int): ImageVector = when (seconds) {
    10 -> Icons.Filled.Forward10
    30 -> Icons.Filled.Forward30
    else -> Icons.Filled.FastForward
}

/** What a film or TV library's tab opens on, for every library on this TV. As the Apple TV's LibrarySettingsView. */
@Composable
internal fun TvLanguageSettings(model: AppModel) {
    val focus = remember { FocusRequester() }
    TakeFocus(focus)
    var saving by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }

    fun choose(code: String?) {
        if (saving) return
        saving = true
        error = null
        model.scope.launch {
            try {
                model.setLanguage(code)
            } catch (e: io.github.flexarr.core.FlexarrException) {
                error = e.message
            } finally {
                saving = false
            }
        }
    }

    TvSettingsPage(io.github.flexarr.core.i18n.t("Language"), summary = error ?: io.github.flexarr.core.i18n.t("This profile’s own, on every device.")) {
        item {
            TvSettingsRow(
                io.github.flexarr.core.i18n.t("Device language"), onClick = { choose(null) },
                modifier = if (model.profileLanguage == null) Modifier.focusRequester(focus) else Modifier,
                icon = Icons.Filled.Language,
                accessory = if (model.profileLanguage == null) TvSettingAccessory.Checkmark else TvSettingAccessory.None,
            )
        }
        model.languages.forEach { language ->
            item {
                TvSettingsRow(
                    language.name, onClick = { choose(language.code) },
                    modifier = if (model.profileLanguage == language.code) Modifier.focusRequester(focus) else Modifier,
                    accessory = if (model.profileLanguage == language.code) TvSettingAccessory.Checkmark else TvSettingAccessory.None,
                )
            }
        }
    }
}

@Composable
internal fun TvLibrarySettings() {
    val context = LocalContext.current
    val opensOn = TvLibraryOpening.saved(context)
    val focus = remember { FocusRequester() }
    TakeFocus(focus)
    TvSettingsPage(t(Words.libraries), summary = null) {
        item { TvSettingsGroupLabel(t("Open on")) }
        TvLibraryOpening.entries.forEach { opening ->
            item {
                TvSettingsRow(
                    opening.label, onClick = { TvLibraryOpening.save(context, opening) },
                    modifier = if (opening == opensOn) Modifier.focusRequester(focus) else Modifier,
                    icon = opening.icon, help = opening.help,
                    accessory = if (opening == opensOn) TvSettingAccessory.Checkmark else TvSettingAccessory.None,
                )
            }
        }
    }
}

private val TvLibraryOpening.help: String
    get() = when (this) {
        TvLibraryOpening.Browse -> t("A library opens on its highlights: what you’re watching in it, the latest, the best not yet watched and its biggest genres.")
        TvLibraryOpening.All -> t("A library opens on every title, A to Z, with sorting, genres and a letter rail to jump through it.")
        TvLibraryOpening.Genres -> t("A movie or TV library opens on its genres, each with a picture from one of its titles. Home videos open on Browse.")
    }

private val TvLibraryOpening.icon: ImageVector
    get() = when (this) {
        TvLibraryOpening.Browse -> Icons.Filled.VideoLibrary
        TvLibraryOpening.All -> Icons.Filled.GridView
        TvLibraryOpening.Genres -> Icons.Filled.TheaterComedy
    }

/**
 * The order of the libraries and Live TV in the sidebar, for the profile on every device. OK picks one up, up and
 * down move it, and OK again puts it down, as when arranging apps on the Apple TV's Home screen. Each move is saved
 * straight away. As the Apple TV's TabBarSettingsView.
 */
@Composable
internal fun TvSidebarSettings(model: AppModel, libraries: Loader<List<Library>>) {
    // Moved here but maybe not saved yet; null shows the profile's order.
    var moved by remember { mutableStateOf<List<LibraryMenuEntry>?>(null) }
    // The entry picked up, which moves where up and down say.
    var moving by remember { mutableStateOf<String?>(null) }
    var problem by remember { mutableStateOf<String?>(null) }
    // One save at a time, in the order the moves were made, so the last move is the one that stays.
    val saving = remember { Mutex() }
    val scope = rememberCoroutineScope()
    val entries = moved ?: sidebarEntries(model, libraries)
    val focus = remember { FocusRequester() }
    TakeFocus(focus, ready = entries.isNotEmpty())

    // Back with one picked up puts it down where it is.
    BackHandler(enabled = moving != null) { moving = null }

    fun save(order: List<LibraryMenuEntry>) {
        moved = order
        problem = null
        model.scope.launch {
            saving.withLock {
                try {
                    // Podcasts (not in the sidebar) keep their place among the rest.
                    model.setLibraryOrder(libraryOrder(model.currentProfile?.libraryOrder.orEmpty(), order.map { it.id }))
                } catch (e: FlexarrException) {
                    problem = t("Couldn’t save the order. {reason}", "reason" to e.message.orEmpty())
                    moved = null
                    moving = null
                }
            }
        }
    }

    fun step(id: String, by: Int, into: BringIntoViewRequester) {
        val from = entries.indexOfFirst { it.id == id }
        val to = from + by
        if (from < 0 || to !in entries.indices) return
        save(entries.toMutableList().apply { add(to, removeAt(from)) })
        scope.launch { into.bringIntoView() }
    }

    TvSettingsPage(
        t("Sidebar"),
        summary = problem ?: t("Home is always first, and Discover, Search and Settings last. Your other devices list your libraries in this order too."),
    ) {
        item { TvSettingsGroupLabel(t("Between Home and Search")) }
        items(entries, key = { it.id }) { entry ->
            val picked = moving == entry.id
            val into = remember { BringIntoViewRequester() }
            TvSettingsRow(
                entry.title,
                onClick = { moving = if (picked) null else entry.id },
                modifier = Modifier
                    .animateItem()
                    .bringIntoViewRequester(into)
                    .then(if (entry == entries.first()) Modifier.focusRequester(focus) else Modifier)
                    .onPreviewKeyEvent { event ->
                        if (!picked) return@onPreviewKeyEvent false
                        when (event.key) {
                            // Picked up, up and down move it rather than focus.
                            Key.DirectionUp, Key.DirectionDown -> {
                                if (event.type == KeyEventType.KeyDown) step(entry.id, if (event.key == Key.DirectionUp) -1 else 1, into)
                                true
                            }
                            // Anywhere else (the sidebar) puts it down where it is.
                            Key.DirectionLeft, Key.DirectionRight -> {
                                moving = null
                                false
                            }
                            else -> false
                        }
                    },
                icon = entry.icon,
                value = if (picked) t("Move up or down") else null,
                help = if (picked) t("Press up or down to move {name}, then press OK to put it down.", "name" to entry.title) else t("Press OK to pick up {name}, then press up or down to move it.", "name" to entry.title),
                accessory = if (picked) TvSettingAccessory.Moving else TvSettingAccessory.Handle,
            )
        }
    }
}

/** The server this TV uses: where it is, how it's reached now, and what it's running. As the Apple TV's ServerSettingsView. */
@Composable
internal fun TvServerSettings(model: AppModel, libraries: Loader<List<Library>>, returningFrom: TvSettingsPageId?, onDiagnostics: () -> Unit) {
    val client = model.client ?: return
    val status = rememberLoader(model.credentials) { client.systemStatus() }
    val checking = status.state is Load.Loading || status.refreshing
    val remote = model.credentials?.remoteAddress
    val focus = remember { FocusRequester() }
    val onDiagnosticsRow = returningFrom == TvSettingsPageId.Diagnostics
    TakeFocus(focus)

    val guest = model.credentials?.guest
    TvSettingsPage(
        t("Server"),
        summary = if (guest != null) {
            null
        } else if (remote == null) {
            t("To watch away from home, turn on remote access with Tailscale in Settings › Remote access on the web. This TV picks the address up by itself.")
        } else {
            t("Away from home, this TV reaches the server over Tailscale, which needs to be connected on it.")
        },
    ) {
        item { TvSettingsGroupLabel(t("Connection")) }
        // A guest's server is only ever reached through the tunnel: no address to show.
        if (guest != null) {
            item { TvSettingsFact(t("Connected by invite"), model.guestServerTitle(guest)) }
        } else {
            model.serverName?.let { name -> item { TvSettingsFact(t("Name"), name) } }
            item { TvSettingsFact(t("Address"), shortAddress(model.credentials?.address)) }
        }
        if (remote != null && guest == null) {
            item { TvSettingsFact(t("Away from home"), shortAddress(remote)) }
            item { TvSettingsFact(t("Connected"), if (client.addresses.isAway) t("Away, over Tailscale") else t("At home")) }
        }

        item { TvSettingsGroupLabel(t("Server")) }
        when (val state = status.state) {
            is Load.Loaded -> {
                item { TvSettingsFact(t("Version"), ServerVersion.short(state.value.version)) }
                item { TvSettingsFact(t("Running on"), state.value.osDescription) }
                state.value.startTime?.let(::upSince)?.let { item { TvSettingsFact(t("Up since"), it) } }
            }
            is Load.Failed -> item { TvSettingsFact(t("Status"), state.message) }
            Load.Loading -> item { TvSettingsFact(t("Status"), t("Checking")) }
        }
        item { TvSettingsFact(t(Words.libraries), (libraries.state as? Load.Loaded)?.value?.joinToString(", ") { it.name }.orEmpty()) }

        item { TvSettingsGroupLabel(t("Error reports")) }
        item {
            // As the server says now, or as this TV last heard while it can't be asked.
            val reportsOn = (status.state as? Load.Loaded)?.value?.let { it.errorReports?.enabled == true } ?: ErrorReports.isOn
            TvSettingsFact(t("Error reports"), if (reportsOn) t("On") else t("Off"))
        }
        item { TvSettingsNote(ErrorReports.PRIVACY_NOTE) }

        // The first control here, so focus has somewhere to land; it also tells whether the server's still up. While
        // it's busy it ignores a press rather than turning off, which on a TV would take focus away from it.
        item {
            TvSettingsRow(
                if (checking) t("Checking…") else t("Check again"), onClick = { if (!checking) status.retry() },
                modifier = Modifier.padding(top = 15.dp).then(if (onDiagnosticsRow) Modifier else Modifier.focusRequester(focus)),
                icon = Icons.Filled.Refresh, help = t("Ask the server how it is now."),
            )
        }
        item {
            TvSettingsRow(
                t("Send diagnostics"), onClick = onDiagnostics, modifier = if (onDiagnosticsRow) Modifier.focusRequester(focus) else Modifier,
                icon = Icons.Filled.MedicalServices, accessory = TvSettingAccessory.Chevron,
                help = t("Send this TV’s recent log to your server, to look into a problem."),
            )
        }
    }
}

/**
 * Send diagnostics: this TV's recent log, to the server only, for looking into a problem. A note says what happened;
 * the line under the button says how the send went. As the Apple TV's DiagnosticsSettingsView.
 */
@Composable
internal fun TvDiagnosticsSettings(model: AppModel) {
    val scope = rememberCoroutineScope()
    val context = LocalContext.current
    // What this TV says it takes, asked once as the page opens.
    val facts = remember { DeviceCapabilities.facts(context) }
    val screen = remember { DeviceCapabilities.screen(context) }
    val profile = remember(facts) { DeviceCapabilities.profile(facts) }
    var note by remember { mutableStateOf("") }
    var sending by remember { mutableStateOf(false) }
    var outcome by remember { mutableStateOf<String?>(null) }
    var editingNote by remember { mutableStateOf(false) }
    val focus = remember { FocusRequester() }
    TakeFocus(focus)

    fun send() {
        val client = model.client ?: return
        if (sending) return
        sending = true
        outcome = null
        scope.launch {
            outcome = try {
                val id = LogHub.sendDiagnostics(client, diagnosticsHeader(model, context), note)
                note = ""
                t("Sent to your server ({id}). Mention that if you ask for help.", "id" to id)
            } catch (e: FlexarrException) {
                when {
                    e is FlexarrException.UnexpectedResponse || (e is FlexarrException.Http && e.status in setOf(404, 405)) ->
                        t("This server doesn’t take diagnostics yet. Update Flexarr on it, then try again.")
                    else -> e.message
                }
            } finally {
                sending = false
            }
        }
    }

    TvSettingsPage(
        t("Send diagnostics"),
        summary = t("Sends what this TV has logged lately to your server, and only there, so a problem can be looked into. Sign-ins and stream addresses are taken out first. (Error reports, when your server has them on, are separate: see Server.)"),
    ) {
        item {
            TvSettingsRow(
                t("Note"), onClick = { if (!sending) editingNote = true }, modifier = Modifier.focusRequester(focus), icon = Icons.Filled.EditNote,
                value = note.ifEmpty { t("Optional") }, help = t("What happened and roughly when, so it’s easy to find in the log."),
            )
        }
        item {
            TvSettingsRow(
                if (sending) t("Sending…") else t("Send"), onClick = ::send, icon = Icons.AutoMirrored.Filled.Send,
                help = t("Send the log now, with the note."),
            )
        }
        item {
            if (sending) {
                CircularProgressIndicator(Modifier.padding(start = 16.dp, top = 10.dp).size(20.dp), color = Flexarr.screen, strokeWidth = 2.dp)
            } else {
                outcome?.let { Text(it, fontSize = 13.5.sp, color = Flexarr.usher, lineHeight = 17.5.sp, modifier = Modifier.padding(start = 16.dp, end = 16.dp, top = 10.dp)) }
            }
        }
        item { TvSettingsGroupLabel(t("What this TV reports")) }
        screen?.let { screen ->
            item {
                TvSettingsRow(
                    t("Screen"), onClick = {}, icon = Icons.Filled.Tv,
                    value = t("{width}×{height}, {rate} Hz", "width" to screen.width, "height" to screen.height, "rate" to DeviceCapabilities.rate(screen.refreshRate)),
                    help = t("Refresh rates it can switch to: {rates} Hz. Whether it switches to a film’s is the TV’s own setting, Match content frame rate.", "rates" to screen.refreshRates.joinToString { DeviceCapabilities.rate(it) }),
                )
            }
        }
        item {
            TvSettingsRow(
                t("HDR"), onClick = {}, icon = Icons.Filled.HdrOn,
                value = DeviceCapabilities.hdrNames(facts.hdrTypes).joinToString().ifEmpty { t("None") },
                help = if (profile.maxHeight >= 2160) t("This TV plays 4K files.") else t("This TV doesn’t play 4K files, so your server sends them at 1080p."),
            )
        }
        item {
            TvSettingsRow(
                t("Surround to your amplifier"), onClick = {}, icon = Icons.Filled.SurroundSound,
                value = DeviceCapabilities.surroundNames(facts.passthrough).joinToString().ifEmpty { t("None") },
                help = t("The formats what’s connected to this TV says it takes as they are. Lossless surround in Playback sends them that way."),
            )
        }
    }

    if (editingNote) {
        NoteDialog(note, onDone = {
            note = it
            editingNote = false
        }, onCancel = { editingNote = false })
    }
}

/** The note, typed with the TV's keyboard: Done keeps it, Cancel or Back leaves it as it was. */
@Composable
private fun NoteDialog(initial: String, onDone: (String) -> Unit, onCancel: () -> Unit) {
    var draft by remember { mutableStateOf(initial) }
    val field = remember { FocusRequester() }
    Dialog(onDismissRequest = onCancel) {
        Column(Modifier.width(440.dp).background(Flexarr.seat, RoundedCornerShape(Tokens.radiusPanel)).padding(24.dp), verticalArrangement = Arrangement.spacedBy(14.dp)) {
            Text(t("Note"), fontSize = 18.sp, fontWeight = FontWeight.Bold)
            TvTextField(draft, { draft = it }, t("What happened?"), Modifier.fillMaxWidth().focusRequester(field))
            Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                TvActionButton(t(Words.done), onClick = { onDone(draft) }, prominent = true)
                TvActionButton(t(Words.cancel), onClick = onCancel)
            }
        }
    }
    TakeFocus(field)
}
