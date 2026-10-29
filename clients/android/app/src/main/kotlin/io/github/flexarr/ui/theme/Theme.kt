package io.github.flexarr.ui.theme

import android.graphics.Typeface
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxScope
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.drawBehind
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.luminance
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight

// Tokens from docs/design.md, as the Apple apps' Colors.swift. The room is a dim plum, never pure black, and the only
// loud colour on screen is each title's own ambient colour.
object Flexarr {
    val room = Color(0xFF1B1720)
    val seat = Color(0xFF25202C)
    val aisle = Color(0xFF3A3343)
    val screen = Color(0xFFF2EEE7)
    val usher = Color(0xFFA39AAE)
    /** Fallback accent when a title has no ambient colour. */
    val silver = Color(0xFFA9C2EC)
    /** Errors: a soft coral that reads on the room without shouting. */
    val danger = Color(0xFFF08A7E)
    /** On the phone, or a good match: the one green. */
    val downloaded = Color(0xFF6FCF97)

    /** Marquee display type: tall, condensed and heavy, standing in for Big Shoulders Display. */
    val marquee = FontFamily(Typeface.create("sans-serif-condensed", Typeface.BOLD))
}

fun marquee(size: Int, color: Color = Flexarr.screen) =
    TextStyle(fontFamily = Flexarr.marquee, fontSize = size.sp, fontWeight = FontWeight.Black, color = color)

/** A title's ambient colour ("#3a6b8c"), or silver when it has none. */
fun ambientColor(hex: String?): Color {
    var text = hex?.trim()?.removePrefix("#") ?: return Flexarr.silver
    if (text.length == 3) text = text.map { "$it$it" }.joinToString("")
    return text.takeIf { it.length == 6 }?.toLongOrNull(16)?.let { Color(0xFF000000 or it) } ?: Flexarr.silver
}

/**
 * A version bright enough to read as a glow on the dark room. Very dark artwork colours (navy, near-black) are lifted
 * towards white so they still show, as FlexarrKit's `RGB.glow`.
 */
fun Color.glow(): Color {
    var colour = this
    var steps = 0
    while (colour.luminance() < 0.12f && steps < 10) {
        colour = Color(
            red = colour.red + (1 - colour.red) * 0.15f,
            green = colour.green + (1 - colour.green) * 0.15f,
            blue = colour.blue + (1 - colour.blue) * 0.15f,
        )
        steps++
    }
    return colour
}

/** A title's colour lifted to glow on the room. */
fun glowColor(hex: String?): Color = ambientColor(hex).glow()

/**
 * The room with an optional ambient wash bleeding in from the top-left, like bias lighting behind a TV. Everything on
 * a screen sits on it.
 */
@Composable
fun RoomBackground(ambient: Color?, modifier: Modifier = Modifier, intensity: Float = 0.35f, reach: Dp = 700.dp, content: @Composable BoxScope.() -> Unit) {
    val radius = with(LocalDensity.current) { reach.toPx() }
    Box(
        modifier.fillMaxSize().background(Flexarr.room).then(
            if (ambient == null) Modifier
            else Modifier.background(Brush.radialGradient(listOf(ambient.copy(alpha = intensity), Color.Transparent), center = Offset.Zero, radius = radius)),
        ),
        content = content,
    )
}

/**
 * The room as above, with a wash that changes (Home's, following the featured title): read only when it's drawn, so
 * the colour easing from one title's to the next repaints the room without recomposing the screen on it.
 */
@Composable
fun RoomBackground(ambient: () -> Color, modifier: Modifier = Modifier, intensity: Float = 0.35f, reach: Dp = 700.dp, content: @Composable BoxScope.() -> Unit) {
    val radius = with(LocalDensity.current) { reach.toPx() }
    Box(
        modifier.fillMaxSize().background(Flexarr.room).drawBehind {
            drawRect(Brush.radialGradient(listOf(ambient().copy(alpha = intensity), Color.Transparent), center = Offset.Zero, radius = radius))
        },
        content = content,
    )
}

private val colors = darkColorScheme(
    primary = Flexarr.screen,
    onPrimary = Flexarr.room,
    secondary = Flexarr.silver,
    onSecondary = Flexarr.room,
    background = Flexarr.room,
    onBackground = Flexarr.screen,
    surface = Flexarr.room,
    onSurface = Flexarr.screen,
    surfaceVariant = Flexarr.seat,
    onSurfaceVariant = Flexarr.usher,
    surfaceContainer = Flexarr.seat,
    surfaceContainerHigh = Flexarr.seat,
    surfaceContainerHighest = Flexarr.aisle,
    surfaceContainerLow = Flexarr.room,
    outline = Flexarr.aisle,
    outlineVariant = Flexarr.aisle,
    error = Flexarr.danger,
    onError = Flexarr.room,
)

@Composable
fun FlexarrTheme(content: @Composable () -> Unit) {
    MaterialTheme(colorScheme = colors, content = content)
}
