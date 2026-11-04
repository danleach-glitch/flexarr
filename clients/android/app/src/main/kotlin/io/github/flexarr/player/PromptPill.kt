package io.github.flexarr.player

import io.github.flexarr.core.i18n.t
import io.github.flexarr.core.i18n.Words

import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.foundation.background
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsFocusedAsState
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.IntrinsicSize
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicText
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.FastForward
import androidx.compose.material.icons.filled.SkipNext
import androidx.compose.material3.Icon
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.semantics.clearAndSetSemantics
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.TextUnit
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.tv.material3.ClickableSurfaceDefaults
import androidx.tv.material3.Surface
import io.github.flexarr.ui.components.pressable
import io.github.flexarr.ui.theme.Flexarr

// Skip Intro or Next Episode, the pill above the timeline. Counting down to the next episode, its X (Cancel) is the
// pill's right half: "Next episode in 7 | X", the halves 2dp apart with square inner edges. Each half is its own
// button: on a TV the focused one is white and the other grey; on the phone Next is white and the X grey.

/** The pill's width doesn't follow the countdown: it holds its "Next episode in 10" width from the countdown's start. */
private const val HOLD_WIDTH_OF = "Next episode in 10"

/** What the pill last showed, so it fades out as it was (not as a shorter "Next episode" first). */
private class PromptShown {
    var title = ""
    var countingDown = false
    var held = false
    var visible = false
}

/**
 * Shows [content] with the pill's [title] while there is one and fades it out as it last was once there isn't. Its
 * third argument says whether the pill holds its "Next episode in 10" width: from the countdown's start until it goes.
 */
@Composable
internal fun FadingPrompt(title: String?, countingDown: Boolean, modifier: Modifier = Modifier, content: @Composable (title: String, countingDown: Boolean, held: Boolean) -> Unit) {
    val shown = remember { PromptShown() }
    if (title != null) {
        if (!shown.visible) shown.held = false
        if (countingDown && title.startsWith("Next episode")) shown.held = true
        shown.title = title
        shown.countingDown = countingDown
        shown.visible = true
    } else {
        shown.visible = false
    }
    val showTitle = shown.title
    val showCounting = shown.countingDown
    val held = shown.held
    AnimatedVisibility(title != null, modifier = modifier, enter = fadeIn(), exit = fadeOut()) {
        content(showTitle, showCounting, held)
    }
}

// Next episode shows ▶▶, as on Apple; Skip intro and recap their skip-ahead.
private fun promptIcon(title: String): ImageVector = if (title.startsWith("Skip")) Icons.Filled.SkipNext else Icons.Filled.FastForward

/** The Next half's shape: a capsule by itself, its left end only with the X beside it. */
private fun nextShape(countingDown: Boolean) = if (countingDown) RoundedCornerShape(topStartPercent = 50, bottomStartPercent = 50) else RoundedCornerShape(percent = 50)

private val xShape = RoundedCornerShape(topEndPercent = 50, bottomEndPercent = 50)

private val restingFill get() = Flexarr.aisle.copy(alpha = 0.85f)

/** The label, with tabular digits, kept at least as wide as "Next episode in 10" while [held]. */
@Composable
private fun PromptLabel(title: String, held: Boolean, color: Color, fontSize: TextUnit) {
    val style = TextStyle(color = color, fontSize = fontSize, fontWeight = FontWeight.SemiBold, fontFeatureSettings = "tnum")
    Box(contentAlignment = Alignment.CenterStart) {
        if (held) BasicText(HOLD_WIDTH_OF, style = style, maxLines = 1, modifier = Modifier.alpha(0f).clearAndSetSemantics {})
        BasicText(title, style = style, maxLines = 1)
    }
}

/** The phone's pill, kept compact: Next white, its X grey. */
@Composable
internal fun PromptPill(title: String, countingDown: Boolean, held: Boolean, onTap: () -> Unit, onCancel: () -> Unit) {
    // Both halves as tall as the taller (the label's), so they meet as one pill.
    Row(Modifier.height(IntrinsicSize.Min), horizontalArrangement = Arrangement.spacedBy(2.dp)) {
        Row(
            // Taps land up to 8dp above and below the pill, so it's easy to hit while staying compact.
            Modifier.fillMaxHeight().pressable(onTap).padding(vertical = 8.dp).clip(nextShape(countingDown))
                .background(Flexarr.screen.copy(alpha = 0.95f)).padding(horizontal = 13.dp, vertical = 8.dp),
            horizontalArrangement = Arrangement.spacedBy(6.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Icon(promptIcon(title), contentDescription = null, tint = Flexarr.room, modifier = Modifier.size(14.dp))
            PromptLabel(title, held, Flexarr.room, 14.sp)
        }
        if (countingDown) {
            Box(
                Modifier.fillMaxHeight().pressable(onCancel).semantics { contentDescription = t(Words.cancel) }.padding(vertical = 8.dp)
                    .clip(xShape).background(restingFill).padding(start = 11.dp, end = 14.dp),
                contentAlignment = Alignment.Center,
            ) {
                Icon(Icons.Filled.Close, contentDescription = null, tint = Flexarr.screen, modifier = Modifier.size(13.dp))
            }
        }
    }
}

/** One half of the TV's pill: white with dark content when focused, grey with light content when not. */
@Composable
private fun TvPromptHalf(onClick: () -> Unit, shape: RoundedCornerShape, modifier: Modifier, start: Dp, end: Dp, content: @Composable (Color) -> Unit) {
    val source = remember { MutableInteractionSource() }
    val focused by source.collectIsFocusedAsState()
    Surface(
        onClick = onClick,
        modifier = modifier.fillMaxHeight(),
        shape = ClickableSurfaceDefaults.shape(shape),
        colors = ClickableSurfaceDefaults.colors(
            containerColor = restingFill,
            focusedContainerColor = Flexarr.screen,
            pressedContainerColor = Flexarr.screen,
        ),
        // The halves sit 2dp apart: grown on focus, one would cover the gap.
        scale = ClickableSurfaceDefaults.scale(focusedScale = 1f, pressedScale = 0.97f),
        interactionSource = source,
    ) {
        Row(Modifier.fillMaxHeight().padding(start = start, end = end), horizontalArrangement = Arrangement.spacedBy(7.dp), verticalAlignment = Alignment.CenterVertically) {
            content(if (focused) Flexarr.room else Flexarr.screen)
        }
    }
}

/** The TV's pill: each half takes focus (left and right move between them); [nextModifier] is the Next half's. */
@Composable
internal fun TvPromptPill(title: String, countingDown: Boolean, held: Boolean, onTap: () -> Unit, onCancel: () -> Unit, nextModifier: Modifier = Modifier) {
    Row(Modifier.height(38.dp), horizontalArrangement = Arrangement.spacedBy(2.dp)) {
        TvPromptHalf(onTap, nextShape(countingDown), nextModifier, start = 16.dp, end = 18.dp) { tint ->
            Icon(promptIcon(title), contentDescription = null, tint = tint, modifier = Modifier.size(15.dp))
            PromptLabel(title, held, tint, 14.5.sp)
        }
        if (countingDown) {
            TvPromptHalf(onCancel, xShape, Modifier.semantics { contentDescription = t(Words.cancel) }, start = 10.dp, end = 13.dp) { tint ->
                Icon(Icons.Filled.Close, contentDescription = null, tint = tint, modifier = Modifier.size(15.dp))
            }
        }
    }
}
