package io.github.flexarr.ui.tv

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.relocation.BringIntoViewRequester
import androidx.compose.foundation.relocation.bringIntoViewRequester
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.Send
import androidx.compose.material.icons.filled.CallToAction
import androidx.compose.material.icons.filled.ClosedCaption
import androidx.compose.material.icons.filled.EditNote
import androidx.compose.material.icons.filled.FastForward
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
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.input.key.Key
import androidx.compose.ui.input.key.KeyEventType
import androidx.compose.ui.input.key.key
import androidx.compose.ui.input.key.onPreviewKeyEvent
import androidx.compose.ui.input.key.type
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.TextStyle
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
                "Subtitle size", "For this profile on every device.", SubtitleSize.choices, { it.label }, current.shownSubtitleSize,
                choose = { save(current.copy(subtitleSize = it), current) }, onDone = ::close,
            )
            return
        }
        PlaybackChoice.SubtitlePosition -> if (current != null) {
            BackHandler(onBack = ::close)
            TvSettingsChoicePage(
                "Subtitle position", "For this profile on every device.", SubtitlePosition.choices, { it.label }, current.shownSubtitlePosition,
                choose = { save(current.copy(subtitlePosition = it), current) }, onDone = ::close,
            )
            return
        }
        PlaybackChoice.Skip -> {
            BackHandler(onBack = ::close)
            TvSettingsChoicePage(
                "Skip back and forward", "A press of left or right on the remote.", PlayerSettings.skipChoices, { "$it seconds" }, skipSeconds,
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
                "Dialogue boost", null, SoundSettings.DialogueBoost.entries, { it.name }, sound.dialogueBoost,
                choose = { boost -> SoundSettings.update(context) { it.copy(dialogueBoost = boost) } }, onDone = ::close,
            )
            return
        }
        PlaybackChoice.NightMode -> {
            BackHandler(onBack = ::close)
            TvSettingsChoicePage(
                "Night mode", null, SoundSettings.NightMode.entries, ::nightModeLabel, sound.nightMode,
                choose = { mode -> SoundSettings.update(context) { it.copy(nightMode = mode) } }, onDone = ::close,
            )
            return
        }
        PlaybackChoice.NightFrom -> {
            BackHandler(onBack = ::close)
            TvSettingsChoicePage(
                "Night mode from", null, NIGHT_HOURS, SoundSettings::clock, sound.nightStart,
                choose = { at -> SoundSettings.update(context) { it.copy(nightStart = at) } }, onDone = ::close,
            )
            return
        }
        PlaybackChoice.NightUntil -> {
            BackHandler(onBack = ::close)
            TvSettingsChoicePage(
                "Night mode until", null, NIGHT_HOURS, SoundSettings::clock, sound.nightEnd,
                choose = { at -> SoundSettings.update(context) { it.copy(nightEnd = at) } }, onDone = ::close,
            )
            return
        }
        PlaybackChoice.Speakers -> {
            BackHandler(onBack = ::close)
            TvSettingsChoicePage(
                "Speakers", null, SoundSettings.Speakers.entries, { it.label }, sound.speakers,
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

    TvSettingsPage("Playback", summary = if (saveFailed) "That change couldn’t be saved on the server, so it’s been put back." else null) {
        if (current != null) {
            item { TvSettingsGroupLabel(model.currentProfile?.let { "${it.name}, on every device" } ?: "Your profile, on every device") }
            item {
                TvSettingsToggle(
                    "Auto-play next episode", current.autoPlayNext, { save(current.copy(autoPlayNext = !current.autoPlayNext), current) },
                    modifier = Modifier.focusFor(null), icon = Icons.Filled.SkipNext,
                    help = "When an episode ends, the next one starts after a five-second countdown.",
                )
            }
            item {
                TvSettingsToggle(
                    "Skip intros", current.skipIntros, { save(current.copy(skipIntros = !current.skipIntros), current) }, icon = Icons.Filled.FastForward,
                    help = "Jumps past a show’s opening titles by itself. Off, a Skip Intro button shows instead.",
                )
            }
            item {
                TvSettingsToggle(
                    "Skip recaps", current.skipRecaps, { save(current.copy(skipRecaps = !current.skipRecaps), current) }, icon = Icons.Filled.History,
                    help = "Jumps past the “Previously on” at the start of an episode.",
                )
            }
            item {
                TvSettingsToggle(
                    "Skip credits", current.skipCredits, { save(current.copy(skipCredits = !current.skipCredits), current) }, icon = Icons.Filled.Theaters,
                    help = "Goes on to the next episode when the credits start, rather than when they end.",
                )
            }
            item {
                TvSettingsRow(
                    "Subtitle size", onClick = { choosing = PlaybackChoice.SubtitleSize }, modifier = Modifier.focusFor(PlaybackChoice.SubtitleSize),
                    icon = Icons.Filled.ClosedCaption, value = current.shownSubtitleSize.label, accessory = TvSettingAccessory.Chevron,
                    help = "How big subtitles are. Subtitles that run early or late can be moved from the player’s Subtitles menu.",
                )
            }
            item {
                TvSettingsRow(
                    "Subtitle position", onClick = { choosing = PlaybackChoice.SubtitlePosition }, modifier = Modifier.focusFor(PlaybackChoice.SubtitlePosition),
                    icon = Icons.Filled.Height, value = current.shownSubtitlePosition.label, accessory = TvSettingAccessory.Chevron,
                    help = "Raise subtitles clear of text in the picture or the edge of the TV. Styled subtitles keep the places they were made for.",
                )
            }
            item {
                val inBars = current.subtitlesInBlackBars ?: false
                TvSettingsToggle(
                    "Subtitles in the black bars", inBars, { save(current.copy(subtitlesInBlackBars = !inBars), current) }, icon = Icons.Filled.CallToAction,
                    help = "In films wider than the TV, subtitles go in the black bar below the picture rather than over it.",
                )
            }
        } else if (!loaded) {
            // The server's half arrives a moment later; this keeps the rows below from jumping up and back.
            item { TvSettingsGroupLabel("Your profile, on every device") }
            item { CircularProgressIndicator(Modifier.padding(start = 16.dp, top = 10.dp, bottom = 10.dp).size(20.dp), color = Flexarr.screen, strokeWidth = 2.dp) }
        }

        item { TvSettingsGroupLabel("This TV") }
        item {
            TvSettingsRow(
                "Skip back and forward", onClick = { choosing = PlaybackChoice.Skip }, modifier = Modifier.focusFor(PlaybackChoice.Skip),
                icon = skipIcon(skipSeconds), value = "$skipSeconds seconds", accessory = TvSettingAccessory.Chevron,
                help = "How far a press of left or right on the remote moves while something plays.",
            )
        }
        item {
            TvSettingsRow(
                "Dialogue boost", onClick = { choosing = PlaybackChoice.DialogueBoost }, modifier = Modifier.focusFor(PlaybackChoice.DialogueBoost),
                icon = Icons.Filled.RecordVoiceOver, value = sound.dialogueBoost.name, accessory = TvSettingAccessory.Chevron,
                help = "Makes voices easier to follow under music and effects.",
            )
        }
        item {
            TvSettingsRow(
                "Night mode", onClick = { choosing = PlaybackChoice.NightMode }, modifier = Modifier.focusFor(PlaybackChoice.NightMode),
                icon = Icons.Filled.Bedtime, accessory = TvSettingAccessory.Chevron,
                value = if (sound.nightMode == SoundSettings.NightMode.Scheduled) sound.nightHours else nightModeLabel(sound.nightMode),
                help = "Quietens loud scenes and lifts quiet ones.",
            )
        }
        if (sound.nightMode == SoundSettings.NightMode.Scheduled) {
            item {
                TvSettingsRow(
                    "Night mode from", onClick = { choosing = PlaybackChoice.NightFrom }, modifier = Modifier.focusFor(PlaybackChoice.NightFrom),
                    icon = Icons.Filled.NightsStay, value = SoundSettings.clock(sound.nightStart), accessory = TvSettingAccessory.Chevron,
                )
            }
            item {
                TvSettingsRow(
                    "Night mode until", onClick = { choosing = PlaybackChoice.NightUntil }, modifier = Modifier.focusFor(PlaybackChoice.NightUntil),
                    icon = Icons.Filled.WbTwilight, value = SoundSettings.clock(sound.nightEnd), accessory = TvSettingAccessory.Chevron,
                )
            }
        }
        item {
            TvSettingsRow(
                "Speakers", onClick = { choosing = PlaybackChoice.Speakers }, modifier = Modifier.focusFor(PlaybackChoice.Speakers),
                icon = Icons.Filled.Speaker, value = sound.speakers.label, accessory = TvSettingAccessory.Chevron,
                help = "Auto plays every channel what’s connected takes.",
            )
        }
    }
}

private fun nightModeLabel(mode: SoundSettings.NightMode) = if (mode == SoundSettings.NightMode.Scheduled) "On a schedule" else mode.name

/** Night mode's hours, on the hour. */
private val NIGHT_HOURS = (0 until 24).map { it * 60 }

private fun skipIcon(seconds: Int): ImageVector = when (seconds) {
    10 -> Icons.Filled.Forward10
    30 -> Icons.Filled.Forward30
    else -> Icons.Filled.FastForward
}

/** What a film or TV library's tab opens on, for every library on this TV. As the Apple TV's LibrarySettingsView. */
@Composable
internal fun TvLibrarySettings() {
    val context = LocalContext.current
    val opensOn = TvLibraryOpening.saved(context)
    val focus = remember { FocusRequester() }
    TakeFocus(focus)
    TvSettingsPage("Libraries", summary = null) {
        item { TvSettingsGroupLabel("Open on") }
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
        TvLibraryOpening.Browse -> "A library opens on its highlights: what you’re watching in it, the latest, the best not yet watched and its biggest genres."
        TvLibraryOpening.All -> "A library opens on every title, A to Z, with sorting, genres and a letter rail to jump through it."
        TvLibraryOpening.Genres -> "A film or TV library opens on its genres, each with a picture from one of its titles. Home videos open on Browse."
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
                    problem = "Couldn’t save the order. ${e.message}"
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
        "Sidebar",
        summary = problem ?: "Home is always first, and Discover, Search and Settings last. Your other devices list your libraries in this order too.",
    ) {
        item { TvSettingsGroupLabel("Between Home and Search") }
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
                value = if (picked) "Move up or down" else null,
                help = if (picked) "Press up or down to move ${entry.title}, then press OK to put it down." else "Press OK to pick up ${entry.title}, then press up or down to move it.",
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

    TvSettingsPage(
        "Server",
        summary = if (remote == null) {
            "To watch away from home, turn on remote access with Tailscale in Settings › Access on the web. This TV picks the address up by itself."
        } else {
            "Away from home, this TV reaches the server over Tailscale, which needs to be connected on it."
        },
    ) {
        item { TvSettingsGroupLabel("Connection") }
        item { TvSettingsFact("Address", shortAddress(model.credentials?.address)) }
        if (remote != null) {
            item { TvSettingsFact("Away from home", shortAddress(remote)) }
            item { TvSettingsFact("Connected", if (client.addresses.isAway) "Away, over Tailscale" else "At home") }
        }

        item { TvSettingsGroupLabel("Server") }
        when (val state = status.state) {
            is Load.Loaded -> {
                item { TvSettingsFact("Version", ServerVersion.short(state.value.version)) }
                item { TvSettingsFact("Running on", state.value.osDescription) }
                state.value.startTime?.let(::upSince)?.let { item { TvSettingsFact("Up since", it) } }
            }
            is Load.Failed -> item { TvSettingsFact("Status", state.message) }
            Load.Loading -> item { TvSettingsFact("Status", "Checking") }
        }
        item { TvSettingsFact("Libraries", (libraries.state as? Load.Loaded)?.value?.joinToString(", ") { it.name }.orEmpty()) }

        item { TvSettingsGroupLabel("Error reports") }
        item {
            // As the server says now, or as this TV last heard while it can't be asked.
            val reportsOn = (status.state as? Load.Loaded)?.value?.let { it.errorReports?.enabled == true } ?: ErrorReports.isOn
            TvSettingsFact("Error reports", if (reportsOn) "On" else "Off")
        }
        item { TvSettingsNote(ErrorReports.PRIVACY_NOTE) }

        // The first control here, so focus has somewhere to land; it also tells whether the server's still up. While
        // it's busy it ignores a press rather than turning off, which on a TV would take focus away from it.
        item {
            TvSettingsRow(
                if (checking) "Checking…" else "Check again", onClick = { if (!checking) status.retry() },
                modifier = Modifier.padding(top = 15.dp).then(if (onDiagnosticsRow) Modifier else Modifier.focusRequester(focus)),
                icon = Icons.Filled.Refresh, help = "Ask the server how it is now.",
            )
        }
        item {
            TvSettingsRow(
                "Send diagnostics", onClick = onDiagnostics, modifier = if (onDiagnosticsRow) Modifier.focusRequester(focus) else Modifier,
                icon = Icons.Filled.MedicalServices, accessory = TvSettingAccessory.Chevron,
                help = "Send this TV’s recent log to your server, to look into a problem.",
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
                val id = LogHub.sendDiagnostics(client, diagnosticsHeader(model), note)
                note = ""
                "Sent to your server ($id). Mention that if you ask for help."
            } catch (e: FlexarrException) {
                when {
                    e is FlexarrException.UnexpectedResponse || (e is FlexarrException.Http && e.status in setOf(404, 405)) ->
                        "This server doesn’t take diagnostics yet. Update Flexarr on it, then try again."
                    else -> e.message
                }
            } finally {
                sending = false
            }
        }
    }

    TvSettingsPage(
        "Send diagnostics",
        summary = "Sends what this TV has logged lately to your server, and only there, so a problem can be looked into. Sign-ins and stream addresses are taken out first. (Error reports, when your server has them on, are separate: see Server.)",
    ) {
        item {
            TvSettingsRow(
                "Note", onClick = { if (!sending) editingNote = true }, modifier = Modifier.focusRequester(focus), icon = Icons.Filled.EditNote,
                value = note.ifEmpty { "Optional" }, help = "What happened and roughly when, so it’s easy to find in the log.",
            )
        }
        item {
            TvSettingsRow(
                if (sending) "Sending…" else "Send", onClick = ::send, icon = Icons.AutoMirrored.Filled.Send,
                help = "Send the log now, with the note.",
            )
        }
        item {
            if (sending) {
                CircularProgressIndicator(Modifier.padding(start = 16.dp, top = 10.dp).size(20.dp), color = Flexarr.screen, strokeWidth = 2.dp)
            } else {
                outcome?.let { Text(it, fontSize = 13.5.sp, color = Flexarr.usher, lineHeight = 17.5.sp, modifier = Modifier.padding(start = 16.dp, end = 16.dp, top = 10.dp)) }
            }
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
        Column(Modifier.width(440.dp).background(Flexarr.seat, RoundedCornerShape(14.dp)).padding(24.dp), verticalArrangement = Arrangement.spacedBy(14.dp)) {
            Text("Note", fontSize = 18.sp, fontWeight = FontWeight.Bold)
            BasicTextField(
                value = draft,
                onValueChange = { draft = it },
                singleLine = true,
                textStyle = TextStyle(fontSize = 15.sp, color = Flexarr.screen),
                cursorBrush = SolidColor(Flexarr.screen),
                modifier = Modifier.fillMaxWidth().focusRequester(field)
                    .background(Flexarr.screen.copy(alpha = 0.08f), RoundedCornerShape(8.dp)).heightIn(min = 38.dp).padding(horizontal = 14.dp, vertical = 10.dp),
                decorationBox = { inner ->
                    if (draft.isEmpty()) Text("What happened?", fontSize = 15.sp, color = Flexarr.usher)
                    inner()
                },
            )
            Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                TvActionButton("Done", onClick = { onDone(draft) }, prominent = true)
                TvActionButton("Cancel", onClick = onCancel)
            }
        }
    }
    TakeFocus(field)
}
