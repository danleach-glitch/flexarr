package io.github.flexarr.ui.components

import androidx.compose.animation.core.animate
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.Orientation
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.gestures.draggable
import androidx.compose.foundation.gestures.rememberDraggableState
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clipToBounds
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.platform.LocalLayoutDirection
import androidx.compose.ui.semantics.clearAndSetSemantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.LayoutDirection
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.github.flexarr.ui.theme.Flexarr
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch
import kotlin.math.abs
import kotlin.math.roundToInt

// The iPhone's List swipe actions (.swipeActions): a row slides aside to show buttons at its start or end edge.

/** A button a swipe shows: a short [label], an [icon] and the colour behind them. */
class SwipeAction(val label: String, val icon: ImageVector, val color: Color, val contentColor: Color = Flexarr.screen, val run: () -> Unit)

private val ActionWidth = 76.dp

/**
 * A row that slides aside, as an iPhone list row does: swiping it toward its end shows [start] actions at its start
 * edge, and toward its start shows [end] actions at its end (start and end follow the reading direction, so they swap
 * for right-to-left). It rests open until an action, or the row, is tapped. With [fullSwipeStart] or [fullSwipeEnd],
 * swiping most of the way across does that edge's first action at once. The buttons are hidden from TalkBack: the row
 * offers the same actions as its own custom actions (see [menuActions]).
 */
@Composable
fun SwipeActions(
    start: List<SwipeAction>,
    end: List<SwipeAction>,
    modifier: Modifier = Modifier,
    fullSwipeStart: Boolean = true,
    fullSwipeEnd: Boolean = false,
    content: @Composable () -> Unit,
) {
    val density = LocalDensity.current
    val haptics = LocalHapticFeedback.current
    val rtl = LocalLayoutDirection.current == LayoutDirection.Rtl
    val scope = rememberCoroutineScope()
    // How far the row has slid toward its end (negative: toward its start), in pixels.
    var offset by remember { mutableFloatStateOf(0f) }
    var width by remember { mutableIntStateOf(0) }
    var settling by remember { mutableStateOf<Job?>(null) }
    val actionWidth = with(density) { ActionWidth.toPx() }
    val startReveal = start.size * actionWidth
    val endReveal = end.size * actionWidth
    // Past this (and past the buttons), a full swipe does the first action.
    fun fullAt(reveal: Float) = maxOf(width * 0.6f, reveal + actionWidth)
    val startFull = fullSwipeStart && start.isNotEmpty() && offset > fullAt(startReveal)
    val endFull = fullSwipeEnd && end.isNotEmpty() && -offset > fullAt(endReveal)

    fun settle(to: Float) {
        settling?.cancel()
        settling = scope.launch { animate(offset, to) { value, _ -> offset = value } }
    }
    fun perform(action: SwipeAction) {
        action.run()
        settle(0f)
    }

    val dragState = rememberDraggableState { delta ->
        val wasFull = startFull || endFull
        val most = if (fullSwipeStart) width.toFloat() else startReveal * 1.25f
        val least = if (fullSwipeEnd) -width.toFloat() else -endReveal * 1.25f
        offset = (offset + delta).coerceIn(if (end.isEmpty()) 0f else least, if (start.isEmpty()) 0f else most)
        val isFull = (fullSwipeStart && start.isNotEmpty() && offset > fullAt(startReveal)) ||
            (fullSwipeEnd && end.isNotEmpty() && -offset > fullAt(endReveal))
        if (isFull != wasFull) haptics.performHapticFeedback(HapticFeedbackType.GestureThresholdActivate)
    }

    Box(
        modifier.clipToBounds().onSizeChanged { width = it.width }.draggable(
            dragState,
            Orientation.Horizontal,
            // Dragging toward the end is rightward, unless the row reads right to left.
            reverseDirection = rtl,
            onDragStarted = { settling?.cancel() },
            onDragStopped = { velocity ->
                val fling = with(density) { 800.dp.toPx() }
                when {
                    startFull -> perform(start.first())
                    endFull -> perform(end.first())
                    offset > 0 && (offset > startReveal / 2 || velocity > fling) && velocity > -fling -> settle(startReveal)
                    offset < 0 && (-offset > endReveal / 2 || velocity < -fling) && velocity < fling -> settle(-endReveal)
                    else -> settle(0f)
                }
            },
        ),
    ) {
        // Offset follows the reading direction, so a positive offset moves the row toward its end either way.
        Box(Modifier.offset { IntOffset(offset.roundToInt(), 0) }) {
            content()
            // Open: a tap anywhere on the row closes it, rather than opening what the row opens.
            if (offset != 0f) {
                Box(Modifier.matchParentSize().pointerInput(Unit) { detectTapGestures { settle(0f) } })
            }
        }
        if (offset != 0f) {
            Box(Modifier.matchParentSize().clearAndSetSemantics {}) {
                val shown = with(density) { abs(offset).toDp() }
                val actions = if (offset > 0) start else end
                Row(
                    Modifier.align(if (offset > 0) Alignment.CenterStart else Alignment.CenterEnd).width(shown).fillMaxHeight(),
                ) {
                    // A full swipe gives the first action the whole width, as the iPhone does.
                    val full = if (offset > 0) startFull else endFull
                    val laidOut = if (full) listOf(actions.first()) else if (offset > 0) actions else actions.reversed()
                    laidOut.forEach { action -> ActionButton(action, Modifier.weight(1f)) { perform(action) } }
                }
            }
        }
    }
}

@Composable
private fun ActionButton(action: SwipeAction, modifier: Modifier, onClick: () -> Unit) {
    Box(modifier.fillMaxHeight().background(action.color).clickable(onClick = onClick), contentAlignment = Alignment.Center) {
        Column(horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(4.dp), modifier = Modifier.padding(horizontal = 4.dp)) {
            Icon(action.icon, contentDescription = null, tint = action.contentColor, modifier = Modifier.size(22.dp))
            Text(action.label, fontSize = 12.sp, fontWeight = FontWeight.SemiBold, color = action.contentColor, textAlign = TextAlign.Center, maxLines = 2, overflow = TextOverflow.Ellipsis)
        }
    }
}
