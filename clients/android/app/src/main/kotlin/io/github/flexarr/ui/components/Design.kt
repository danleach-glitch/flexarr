package io.github.flexarr.ui.components

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsPressedAsState
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.WifiOff
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.scale
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Shadow
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import coil3.compose.SubcomposeAsyncImage
import io.github.flexarr.core.ItemSummary
import io.github.flexarr.core.displayTitle
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.marquee

// The Apple apps' shared pieces (Theme.swift and Cards.swift on iPhone), for the phone.

/** The shared left edge for heroes, rows and grids. */
val Edge = 20.dp
/** Posters in a row: a little over three across on a phone. */
val PosterWidth = 116.dp
val WideWidth = 260.dp
val RowSpacing = 12.dp

/** A press dims a touch and shrinks a little, rather than rippling: cards and buttons alike, as on iPhone. */
@Composable
fun Modifier.pressable(onClick: () -> Unit, enabled: Boolean = true, onLongClick: (() -> Unit)? = null): Modifier {
    val source = remember { MutableInteractionSource() }
    val pressed by source.collectIsPressedAsState()
    val gestures = if (onLongClick == null) {
        clickable(source, indication = null, enabled = enabled, onClick = onClick)
    } else {
        combinedClickable(source, indication = null, enabled = enabled, onClick = onClick, onLongClick = onLongClick)
    }
    return this.scale(if (pressed) 0.98f else 1f).alpha(if (pressed) 0.75f else 1f).then(gestures)
}

/**
 * Play and the other actions on heroes and details: the main one in projected white, the rest quieter on a
 * translucent seat, with the title's ambient colour in the glow.
 */
@Composable
fun ActionButton(
    label: String,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    icon: ImageVector? = null,
    prominent: Boolean = false,
    ambient: Color = Flexarr.silver,
    enabled: Boolean = true,
) {
    Row(
        modifier
            .heightIn(min = 48.dp)
            .then(if (prominent) Modifier.shadow(16.dp, RoundedCornerShape(12.dp), ambientColor = ambient.copy(alpha = 0.45f), spotColor = ambient.copy(alpha = 0.45f)) else Modifier)
            .clip(RoundedCornerShape(12.dp))
            .background(if (prominent) Flexarr.screen else Flexarr.screen.copy(alpha = 0.14f))
            .alpha(if (enabled) 1f else 0.5f)
            .pressable(onClick, enabled)
            .padding(horizontal = 20.dp),
        horizontalArrangement = Arrangement.spacedBy(8.dp, Alignment.CenterHorizontally),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        val tint = if (prominent) Flexarr.room else Flexarr.screen
        if (icon != null) Icon(icon, contentDescription = null, tint = tint, modifier = Modifier.size(20.dp))
        Text(label, color = tint, fontWeight = FontWeight.SemiBold, fontSize = 17.sp, maxLines = 1)
    }
}

/** A round action with its name underneath (the row under Play on a title's page). */
@Composable
fun LabeledAction(label: String, icon: ImageVector, onClick: () -> Unit, modifier: Modifier = Modifier) {
    Column(modifier.pressable(onClick), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(6.dp)) {
        Box(Modifier.size(48.dp).clip(CircleShape).background(Flexarr.screen.copy(alpha = 0.14f)), contentAlignment = Alignment.Center) {
            Icon(icon, contentDescription = null, tint = Flexarr.screen, modifier = Modifier.size(22.dp))
        }
        Text(label, fontSize = 12.sp, fontWeight = FontWeight.Medium, color = Flexarr.usher, textAlign = TextAlign.Center, maxLines = 2)
    }
}

/** Small selectable pill for seasons. */
@Composable
fun PillButton(label: String, selected: Boolean, onClick: () -> Unit, ambient: Color = Flexarr.silver) {
    Box(
        Modifier.height(36.dp).clip(CircleShape)
            .then(if (selected) Modifier.background(ambient.copy(alpha = 0.32f)) else Modifier.background(Color.Transparent))
            .then(if (selected) Modifier else Modifier.border(1.5.dp, Flexarr.aisle, CircleShape))
            .pressable(onClick)
            .padding(horizontal = 16.dp),
        contentAlignment = Alignment.Center,
    ) {
        Text(label, fontSize = 15.sp, fontWeight = if (selected) FontWeight.Bold else FontWeight.Medium, color = if (selected) Flexarr.screen else Flexarr.usher)
    }
}

/** Section heading for rows: marquee type on the shared edge, with an optional See all. */
@Composable
fun SectionHeading(title: String, modifier: Modifier = Modifier, onSeeAll: (() -> Unit)? = null) {
    Row(modifier.fillMaxWidth().padding(horizontal = Edge), verticalAlignment = Alignment.Bottom) {
        Text(title, style = marquee(26).copy(fontWeight = FontWeight.Bold), modifier = Modifier.weight(1f), maxLines = 1, overflow = TextOverflow.Ellipsis)
        if (onSeeAll != null) {
            Text("See all", fontSize = 15.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.usher, modifier = Modifier.pressable(onSeeAll).padding(4.dp))
        }
    }
}

/** A page's own title, as iPhone's large navigation titles, with Back when the page was opened from another. */
@Composable
fun PageTitle(title: String, onBack: (() -> Unit)? = null, trailing: @Composable () -> Unit = {}) {
    Column(Modifier.fillMaxWidth().statusBarsPadding()) {
        Row(Modifier.fillMaxWidth().height(48.dp).padding(horizontal = 4.dp), verticalAlignment = Alignment.CenterVertically) {
            if (onBack != null) IconButton(onClick = onBack) { Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = "Back", tint = Flexarr.screen) }
            Box(Modifier.weight(1f))
            trailing()
        }
        Text(title, fontSize = 34.sp, fontWeight = FontWeight.Bold, color = Flexarr.screen, modifier = Modifier.padding(horizontal = Edge).padding(bottom = 8.dp), maxLines = 1, overflow = TextOverflow.Ellipsis)
    }
}

/** "2016   1h 56m   TV-MA": facts separated by space, never middle dots. */
@Composable
fun FactsLine(facts: List<String>, modifier: Modifier = Modifier) {
    Row(modifier, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
        facts.forEach { Text(it, fontSize = 15.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen.copy(alpha = 0.85f), maxLines = 1) }
    }
}

/** Thin bar for resume points. */
@Composable
fun ProgressBar(value: Double, modifier: Modifier = Modifier, tint: Color = Flexarr.screen) {
    Box(modifier.fillMaxWidth().height(4.dp).clip(CircleShape).background(Color.Black.copy(alpha = 0.45f))) {
        Box(Modifier.fillMaxWidth(value.toFloat().coerceIn(0f, 1f)).widthIn(min = 4.dp).height(4.dp).clip(CircleShape).background(tint))
    }
}

@Composable
fun WatchedBadge(modifier: Modifier = Modifier) {
    Box(modifier.size(20.dp).clip(CircleShape).background(Flexarr.screen.copy(alpha = 0.92f)), contentAlignment = Alignment.Center) {
        Icon(Icons.Filled.Check, contentDescription = "Watched", tint = Flexarr.room, modifier = Modifier.size(13.dp))
    }
}

/** Clear logo when the title has one, otherwise the title set in marquee type. */
@Composable
fun TitleArt(item: ItemSummary, maxWidth: Dp, maxHeight: Dp, fontSize: Int) {
    val client = LocalClient.current
    val title = @Composable {
        Text(
            item.displayTitle,
            style = marquee(fontSize).copy(shadow = Shadow(Color.Black.copy(alpha = 0.35f), blurRadius = 20f)),
            maxLines = 2,
            overflow = TextOverflow.Ellipsis,
            modifier = Modifier.widthIn(max = maxWidth * 1.2f),
        )
    }
    val logo = client.imageUrl(item.artwork.logo, 600)
    if (logo == null) {
        title()
    } else {
        SubcomposeAsyncImage(
            model = logo,
            contentDescription = item.title,
            contentScale = ContentScale.Fit,
            alignment = Alignment.BottomStart,
            modifier = Modifier.widthIn(max = maxWidth).heightIn(max = maxHeight),
            error = { title() },
            loading = { Box(Modifier.size(maxWidth, maxHeight / 2)) },
        )
    }
}

/** Loading, error and empty states in plain words. */
@Composable
fun StatusView(message: String? = null, retry: (() -> Unit)? = null, empty: Boolean = false, modifier: Modifier = Modifier) {
    Column(
        modifier.fillMaxSize().padding(32.dp),
        verticalArrangement = Arrangement.spacedBy(16.dp, Alignment.CenterVertically),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        when {
            message == null -> CircularProgressIndicator(color = Flexarr.screen)
            empty -> Text(message, fontSize = 17.sp, fontWeight = FontWeight.Medium, color = Flexarr.usher, textAlign = TextAlign.Center)
            else -> {
                Icon(Icons.Filled.WifiOff, contentDescription = null, tint = Flexarr.usher, modifier = Modifier.size(40.dp))
                Text(message, fontSize = 17.sp, fontWeight = FontWeight.Medium, color = Flexarr.screen, textAlign = TextAlign.Center)
                if (retry != null) ActionButton("Try again", onClick = retry, prominent = true)
            }
        }
    }
}

/** Body text in the app's colours. */
val Body = TextStyle(fontSize = 15.sp, color = Flexarr.screen)
