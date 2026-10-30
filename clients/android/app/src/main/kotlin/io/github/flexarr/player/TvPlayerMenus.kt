package io.github.flexarr.player

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.KeyboardArrowRight
import androidx.compose.material.icons.filled.Bedtime
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.ClosedCaption
import androidx.compose.material.icons.filled.GraphicEq
import androidx.compose.material.icons.filled.Speed
import androidx.compose.material.icons.outlined.Bedtime
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.tv.material3.ClickableSurfaceDefaults
import androidx.tv.material3.Icon
import androidx.tv.material3.Surface
import androidx.tv.material3.Text
import io.github.flexarr.core.SubtitlePosition
import io.github.flexarr.core.SubtitleSize
import io.github.flexarr.core.Teletext
import io.github.flexarr.data.SoundSettings
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.tv.TakeFocus
import io.github.flexarr.ui.tv.TvActionButton

// The player's menus for the remote, as the Apple TV's: a button for each beneath the timeline, opening a panel down
// the right of the picture, with a page for each submenu. The same choices as the phone's dropdowns (PlayerMenus.kt).

internal enum class TvMenu(val title: String) { Audio("Audio"), Subtitles("Subtitles"), Speed("Speed"), Sleep("Sleep timer") }

/** The menus' buttons; [menus] are the ones this player has (a live channel has only Sleep). */
@Composable
internal fun TvMenuButtons(controller: PlaybackController?, sleep: VideoSleep, menus: List<TvMenu>, requesters: Map<TvMenu, FocusRequester>, onOpen: (TvMenu) -> Unit) {
    Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
        menus.forEach { menu ->
            val icon = when (menu) {
                TvMenu.Audio -> Icons.Filled.GraphicEq
                TvMenu.Subtitles -> Icons.Filled.ClosedCaption
                TvMenu.Speed -> Icons.Filled.Speed
                TvMenu.Sleep -> if (sleep.setting != null) Icons.Filled.Bedtime else Icons.Outlined.Bedtime
            }
            val label = when (menu) {
                TvMenu.Speed -> controller?.rate?.takeIf { it != 1f }?.let(PlaybackController::rateLabel) ?: menu.title
                TvMenu.Sleep -> sleep.setting?.label ?: menu.title
                else -> menu.title
            }
            TvActionButton(label, onClick = { onOpen(menu) }, icon = icon, modifier = Modifier.focusRequester(requesters.getValue(menu)))
        }
    }
}

/** Which menus a film or episode has: Subtitles only when there are some, or some to search for. */
internal fun PlaybackController.tvMenus(): List<TvMenu> {
    val info = info ?: return emptyList()
    return listOfNotNull(TvMenu.Audio, TvMenu.Subtitles.takeIf { info.subtitles.isNotEmpty() || canSearchSubtitles }, TvMenu.Speed, TvMenu.Sleep)
}

/** One row of a menu's page. */
private data class PanelItem(
    val label: String,
    val value: String? = null,
    val checked: Boolean = false,
    val opens: Boolean = false,
    val enabled: Boolean = true,
    /** A little space before it, between groups (tracks, then settings). */
    val gapBefore: Boolean = false,
    val onClick: () -> Unit,
)

/**
 * A menu's panel down the right of the picture, opening on what's chosen. Choosing something that changes the
 * picture (a track, a speed) closes it; a setting (Size, Night mode) stays so the next can be tried. Back goes up a
 * page, then closes it.
 */
@Composable
internal fun TvMenuPanel(
    menu: TvMenu,
    controller: PlaybackController?,
    sleep: VideoSleep,
    offersEndOfEpisode: Boolean,
    onAudioSync: () -> Unit,
    onClose: () -> Unit,
    modifier: Modifier = Modifier,
) {
    var page by remember(menu) { mutableStateOf<String?>(null) }
    BackHandler { if (page != null) page = null else onClose() }
    val rows = panelItems(menu, page, controller, sleep, offersEndOfEpisode, openPage = { page = it }, onAudioSync, onClose)
    val focusIndex = rows.indexOfFirst { it.checked }.coerceAtLeast(0)
    val focus = remember(menu, page) { FocusRequester() }
    TakeFocus(focus, key = page)
    Column(
        modifier.fillMaxHeight().width(340.dp).padding(20.dp).glass(RoundedCornerShape(22.dp)).padding(vertical = 18.dp),
        verticalArrangement = Arrangement.spacedBy(10.dp),
    ) {
        Text(pageTitle(menu, page), fontSize = 17.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen, modifier = Modifier.padding(horizontal = 20.dp))
        LazyColumn(contentPadding = PaddingValues(horizontal = 12.dp, vertical = 4.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
            itemsIndexed(rows) { index, row ->
                PanelRow(
                    row.label, value = row.value, checked = row.checked, opens = row.opens, enabled = row.enabled,
                    modifier = Modifier.padding(top = if (row.gapBefore) 10.dp else 0.dp).then(if (index == focusIndex) Modifier.focusRequester(focus) else Modifier),
                    onClick = row.onClick,
                )
            }
        }
    }
}

private fun pageTitle(menu: TvMenu, page: String?): String = when (page) {
    "dialogue" -> "Dialogue boost"
    "night" -> "Night mode"
    "speakers" -> "Speakers"
    "timing" -> "Timing"
    "teletext" -> "Teletext page"
    "size" -> "Size"
    "position" -> "Position"
    else -> menu.title
}

private fun nightLabel(sound: SoundSettings, mode: SoundSettings.NightMode = sound.nightMode) =
    if (mode == SoundSettings.NightMode.Scheduled) sound.nightHours else mode.name

/** The rows of [menu]'s [page] (null: its first page), as the phone's menus have them. */
private fun panelItems(
    menu: TvMenu,
    page: String?,
    controller: PlaybackController?,
    sleep: VideoSleep,
    offersEndOfEpisode: Boolean,
    openPage: (String?) -> Unit,
    onAudioSync: () -> Unit,
    close: () -> Unit,
): List<PanelItem> = buildList {
    if (menu == TvMenu.Sleep) {
        add(PanelItem("Off", checked = sleep.setting == null) { sleep.set(null); close() })
        val choices = SleepTimer.minuteChoices.map { SleepTimer.Setting.Minutes(it) } +
            if (offersEndOfEpisode) listOf(SleepTimer.Setting.EndOfEpisode) else emptyList()
        choices.forEachIndexed { i, setting -> add(PanelItem(setting.label, checked = setting == sleep.setting, gapBefore = i == 0) { sleep.set(setting); close() }) }
        return@buildList
    }
    val controller = controller ?: return@buildList
    val info = controller.info ?: return@buildList
    val sound = controller.sound
    val reopening = controller.isReopening
    when (menu) {
        TvMenu.Audio -> when (page) {
            "dialogue" -> SoundSettings.DialogueBoost.entries.forEach { boost ->
                add(PanelItem(boost.name, checked = boost == sound.dialogueBoost) { controller.changeSound { it.copy(dialogueBoost = boost) } })
            }
            "night" -> SoundSettings.NightMode.entries.forEach { mode ->
                add(PanelItem(nightLabel(sound, mode), checked = mode == sound.nightMode) { controller.changeSound { it.copy(nightMode = mode) } })
            }
            "speakers" -> SoundSettings.Speakers.entries.forEach { speakers ->
                add(PanelItem(speakers.label, checked = speakers == sound.speakers) { controller.changeSound { it.copy(speakers = speakers) } })
            }
            else -> {
                if (info.audioTracks.size > 1) {
                    info.audioTracks.forEach { track ->
                        add(PanelItem(track.label, checked = track.index == info.selectedAudioIndex, enabled = !reopening) { close(); controller.selectAudio(track.index) })
                    }
                }
                controller.audioSync?.let { sync ->
                    add(PanelItem("Audio sync", value = if (sync == 0.0) "As the file is" else PlaybackController.syncLabel(sync), gapBefore = isNotEmpty()) { close(); onAudioSync() })
                }
                add(PanelItem("Dialogue boost", value = sound.dialogueBoost.name, opens = true, gapBefore = true) { openPage("dialogue") })
                add(PanelItem("Night mode", value = nightLabel(sound), opens = true) { openPage("night") })
                add(PanelItem("Speakers", value = sound.speakers.label, opens = true) { openPage("speakers") })
            }
        }
        TvMenu.Subtitles -> when (page) {
            "timing" -> {
                val current = info.subtitleDelaySeconds ?: 0.0
                (PlaybackController.delayChoices + current).distinct().sorted().forEach { seconds ->
                    add(PanelItem(PlaybackController.delayLabel(seconds), checked = seconds == current, enabled = !reopening) { close(); controller.setSubtitleDelay(seconds) })
                }
            }
            "teletext" -> (listOf(null) + Teletext.pages).forEach { number ->
                add(PanelItem(number?.toString() ?: "Automatic", checked = number == controller.teletextPage, enabled = !reopening) { close(); controller.chooseTeletextPage(number) })
            }
            "size" -> SubtitleSize.choices.forEach { size ->
                add(PanelItem(size.label, checked = size == controller.subtitleSize) { controller.chooseSubtitleSize(size) })
            }
            "position" -> {
                SubtitlePosition.choices.forEach { position ->
                    add(PanelItem(position.label, checked = position == controller.subtitlePosition) { controller.setSubtitlePlacement(position, controller.subtitlesInBlackBars) })
                }
                add(PanelItem("In the black bars", value = if (controller.subtitlesInBlackBars) "On" else "Off", gapBefore = true) {
                    controller.setSubtitlePlacement(controller.subtitlePosition, !controller.subtitlesInBlackBars)
                })
            }
            else -> {
                add(PanelItem("Off", checked = controller.subtitleKey == null, enabled = !reopening) { close(); controller.selectSubtitle(null) })
                info.subtitles.forEach { track ->
                    add(PanelItem(track.label, checked = track.key == controller.subtitleKey, enabled = !reopening) { close(); controller.selectSubtitle(track) })
                }
                // Teletext subtitles are on a page of their own, which a recording doesn't always mark.
                if (controller.canChooseTeletextPage) {
                    add(PanelItem("Teletext page", value = controller.teletextPage?.toString() ?: "Automatic", opens = true, gapBefore = true) { openPage("teletext") })
                }
                // Burned into the picture, they can't be moved: the server draws them where they fall.
                if (!info.subtitleBurnedIn) {
                    add(PanelItem("Timing", value = PlaybackController.delayLabel(info.subtitleDelaySeconds ?: 0.0), opens = true, gapBefore = size == info.subtitles.size + 1) { openPage("timing") })
                }
                add(PanelItem("Size", value = controller.subtitleSize.label, opens = true, gapBefore = size == info.subtitles.size + 1) { openPage("size") })
                add(PanelItem("Position", value = controller.subtitlePosition.label, opens = true) { openPage("position") })
                if (controller.canSearchSubtitles) {
                    add(PanelItem("Search for subtitles…", gapBefore = true) { close(); controller.searchSubtitles() })
                    val resyncable = controller.resyncableSubtitle
                    when {
                        controller.isResyncing -> add(PanelItem("Re-syncing…", enabled = false) {})
                        resyncable != null -> add(PanelItem("Re-sync to audio") { close(); controller.resyncSubtitles(resyncable) })
                    }
                }
            }
        }
        TvMenu.Speed -> (PlaybackController.rateChoices + controller.rate).distinct().sorted().filter { it <= PlaybackController.MAX_RATE }.forEach { rate ->
            add(PanelItem(PlaybackController.rateLabel(rate), checked = rate == controller.rate) { close(); controller.chooseRate(rate) })
        }
        TvMenu.Sleep -> Unit
    }
}

@Composable
private fun PanelRow(
    label: String,
    modifier: Modifier = Modifier,
    value: String? = null,
    checked: Boolean = false,
    opens: Boolean = false,
    enabled: Boolean = true,
    onClick: () -> Unit,
) {
    Surface(
        // Kept focusable when it can't be chosen (a track while the title reopens), so focus never lands on nothing.
        onClick = { if (enabled) onClick() },
        modifier = modifier.fillMaxWidth().heightIn(min = 40.dp),
        shape = ClickableSurfaceDefaults.shape(RoundedCornerShape(10.dp)),
        colors = ClickableSurfaceDefaults.colors(
            containerColor = Flexarr.screen.copy(alpha = 0.07f),
            contentColor = Flexarr.screen,
            focusedContainerColor = Flexarr.screen,
            focusedContentColor = Flexarr.room,
        ),
        scale = ClickableSurfaceDefaults.scale(focusedScale = 1.02f),
    ) {
        Row(
            Modifier.fillMaxWidth().heightIn(min = 40.dp).padding(horizontal = 14.dp).alpha(if (enabled) 1f else 0.5f),
            horizontalArrangement = Arrangement.spacedBy(10.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Box(Modifier.size(16.dp)) { if (checked) Icon(Icons.Filled.Check, contentDescription = "Chosen", modifier = Modifier.size(16.dp)) }
            Text(label, fontSize = 14.sp, fontWeight = FontWeight.Medium, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f))
            value?.let { Text(it, fontSize = 13.sp, maxLines = 1, modifier = Modifier.alpha(0.7f)) }
            if (opens) Icon(Icons.AutoMirrored.Filled.KeyboardArrowRight, contentDescription = null, modifier = Modifier.size(16.dp).alpha(0.6f))
        }
    }
}
