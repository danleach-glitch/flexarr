package io.github.flexarr.ui.tv

import androidx.compose.animation.core.animateDpAsState
import androidx.compose.animation.core.spring
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsFocusedAsState
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.WifiOff
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.semantics.disabled
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Shape
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.tv.material3.Border
import androidx.tv.material3.ClickableSurfaceDefaults
import androidx.tv.material3.Glow
import androidx.tv.material3.Icon
import androidx.tv.material3.LocalContentColor
import androidx.tv.material3.MaterialTheme
import androidx.tv.material3.Surface
import androidx.tv.material3.Text
import androidx.tv.material3.darkColorScheme
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.marquee

// The Apple TV app's design (FlexarrTV/Design) on Android TV. tvOS lays out a 1920-point screen and Android TV a
// 960 dp one, so every size here is the Apple TV's halved.

object TvLayout {
    /** The shared left edge for heroes, rows and grids (the title-safe inset). */
    val Edge = 40.dp
    val PosterWidth = 125.dp
    val WideWidth = 250.dp
    val RowSpacing = 22.dp
}

/** Compose for TV's colours as the app's, and the screen colour for text that doesn't say otherwise. */
@Composable
fun TvTheme(content: @Composable () -> Unit) {
    MaterialTheme(
        colorScheme = darkColorScheme(
            primary = Flexarr.screen,
            onPrimary = Flexarr.room,
            background = Flexarr.room,
            onBackground = Flexarr.screen,
            surface = Flexarr.room,
            onSurface = Flexarr.screen,
            surfaceVariant = Flexarr.seat,
            onSurfaceVariant = Flexarr.usher,
            border = Flexarr.aisle,
        ),
    ) {
        CompositionLocalProvider(LocalContentColor provides Flexarr.screen, content = content)
    }
}

/**
 * Play, More info and the other actions. Unfocused they sit quietly on a translucent seat; focused they light up in
 * projected white with an ambient glow, as a first-party Apple TV button does.
 */
@Composable
fun TvActionButton(
    label: String?,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    icon: ImageVector? = null,
    prominent: Boolean = false,
    ambient: Color = Flexarr.silver,
    enabled: Boolean = true,
    contentDescription: String? = null,
) {
    // Turned off, it stays focusable and only looks it, ignoring presses: a button that went dark while focused (Next
    // at the last track, Connect while connecting) would otherwise leave focus on nothing.
    Surface(
        onClick = { if (enabled) onClick() },
        modifier = modifier.height(38.dp).semantics { if (!enabled) disabled() },
        shape = ClickableSurfaceDefaults.shape(RoundedCornerShape(7.dp)),
        colors = ClickableSurfaceDefaults.colors(
            containerColor = if (!enabled) Flexarr.screen.copy(alpha = 0.08f) else if (prominent) ambient.copy(alpha = 0.42f) else Flexarr.screen.copy(alpha = 0.14f),
            contentColor = if (enabled) Flexarr.screen else Flexarr.usher,
            focusedContainerColor = if (enabled) Flexarr.screen else Flexarr.screen.copy(alpha = 0.35f),
            focusedContentColor = Flexarr.room,
            pressedContainerColor = if (enabled) Flexarr.screen else Flexarr.screen.copy(alpha = 0.35f),
            pressedContentColor = Flexarr.room,
        ),
        scale = ClickableSurfaceDefaults.scale(focusedScale = 1.07f, pressedScale = 0.97f),
        glow = ClickableSurfaceDefaults.glow(focusedGlow = Glow(ambient.copy(alpha = 0.65f), 14.dp)),
    ) {
        Row(
            Modifier.fillMaxHeight().padding(horizontal = if (label == null) 13.dp else 18.dp),
            horizontalArrangement = Arrangement.spacedBy(7.dp, Alignment.CenterHorizontally),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            if (icon != null) Icon(icon, contentDescription = if (label == null) contentDescription else null, modifier = Modifier.size(15.dp))
            if (label != null) Text(label, fontSize = 14.5.sp, fontWeight = FontWeight.SemiBold, maxLines = 1)
        }
    }
}

/** A small selectable pill for seasons: the choice stays lit while focus moves elsewhere, so it's visible. */
@Composable
fun TvPill(label: String, selected: Boolean, onClick: () -> Unit, modifier: Modifier = Modifier, ambient: Color = Flexarr.silver) {
    Surface(
        onClick = onClick,
        modifier = modifier.height(31.dp),
        shape = ClickableSurfaceDefaults.shape(CircleShape),
        colors = ClickableSurfaceDefaults.colors(
            containerColor = if (selected) ambient.copy(alpha = 0.32f) else Color.Transparent,
            contentColor = if (selected) Flexarr.screen else Flexarr.usher,
            focusedContainerColor = Flexarr.screen,
            focusedContentColor = Flexarr.room,
        ),
        border = ClickableSurfaceDefaults.border(
            border = if (selected) Border.None else Border(BorderStroke(1.dp, Flexarr.aisle), shape = CircleShape),
            focusedBorder = Border.None,
        ),
        scale = ClickableSurfaceDefaults.scale(focusedScale = 1.06f),
        glow = ClickableSurfaceDefaults.glow(focusedGlow = Glow(ambient.copy(alpha = 0.55f), 11.dp)),
    ) {
        Box(Modifier.padding(horizontal = 15.dp).align(Alignment.Center)) {
            Text(label, fontSize = 13.sp, fontWeight = if (selected) FontWeight.Bold else FontWeight.Medium, maxLines = 1)
        }
    }
}

/**
 * Artwork that lifts when focused, with the title's ambient glow behind it, and its caption beneath brightening and
 * moving down out of the way (the Apple TV's card button). A long press of OK runs [onLongClick] (the card's menu).
 */
@Composable
fun TvArtworkButton(
    ambient: Color,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    onLongClick: (() -> Unit)? = null,
    shape: Shape = RoundedCornerShape(4.dp),
    artwork: @Composable () -> Unit,
    caption: @Composable (focused: Boolean) -> Unit = {},
) {
    val source = remember { MutableInteractionSource() }
    val focused by source.collectIsFocusedAsState()
    val drop by animateDpAsState(if (focused) 7.dp else 0.dp, spring(dampingRatio = 0.8f, stiffness = 500f), label = "caption")
    Column(modifier, verticalArrangement = Arrangement.spacedBy(9.dp)) {
        Surface(
            onClick = onClick,
            onLongClick = onLongClick,
            interactionSource = source,
            shape = ClickableSurfaceDefaults.shape(shape),
            colors = ClickableSurfaceDefaults.colors(containerColor = Flexarr.seat, focusedContainerColor = Flexarr.seat, pressedContainerColor = Flexarr.seat),
            scale = ClickableSurfaceDefaults.scale(focusedScale = 1.08f, pressedScale = 1.02f),
            glow = ClickableSurfaceDefaults.glow(focusedGlow = Glow(ambient.copy(alpha = 0.55f), 17.dp)),
        ) {
            artwork()
        }
        Box(Modifier.offset(y = drop)) { caption(focused) }
    }
}

/** A row's heading: marquee type on the shared edge. */
@Composable
fun TvRowHeading(title: String, modifier: Modifier = Modifier) {
    Text(title, style = marquee(20), modifier = modifier.padding(start = TvLayout.Edge))
}

/** "2016   1h 56m   TV-MA": facts separated by space, never middle dots. */
@Composable
fun TvFactsLine(facts: List<String>, modifier: Modifier = Modifier) {
    Row(modifier, horizontalArrangement = Arrangement.spacedBy(11.dp)) {
        facts.forEach { Text(it, fontSize = 13.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen.copy(alpha = 0.85f), maxLines = 1) }
    }
}

/** Loading, a failure with Try again, or nothing to show, in plain words. */
@Composable
fun TvStatusView(message: String? = null, retry: (() -> Unit)? = null, empty: Boolean = false, modifier: Modifier = Modifier) {
    Column(
        modifier.fillMaxSize(),
        verticalArrangement = Arrangement.spacedBy(14.dp, Alignment.CenterVertically),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        when {
            message == null -> CircularProgressIndicator(color = Flexarr.screen)
            empty -> Text(message, fontSize = 15.sp, fontWeight = FontWeight.Medium, color = Flexarr.usher)
            else -> {
                Icon(Icons.Filled.WifiOff, contentDescription = null, tint = Flexarr.usher, modifier = Modifier.size(32.dp))
                Text(message, fontSize = 15.sp, fontWeight = FontWeight.Medium, textAlign = TextAlign.Center, modifier = Modifier.widthIn(max = 500.dp))
                if (retry != null) TvActionButton("Try again", onClick = retry, prominent = true)
            }
        }
    }
}
