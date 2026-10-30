package io.github.flexarr.ui.tv

import androidx.compose.animation.Crossfade
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyListScope
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.KeyboardArrowRight
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.DragHandle
import androidx.compose.material.icons.filled.SwapVert
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.focus.onFocusChanged
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.tv.material3.ClickableSurfaceDefaults
import androidx.tv.material3.Icon
import androidx.tv.material3.Surface
import androidx.tv.material3.Text
import io.github.flexarr.AppModel
import io.github.flexarr.core.Profile
import io.github.flexarr.ui.profiles.ProfileAvatar
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.RoomBackground
import kotlinx.coroutines.delay

// The Apple TV's Settings pieces (FlexarrTV/Screens/Settings/SettingsComponents.swift), halved.

/** What the left column says about the focused row: an optional icon in a tile, and a sentence or two. */
internal data class TvSettingHelp(val icon: ImageVector?, val text: String)

/** Set by the page: a row calls it as it gains focus, to put its explanation in the left column. */
private val LocalShowSettingHelp = staticCompositionLocalOf<(TvSettingHelp?) -> Unit> { {} }

/**
 * A Settings page laid out the way the Apple TV's own Settings are: the page's name and, under it, what the focused
 * row does on the left; the rows on the right, so focus only ever moves up and down. [summary] shows on the left
 * until a row with help has focus.
 */
@Composable
internal fun TvSettingsPage(title: String, summary: String?, rows: LazyListScope.() -> Unit) {
    var focusedHelp by remember { mutableStateOf<TvSettingHelp?>(null) }
    RoomBackground(ambient = null) {
        Row(Modifier.fillMaxSize().padding(start = TvLayout.Edge + 10.dp, end = TvLayout.Edge, top = 30.dp)) {
            Column(Modifier.width(280.dp), verticalArrangement = Arrangement.spacedBy(17.dp)) {
                Text(title, fontSize = 29.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen)
                Crossfade(focusedHelp ?: summary?.let { TvSettingHelp(null, it) }, animationSpec = tween(200), label = "help") { help ->
                    SettingHelpPanel(help)
                }
            }
            Spacer(Modifier.width(35.dp))
            CompositionLocalProvider(LocalShowSettingHelp provides { focusedHelp = it }) {
                // Room at the sides, so the focused row's lift isn't cut off at the column's edges.
                LazyColumn(
                    Modifier.weight(1f).fillMaxSize(),
                    contentPadding = PaddingValues(start = 10.dp, end = 10.dp, top = 12.dp, bottom = 40.dp),
                    verticalArrangement = Arrangement.spacedBy(5.dp),
                    content = rows,
                )
            }
        }
    }
}

@Composable
private fun SettingHelpPanel(help: TvSettingHelp?) {
    Column(verticalArrangement = Arrangement.spacedBy(15.dp)) {
        help?.icon?.let { icon ->
            Box(Modifier.size(84.dp).background(Flexarr.aisle, RoundedCornerShape(18.dp)), contentAlignment = Alignment.Center) {
                Icon(icon, contentDescription = null, tint = Flexarr.screen, modifier = Modifier.size(38.dp))
            }
        }
        help?.text?.takeIf { it.isNotEmpty() }?.let {
            Text(it, fontSize = 13.5.sp, color = Flexarr.usher, lineHeight = 17.5.sp)
        }
    }
}

/** A group's name over its rows: small, dim and sentence case, as on the web and iPhone. */
@Composable
internal fun TvSettingsGroupLabel(title: String, modifier: Modifier = Modifier) {
    Text(title, fontSize = 12.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.usher, modifier = modifier.padding(start = 16.dp, top = 13.dp, bottom = 2.dp))
}

/** What sits at a row's right end. */
internal enum class TvSettingAccessory {
    None,

    /** Opens a page. */
    Chevron,

    /** The choice that's made, on a page of choices. */
    Checkmark,

    /** Can be picked up and moved, in a list to put in order. */
    Handle,

    /** Picked up: up and down move it. */
    Moving,
}

/**
 * One row: its name on the left, how it's set now on the right. Every kind of setting is this row, so a switch, a
 * choice and a page all look and move alike; pressing a switch's row flips it, as on the Apple TV's own Settings.
 * [profile]: their picture in the icon's place, for a row about a profile.
 */
@Composable
internal fun TvSettingsRow(
    title: String,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    icon: ImageVector? = null,
    profile: Pair<AppModel, Profile>? = null,
    value: String? = null,
    help: String? = null,
    accessory: TvSettingAccessory = TvSettingAccessory.None,
    destructive: Boolean = false,
) {
    val showHelp = LocalShowSettingHelp.current
    val rowHelp = help?.let { TvSettingHelp(icon, it) }
    var focused by remember { mutableStateOf(false) }
    // A row whose help changes while it has focus (a row picked up in Sidebar) says the new one.
    LaunchedEffect(rowHelp, focused) { if (focused) showHelp(rowHelp) }
    Surface(
        onClick = onClick,
        modifier = modifier.fillMaxWidth().heightIn(min = 39.dp).onFocusChanged { focused = it.isFocused },
        shape = ClickableSurfaceDefaults.shape(RoundedCornerShape(8.dp)),
        colors = ClickableSurfaceDefaults.colors(
            containerColor = Flexarr.screen.copy(alpha = 0.07f),
            contentColor = if (destructive) Flexarr.danger else Flexarr.screen,
            focusedContainerColor = Flexarr.screen,
            focusedContentColor = Flexarr.room,
            pressedContainerColor = Flexarr.screen,
            pressedContentColor = Flexarr.room,
        ),
        scale = ClickableSurfaceDefaults.scale(focusedScale = 1.03f, pressedScale = 0.98f),
    ) {
        Row(
            Modifier.fillMaxWidth().heightIn(min = 39.dp).padding(horizontal = 16.dp),
            horizontalArrangement = Arrangement.spacedBy(11.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            when {
                profile != null -> ProfileAvatar(profile.first, profile.second, size = SETTINGS_AVATAR)
                icon != null -> Box(Modifier.width(20.dp), contentAlignment = Alignment.Center) {
                    Icon(icon, contentDescription = null, modifier = Modifier.size(15.dp))
                }
            }
            Text(title, fontSize = 14.5.sp, fontWeight = FontWeight.Medium, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f))
            value?.let { Text(it, fontSize = 14.5.sp, fontWeight = FontWeight.Medium, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.alpha(0.7f)) }
            when (accessory) {
                TvSettingAccessory.None -> {}
                TvSettingAccessory.Chevron -> Icon(Icons.AutoMirrored.Filled.KeyboardArrowRight, contentDescription = null, modifier = Modifier.size(16.dp).alpha(0.55f))
                TvSettingAccessory.Checkmark -> Icon(Icons.Filled.Check, contentDescription = "Chosen", modifier = Modifier.size(15.dp))
                TvSettingAccessory.Handle -> Icon(Icons.Filled.DragHandle, contentDescription = null, modifier = Modifier.size(15.dp).alpha(0.55f))
                TvSettingAccessory.Moving -> Icon(Icons.Filled.SwapVert, contentDescription = null, modifier = Modifier.size(15.dp))
            }
        }
    }
}

/** A switch: the row says On or Off and a press flips it. */
@Composable
internal fun TvSettingsToggle(title: String, isOn: Boolean, flip: () -> Unit, modifier: Modifier = Modifier, icon: ImageVector? = null, help: String? = null) {
    TvSettingsRow(title, onClick = flip, modifier = modifier, icon = icon, value = if (isOn) "On" else "Off", help = help)
}

/** A fact on a page, not a control: the same band as a row, without focus. [profile]: their picture before the label. */
@Composable
internal fun TvSettingsFact(label: String, value: String, profile: Pair<AppModel, Profile>? = null) {
    Row(
        Modifier.fillMaxWidth().heightIn(min = 37.dp).padding(horizontal = 16.dp),
        horizontalArrangement = Arrangement.spacedBy(15.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        profile?.let { ProfileAvatar(it.first, it.second, size = SETTINGS_AVATAR) }
        Text(label, fontSize = 14.5.sp, fontWeight = FontWeight.Medium, color = Flexarr.usher)
        Spacer(Modifier.weight(1f))
        Text(value, fontSize = 14.5.sp, fontWeight = FontWeight.Medium, color = Flexarr.screen, maxLines = 1, overflow = TextOverflow.Ellipsis)
    }
}

/** A page's note in plain words, under its rows. */
@Composable
internal fun TvSettingsNote(text: String, modifier: Modifier = Modifier) {
    Text(text, fontSize = 12.sp, color = Flexarr.usher, lineHeight = 16.sp, modifier = modifier.padding(horizontal = 16.dp))
}

/**
 * A page of choices, for a setting with more than two: the chosen one has a tick, and choosing goes back ([choose],
 * then [onDone]). It opens on what's chosen now, so a press of OK leaves it as it was.
 */
@Composable
internal fun <T> TvSettingsChoicePage(title: String, summary: String?, choices: List<T>, label: (T) -> String, selection: T, choose: (T) -> Unit, onDone: () -> Unit) {
    val focus = remember { FocusRequester() }
    TakeFocus(focus)
    TvSettingsPage(title, summary) {
        choices.forEach { choice ->
            item {
                TvSettingsRow(
                    label(choice),
                    onClick = {
                        if (choice != selection) choose(choice)
                        onDone()
                    },
                    modifier = if (choice == selection) Modifier.focusRequester(focus) else Modifier,
                    accessory = if (choice == selection) TvSettingAccessory.Checkmark else TvSettingAccessory.None,
                )
            }
        }
    }
}

/**
 * Focus to [requester] as a page appears (or [key] changes), asking again for a moment: the first request can come
 * before a list has placed the row.
 */
@Composable
internal fun TakeFocus(requester: FocusRequester, key: Any? = Unit, ready: Boolean = true) {
    LaunchedEffect(key, ready) {
        if (!ready) return@LaunchedEffect
        repeat(20) {
            if (runCatching { requester.requestFocus() }.getOrDefault(false)) return@LaunchedEffect
            delay(50)
        }
    }
}

/** A profile's picture on a row: a little larger than an icon, still within the row's height. */
private const val SETTINGS_AVATAR = 26
