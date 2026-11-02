package io.github.flexarr.ui.library

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.blur
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.clearAndSetSemantics
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.role
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.github.flexarr.core.GenreCount
import io.github.flexarr.ui.components.RemoteImage
import io.github.flexarr.ui.components.pressable
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.Tokens
import io.github.flexarr.ui.theme.marquee

/**
 * A genre as a way in, for a library's Genres: a backdrop from one of its titles (a wash without one), the name and how
 * many there are. An audiobook genre has its books' covers fanned instead ([BookGenreArt]).
 */
@Composable
internal fun GenreTile(genre: GenreCount, caption: String, onClick: () -> Unit) {
    Box(
        Modifier.fillMaxWidth().aspectRatio(16f / 9f).clip(RoundedCornerShape(Tokens.radiusArt)).background(Flexarr.seat)
            .pressable(onClick)
            .clearAndSetSemantics {
                contentDescription = "${genre.name}, $caption"
                role = Role.Button
            },
    ) {
        val covers = genre.covers
        if (covers != null) {
            BookGenreArt(covers, Modifier.matchParentSize())
        } else {
            RemoteImage(genre.backdrop, 640, Modifier.matchParentSize()) { Wash() }
        }
        Box(Modifier.matchParentSize().background(Brush.verticalGradient(0.5f to Flexarr.room.copy(alpha = 0.1f), 1f to Flexarr.room.copy(alpha = 0.92f))))
        Column(Modifier.align(Alignment.BottomStart).padding(10.dp), verticalArrangement = Arrangement.spacedBy(2.dp)) {
            Text(genre.name, style = marquee(20).copy(fontWeight = FontWeight.ExtraBold), maxLines = 1, overflow = TextOverflow.Ellipsis)
            Text(caption, fontSize = 12.sp, fontWeight = FontWeight.Medium, color = Flexarr.usher, maxLines = 1)
        }
    }
}

/** The quiet wash a genre has without a picture. */
@Composable
private fun Wash() {
    Box(Modifier.fillMaxSize().background(Brush.linearGradient(listOf(Flexarr.silver.copy(alpha = 0.3f), Flexarr.seat))))
}

/**
 * An audiobook genre's picture, as on the web: books have no backdrops, so the genre's newest covers stand fanned in the
 * top right, over the front one blurred and darkened to fill the tile with its colours. With no covers (servers before
 * 0.58), the wash.
 */
@Composable
private fun BookGenreArt(covers: List<String>, modifier: Modifier) {
    val shown = covers.take(3)
    val front = shown.firstOrNull()
    BoxWithConstraints(modifier) {
        if (front == null) {
            Wash()
            return@BoxWithConstraints
        }
        RemoteImage(front, 185, Modifier.fillMaxSize().graphicsLayer { scaleX = 1.4f; scaleY = 1.4f }.blur(26.dp)) { Wash() }
        Box(Modifier.fillMaxSize().background(Color.Black.copy(alpha = 0.4f)))
        // Smaller than the web's half, so on a phone's tile the covers stay clear of the name beneath.
        val side = maxHeight * 0.4f
        Box(Modifier.align(Alignment.TopEnd).padding(top = maxHeight * 0.08f, end = maxWidth * 0.06f).size(side)) {
            // Back to front: each further back is smaller, dimmer and turned further out to the left.
            shown.indices.reversed().forEach { index ->
                val scale = listOf(1f, 0.9f, 0.8f)[index]
                Box(
                    Modifier.size(side).graphicsLayer {
                        translationX = -side.toPx() * listOf(0f, 0.34f, 0.62f)[index]
                        rotationZ = listOf(0f, -7f, -13f)[index]
                        scaleX = scale
                        scaleY = scale
                    }.shadow(10.dp, RoundedCornerShape(Tokens.radiusArt)).clip(RoundedCornerShape(Tokens.radiusArt)),
                ) {
                    RemoteImage(shown[index], 342, Modifier.fillMaxSize()) { Box(Modifier.fillMaxSize().background(Flexarr.seat)) }
                    Box(Modifier.fillMaxSize().background(Color.Black.copy(alpha = listOf(0f, 0.3f, 0.5f)[index])))
                }
            }
        }
    }
}
