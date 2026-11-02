package io.github.flexarr.ui.music

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.Schedule
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Shape
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.github.flexarr.AppModel
import io.github.flexarr.core.Availability
import io.github.flexarr.core.MusicExternal
import io.github.flexarr.core.followArtist
import io.github.flexarr.core.requestAlbum
import io.github.flexarr.ui.Navigator
import io.github.flexarr.ui.components.ItemMenu
import io.github.flexarr.ui.components.LocalNavigator
import io.github.flexarr.ui.components.MenuAction
import io.github.flexarr.ui.components.RemoteImage
import io.github.flexarr.ui.components.menuActions
import io.github.flexarr.ui.components.pressable
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.Tokens
import io.github.flexarr.ui.theme.marquee

// Music from outside the library (Deezer's, through the server): cards for Search and the artist pages, and the
// outside artist and album pages, as the iPhone app's ExternalMusicViews.

/** Where an outside artist or album stands, for its button: asked for, followed, in, or not yet. */
internal enum class MusicRequestState { CanRequest, Requested, InLibrary, Unavailable }

/** [sent]: asked for here since the page loaded, ahead of the server's next answer. */
internal fun MusicExternal.requestState(sent: Boolean): MusicRequestState = when {
    libraryItemId != null || availability == Availability.Available || availability == Availability.PartlyAvailable -> MusicRequestState.InLibrary
    sent || availability == Availability.Requested -> MusicRequestState.Requested
    canRequest -> MusicRequestState.CanRequest
    else -> MusicRequestState.Unavailable
}

/** "Request album", "Follow artist", "Requested", "Following" or "In your library". */
internal fun MusicRequestState.label(isArtist: Boolean): String = when (this) {
    MusicRequestState.CanRequest -> if (isArtist) "Follow artist" else "Request album"
    MusicRequestState.Requested -> if (isArtist) "Following" else "Requested"
    MusicRequestState.InLibrary -> "In your library"
    MusicRequestState.Unavailable -> "Not available"
}

internal val MusicRequestState.icon: ImageVector
    get() = when (this) {
        MusicRequestState.CanRequest -> Icons.Filled.Add
        MusicRequestState.Requested -> Icons.Filled.Schedule
        MusicRequestState.InLibrary -> Icons.Filled.Check
        MusicRequestState.Unavailable -> Icons.Filled.Schedule
    }

/**
 * Asks Lidarr for an album or to follow an artist, saying so in the snackbar if that didn't work ([onFailed] puts back
 * what the screen showed).
 */
internal fun Navigator.requestMusic(model: AppModel, item: MusicExternal, onFailed: () -> Unit) {
    val client = model.client ?: return
    watchlistChange(if (item.isArtist) "Couldn’t follow this artist" else "Couldn’t request this album") {
        try {
            if (item.isArtist) client.followArtist(item.deezerId) else client.requestAlbum(item)
        } catch (e: Exception) {
            onFailed()
            throw e
        }
    }
}

/**
 * An artist (round photo) or album (square cover) from outside the library, with its name and a quieter line. Tapping
 * opens it (as itself when the household has it); a long press offers Request album or Follow artist. [showsRequest]
 * puts that under the card instead, for a page of an artist's albums.
 */
@Composable
fun MusicExternalCard(model: AppModel, item: MusicExternal, width: Dp?, modifier: Modifier = Modifier, showsRequest: Boolean = false) {
    val navigator = LocalNavigator.current
    var sent by remember(item.id) { mutableStateOf(false) }
    var menu by remember { mutableStateOf(false) }
    val state = item.requestState(sent)
    val request = { sent = true; navigator.requestMusic(model, item) { sent = false } }
    val actions = if (state == MusicRequestState.CanRequest && !showsRequest) listOf(MenuAction(state.label(item.isArtist), state.icon) { request() }) else emptyList()
    Box(modifier.then(if (width != null) Modifier.width(width) else Modifier)) {
        Column(verticalArrangement = Arrangement.spacedBy(6.dp), horizontalAlignment = if (item.isArtist) Alignment.CenterHorizontally else Alignment.Start) {
            Column(
                Modifier.pressable({ navigator.openMusic(item) }, onLongClick = { if (actions.isNotEmpty()) menu = true }).menuActions(actions),
                verticalArrangement = Arrangement.spacedBy(if (item.isArtist) 8.dp else 6.dp),
                horizontalAlignment = if (item.isArtist) Alignment.CenterHorizontally else Alignment.Start,
            ) {
                MusicExternalArtwork(item, if (item.isArtist) Modifier.padding(horizontal = 6.dp) else Modifier)
                Column(Modifier.fillMaxWidth(), verticalArrangement = Arrangement.spacedBy(1.dp), horizontalAlignment = if (item.isArtist) Alignment.CenterHorizontally else Alignment.Start) {
                    Text(item.title, fontSize = 13.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen.copy(alpha = 0.9f), maxLines = 1, overflow = TextOverflow.Ellipsis)
                    val below = if (item.isArtist) {
                        state.takeIf { it == MusicRequestState.InLibrary }?.label(true)
                    } else if (showsRequest) {
                        item.kindLine
                    } else {
                        item.artist ?: item.kindLine
                    }
                    below?.let { Text(it, fontSize = 12.sp, color = Flexarr.usher, maxLines = 1, overflow = TextOverflow.Ellipsis) }
                }
            }
            if (showsRequest) MusicRequestChip(item, state, onRequest = request)
        }
        ItemMenu(actions, menu, onDismiss = { menu = false })
    }
}

/** Under an album on an artist's page: Request album, or where it stands ("In your library" opens it). */
@Composable
private fun MusicRequestChip(item: MusicExternal, state: MusicRequestState, onRequest: () -> Unit) {
    val navigator = LocalNavigator.current
    val enabled = state == MusicRequestState.CanRequest || (state == MusicRequestState.InLibrary && item.libraryItemId != null)
    Box(
        Modifier.fillMaxWidth().heightIn(min = 32.dp).clip(RoundedCornerShape(Tokens.radiusControl))
            .background(if (state == MusicRequestState.CanRequest) Flexarr.screen.copy(alpha = 0.14f) else androidx.compose.ui.graphics.Color.Transparent)
            .alpha(if (state == MusicRequestState.Unavailable) 0.5f else 1f)
            .pressable({ if (state == MusicRequestState.CanRequest) onRequest() else navigator.openMusic(item) }, enabled = enabled)
            .padding(horizontal = 6.dp, vertical = 6.dp),
        contentAlignment = Alignment.Center,
    ) {
        Text(
            state.label(item.isArtist),
            fontSize = 12.sp,
            fontWeight = FontWeight.SemiBold,
            color = if (state == MusicRequestState.CanRequest) Flexarr.screen else Flexarr.usher,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
            textAlign = TextAlign.Center,
        )
    }
}

/** An outside artist's photo (round) or album's cover (square), the name standing in until it arrives. Fills its width. */
@Composable
fun MusicExternalArtwork(item: MusicExternal, modifier: Modifier = Modifier, imageWidth: Int = 500) =
    MusicArtwork(item.image, item.title, item.isArtist, modifier, imageWidth)

@Composable
internal fun MusicArtwork(image: String?, title: String, circular: Boolean, modifier: Modifier = Modifier, imageWidth: Int = 500) {
    val shape: Shape = if (circular) CircleShape else RoundedCornerShape(Tokens.radiusArt)
    Box(modifier.fillMaxWidth().aspectRatio(1f).clip(shape).background(Flexarr.seat)) {
        RemoteImage(image, imageWidth, Modifier.fillMaxSize()) {
            Box(Modifier.fillMaxSize().background(Brush.verticalGradient(listOf(Flexarr.silver.copy(alpha = 0.3f), Flexarr.seat))), contentAlignment = Alignment.Center) {
                Text(
                    if (circular) title.split(" ").filter { it.isNotEmpty() }.take(2).joinToString("") { it.first().toString() } else title,
                    style = marquee(if (circular) 28 else 16, Flexarr.screen.copy(alpha = 0.8f)).copy(fontWeight = FontWeight.Bold),
                    textAlign = TextAlign.Center,
                    modifier = Modifier.padding(8.dp),
                )
            }
        }
    }
}
