package io.github.flexarr.ui.components

import androidx.compose.foundation.text.BasicText
import androidx.compose.foundation.text.TextAutoSize
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.TextLayoutResult
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.TextUnit
import androidx.compose.ui.unit.sp

/**
 * A label on one line that shrinks, down to [smallest], to fit the room it has: a tab's name in a language that runs
 * long (German, Finnish) rather than cut short. Past [smallest] it ends with "…". [style] is the screen's own text
 * style (phone or TV), drawn at up to [largest] in [color].
 */
@Composable
fun FittingText(
    text: String,
    largest: TextUnit,
    smallest: TextUnit,
    color: Color,
    style: TextStyle,
    modifier: Modifier = Modifier,
    onTextLayout: ((TextLayoutResult) -> Unit)? = null,
) {
    BasicText(
        text,
        modifier = modifier,
        style = style.merge(TextStyle(color = color, fontSize = largest)),
        onTextLayout = onTextLayout,
        maxLines = 1,
        overflow = TextOverflow.Ellipsis,
        autoSize = TextAutoSize.StepBased(minFontSize = smallest, maxFontSize = largest, stepSize = FITTING_STEP),
    )
}

private val FITTING_STEP = 0.5.sp
