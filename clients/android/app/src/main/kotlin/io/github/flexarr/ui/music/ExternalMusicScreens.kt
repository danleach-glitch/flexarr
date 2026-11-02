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
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.pulltorefresh.PullToRefreshBox
import androidx.compose.ui.draw.shadow
import io.github.flexarr.core.Format
import io.github.flexarr.core.MusicAlbumDetail
import io.github.flexarr.core.MusicArtistDetail
import io.github.flexarr.core.MusicTrack
import io.github.flexarr.core.musicAlbum
import io.github.flexarr.core.musicArtist
import io.github.flexarr.ui.components.ActionButton
import io.github.flexarr.ui.components.Edge
import io.github.flexarr.ui.components.FactsLine
import io.github.flexarr.ui.components.Load
import io.github.flexarr.ui.components.PosterWidth
import io.github.flexarr.ui.components.RowSpacing
import io.github.flexarr.ui.components.SectionHeading
import io.github.flexarr.ui.components.StatusView
import io.github.flexarr.ui.components.rememberLoader
import io.github.flexarr.ui.podcasts.Tabular
import io.github.flexarr.ui.theme.RoomBackground
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

/** A heading with a row of cards from outside the library under it, scrolling sideways to the screen's edges. */
@Composable
internal fun MusicExternalShelf(model: AppModel, title: String, items: List<MusicExternal>, modifier: Modifier = Modifier) {
    val width = PosterWidth * 1.15f
    Column(modifier, verticalArrangement = Arrangement.spacedBy(10.dp)) {
        SectionHeading(title)
        LazyRow(contentPadding = PaddingValues(horizontal = Edge), horizontalArrangement = Arrangement.spacedBy(RowSpacing), verticalAlignment = Alignment.Top) {
            items(items, key = { it.id }) { MusicExternalCard(model, it, width = width) }
        }
    }
}

/**
 * The header of an outside page: the art, the name and what's under it, and one button (Follow artist, Request album,
 * or where it stands). "In your library" opens it.
 */
@Composable
private fun ExternalMusicHeader(
    art: @Composable () -> Unit,
    title: String,
    below: @Composable () -> Unit,
    item: MusicExternal,
    sent: Boolean,
    onRequest: () -> Unit,
) {
    val navigator = LocalNavigator.current
    val state = item.requestState(sent)
    Column(Modifier.fillMaxWidth().statusBarsPadding().padding(top = 12.dp, start = Edge, end = Edge), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(16.dp)) {
        art()
        Column(horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(6.dp)) {
            Text(title, style = marquee(30), textAlign = TextAlign.Center, maxLines = 2, overflow = TextOverflow.Ellipsis)
            below()
        }
        ActionButton(
            state.label(item.isArtist),
            onClick = {
                when (state) {
                    MusicRequestState.CanRequest -> onRequest()
                    MusicRequestState.InLibrary -> navigator.openMusic(item)
                    else -> Unit
                }
            },
            icon = state.icon,
            prominent = state == MusicRequestState.CanRequest,
            enabled = state == MusicRequestState.CanRequest || (state == MusicRequestState.InLibrary && item.libraryItemId != null),
        )
    }
}

/**
 * An artist from outside the library, as the iPhone app's ExternalArtistView: their photo and name, Follow artist,
 * their albums each with Request album, and Fans also like. One the household has opens as itself.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ExternalArtistScreen(model: AppModel, deezerId: Long) {
    val client = model.client ?: return
    val navigator = LocalNavigator.current
    val page = rememberLoader(deezerId) { client.musicArtist(deezerId) }
    (page.state as? Load.Loaded)?.value?.libraryItemId?.let { id ->
        ArtistScreen(model, id)
        return
    }
    var sent by remember(deezerId) { mutableStateOf(false) }
    RoomBackground(ambient = Flexarr.silver, intensity = 0.3f) {
        when (val state = page.state) {
            is Load.Loaded -> PullToRefreshBox(isRefreshing = page.refreshing, onRefresh = page.retry) {
                val artist = state.value
                val summary = artist.summary(followedHere = sent)
                LazyVerticalGrid(
                    columns = GridCells.Adaptive(105.dp),
                    contentPadding = PaddingValues(start = Edge, end = Edge, bottom = 32.dp),
                    horizontalArrangement = Arrangement.spacedBy(12.dp),
                    verticalArrangement = Arrangement.spacedBy(20.dp),
                    modifier = Modifier.fillMaxSize(),
                ) {
                    fullWidth {
                        ExternalMusicHeader(
                            art = { MusicArtwork(artist.image, artist.name, circular = true, modifier = Modifier.width(180.dp).shadow(20.dp, CircleShape)) },
                            title = artist.name,
                            below = { if (artist.albums.isNotEmpty()) FactsLine(listOf(if (artist.albums.size == 1) "1 album" else "${artist.albums.size} albums")) },
                            item = summary,
                            sent = sent,
                            onRequest = { sent = true; navigator.requestMusic(model, summary) { sent = false } },
                        )
                    }
                    if (artist.albums.isNotEmpty()) {
                        fullWidth { SectionHeading("Albums", Modifier.bleed()) }
                        items(artist.albums, key = { it.id }) { album -> MusicExternalCard(model, album, width = null, showsRequest = true) }
                    }
                    if (artist.similar.isNotEmpty()) {
                        fullWidth { MusicExternalShelf(model, "Fans also like", artist.similar, Modifier.bleed()) }
                    }
                }
            }
            is Load.Failed -> StatusView(state.message, page.retry)
            Load.Loading -> StatusView()
        }
        BackButton()
    }
}

/** The artist as a card knows them, for Follow artist: following already (there or here) reads as Requested. */
private fun MusicArtistDetail.summary(followedHere: Boolean): MusicExternal = MusicExternal(
    "artist", deezerId, name, image = image,
    availability = if (followed || followedHere) Availability.Requested else availability,
    libraryItemId = libraryItemId,
)

/**
 * An album from outside the library, as the iPhone app's ExternalAlbumView: cover, title, artist (opening theirs) and
 * year, Request album, and its tracks. One the household has opens as itself.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ExternalAlbumScreen(model: AppModel, deezerId: Long) {
    val client = model.client ?: return
    val navigator = LocalNavigator.current
    val page = rememberLoader(deezerId) { client.musicAlbum(deezerId) }
    (page.state as? Load.Loaded)?.value?.libraryItemId?.let { id ->
        AlbumScreen(model, id)
        return
    }
    var sent by remember(deezerId) { mutableStateOf(false) }
    RoomBackground(ambient = Flexarr.silver, intensity = 0.3f) {
        when (val state = page.state) {
            is Load.Loaded -> PullToRefreshBox(isRefreshing = page.refreshing, onRefresh = page.retry) {
                val album = state.value
                val summary = album.summary
                LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 32.dp)) {
                    item {
                        ExternalMusicHeader(
                            art = { MusicArtwork(album.image, album.title, circular = false, modifier = Modifier.width(240.dp).shadow(20.dp, RoundedCornerShape(Tokens.radiusArt)), imageWidth = 800) },
                            title = album.title,
                            below = {
                                val artistId = album.artistDeezerId
                                Text(
                                    album.artist,
                                    fontSize = 17.sp,
                                    fontWeight = FontWeight.SemiBold,
                                    color = if (artistId != null) Flexarr.silver else Flexarr.screen.copy(alpha = 0.9f),
                                    modifier = if (artistId != null) Modifier.pressable({ navigator.openMusicArtist(artistId) }) else Modifier,
                                )
                                FactsLine(albumFacts(album))
                            },
                            item = summary,
                            sent = sent,
                            onRequest = { sent = true; navigator.requestMusic(model, summary) { sent = false } },
                        )
                    }
                    album.tracks.forEachIndexed { index, track ->
                        item(key = "track-$index") {
                            val disc = track.disc.takeIf { album.tracks.any { it.disc != album.tracks.first().disc } }
                            if (disc != null && (index == 0 || album.tracks[index - 1].disc != track.disc)) {
                                Text("Disc $disc", style = marquee(20, Flexarr.usher).copy(fontWeight = FontWeight.Bold), modifier = Modifier.padding(start = Edge + 4.dp, top = if (index == 0) 20.dp else 40.dp, bottom = 6.dp))
                            }
                            Box(Modifier.padding(horizontal = Edge - 8.dp).padding(top = if (index == 0 && disc == null) 20.dp else 0.dp)) {
                                OutsideTrackRow(track, if (track.number > 0) track.number else index + 1)
                            }
                        }
                    }
                }
            }
            is Load.Failed -> StatusView(state.message, page.retry)
            Load.Loading -> StatusView()
        }
        BackButton()
    }
}

/** "Album, 2001   14 tracks   1h 1m". */
private fun albumFacts(album: MusicAlbumDetail): List<String> = buildList {
    album.summary.kindLine?.let(::add)
    if (album.tracks.isNotEmpty()) add(if (album.tracks.size == 1) "1 track" else "${album.tracks.size} tracks")
    if (album.durationSeconds > 0) Format.runtime(album.durationSeconds.toDouble())?.let(::add)
}

/** A track on an album the library doesn't have: number, title and length, nothing to play. */
@Composable
private fun OutsideTrackRow(track: MusicTrack, number: Int) {
    Row(
        Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 12.dp),
        horizontalArrangement = Arrangement.spacedBy(14.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Box(Modifier.width(28.dp), contentAlignment = Alignment.CenterEnd) {
            Text(number.toString(), fontSize = 15.sp, fontWeight = FontWeight.Medium, color = Flexarr.usher, style = Tabular)
        }
        Text(track.title, fontSize = 17.sp, color = Flexarr.screen.copy(alpha = 0.9f), maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f))
        if (track.durationSeconds > 0) Text(Format.timestamp(track.durationSeconds.toDouble()), fontSize = 15.sp, color = Flexarr.usher, style = Tabular)
    }
}
