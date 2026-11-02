package io.github.flexarr.ui.tv

import android.os.SystemClock
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyListScope
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.tv.material3.ClickableSurfaceDefaults
import androidx.tv.material3.Surface
import androidx.tv.material3.Text
import io.github.flexarr.AppModel
import io.github.flexarr.core.Availability
import io.github.flexarr.core.FlexarrClient
import io.github.flexarr.core.FlexarrException
import io.github.flexarr.core.Format
import io.github.flexarr.core.MusicAlbumDetail
import io.github.flexarr.core.MusicArtistDetail
import io.github.flexarr.core.MusicExternal
import io.github.flexarr.core.MusicTrack
import io.github.flexarr.core.followArtist
import io.github.flexarr.core.musicAlbum
import io.github.flexarr.core.musicArtist
import io.github.flexarr.core.requestAlbum
import io.github.flexarr.ui.components.Load
import io.github.flexarr.ui.components.LocalNavigator
import io.github.flexarr.ui.components.RemoteImage
import io.github.flexarr.ui.components.rememberLoader
import io.github.flexarr.ui.music.MusicRequestState
import io.github.flexarr.ui.music.icon
import io.github.flexarr.ui.music.label
import io.github.flexarr.ui.music.requestMusic
import io.github.flexarr.ui.music.requestState
import io.github.flexarr.ui.podcasts.Tabular
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.RoomBackground
import io.github.flexarr.ui.theme.Tokens
import io.github.flexarr.ui.theme.marquee
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.launch

// Music from outside the library on the TV, as the Apple TV's ExternalMusicViews: cards for Discover and the artist
// pages, and the outside artist and album pages. Each page's button (Follow artist, Request album) takes focus as it
// opens.

/** An outside artist's photo (round) or album's cover (square), the name standing in until it arrives. */
@Composable
internal fun TvMusicArtwork(image: String?, title: String, circular: Boolean, size: Dp) {
    Box(Modifier.size(size).clip(if (circular) CircleShape else RoundedCornerShape(Tokens.radiusArt)).background(Flexarr.seat)) {
        RemoteImage(image, if (size > 200.dp) 800 else 500, Modifier.fillMaxSize()) {
            Box(Modifier.fillMaxSize().background(Brush.verticalGradient(listOf(Flexarr.silver.copy(alpha = 0.3f), Flexarr.seat))), contentAlignment = Alignment.Center) {
                val fontSize = if (circular) size.value * 0.3f else maxOf(12f, size.value * 0.11f)
                Text(
                    if (circular) title.split(" ").filter { it.isNotEmpty() }.take(2).joinToString("") { it.first().toString() } else title,
                    style = marquee(fontSize.toInt(), Flexarr.screen.copy(alpha = 0.8f)).copy(fontWeight = FontWeight.Bold),
                    textAlign = TextAlign.Center,
                    modifier = Modifier.padding(size * 0.08f),
                )
            }
        }
    }
}

/**
 * An artist (round) or album (square) from outside the library, with its name and where it stands. Pressing opens it,
 * as itself when the household has it.
 */
@Composable
internal fun TvMusicExternalCard(item: MusicExternal, onClick: () -> Unit, modifier: Modifier = Modifier, showsArtist: Boolean = true) {
    val state = item.requestState(sent = false)
    val stateLine = state.takeIf { it == MusicRequestState.InLibrary || it == MusicRequestState.Requested }?.label(item.isArtist)
    if (item.isArtist) {
        val size = 110.dp
        Column(modifier.width(size + 15.dp), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(9.dp)) {
            TvArtworkButton(ambient = Flexarr.silver, onClick = onClick, shape = CircleShape, contentDescription = item.title, artwork = { TvMusicArtwork(item.image, item.title, circular = true, size = size) })
            Column(horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(1.dp)) {
                Text(item.title, fontSize = 12.sp, fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis)
                stateLine?.let { Text(it, fontSize = 11.5.sp, color = Flexarr.usher, maxLines = 1) }
            }
        }
    } else {
        val width = TvLayout.PosterWidth
        TvArtworkButton(
            ambient = Flexarr.silver,
            onClick = onClick,
            modifier = modifier,
            shape = RoundedCornerShape(Tokens.radiusArt),
            contentDescription = listOfNotNull(item.title, item.artist).joinToString(", "),
            artwork = { TvMusicArtwork(item.image, item.title, circular = false, size = width) },
            caption = { focused ->
                Column(Modifier.width(width), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                    Text(item.title, fontSize = 12.sp, fontWeight = FontWeight.SemiBold, color = if (focused) Flexarr.screen else Flexarr.screen.copy(alpha = 0.8f), maxLines = 1, overflow = TextOverflow.Ellipsis)
                    val below = stateLine ?: if (showsArtist) item.artist ?: item.kindLine else item.kindLine
                    below?.let { Text(it, fontSize = 11.5.sp, color = Flexarr.usher, maxLines = 1, overflow = TextOverflow.Ellipsis) }
                }
            },
        )
    }
}

/** A heading with a row of outside artists or albums under it; [cardModifier] lets the page steer focus to a card. */
@Composable
internal fun TvMusicExternalRow(title: String, items: List<MusicExternal>, cardModifier: (MusicExternal) -> Modifier = { Modifier }) {
    val navigator = LocalNavigator.current
    Column {
        TvRowHeading(title)
        LazyRow(
            contentPadding = PaddingValues(start = TvLayout.Edge, end = TvLayout.Edge, top = 14.dp, bottom = 20.dp),
            horizontalArrangement = Arrangement.spacedBy(TvLayout.RowSpacing),
            verticalAlignment = Alignment.Top,
        ) {
            // The same artist or album can be in a list twice (a joint album under each artist): its place keeps the keys apart.
            itemsIndexed(items, key = { index, item -> "$index-${item.id}" }) { _, item -> TvMusicExternalCard(item, onClick = { navigator.openMusic(item) }, modifier = cardModifier(item)) }
        }
    }
}

/**
 * An album from outside the library with Request album under it (or where it stands: "In your library" opens it).
 * Down from the cover reaches it. A failed request says so along the bottom.
 */
@Composable
internal fun TvRequestableAlbum(model: AppModel, item: MusicExternal, modifier: Modifier = Modifier) {
    val navigator = LocalNavigator.current
    var sent by remember(item.id) { mutableStateOf(false) }
    val state = item.requestState(sent)
    Column(modifier.width(TvLayout.PosterWidth), verticalArrangement = Arrangement.spacedBy(6.dp)) {
        TvMusicExternalCard(item, onClick = { navigator.openMusic(item) }, showsArtist = false)
        TvPill(
            state.label(isArtist = false),
            selected = state == MusicRequestState.CanRequest,
            onClick = {
                when (state) {
                    MusicRequestState.CanRequest -> {
                        sent = true
                        navigator.requestMusic(model, item) { sent = false }
                    }
                    MusicRequestState.InLibrary -> navigator.openMusic(item)
                    else -> Unit
                }
            },
        )
    }
}

/** Outside albums six across, each with Request album, in rows of the page's list. */
internal fun LazyListScope.requestableAlbumRows(model: AppModel, albums: List<MusicExternal>, key: String) {
    val across = 6
    val spacing = (960.dp - TvLayout.Edge * 2 - TvLayout.PosterWidth * across) / (across - 1)
    itemsIndexed(albums.chunked(across), key = { index, _ -> "$key-$index" }) { _, row ->
        Row(Modifier.padding(start = TvLayout.Edge, end = TvLayout.Edge, bottom = 10.dp), horizontalArrangement = Arrangement.spacedBy(spacing), verticalAlignment = Alignment.Top) {
            row.forEach { TvRequestableAlbum(model, it) }
        }
    }
}

private sealed interface MusicSending {
    data object Idle : MusicSending
    data object InFlight : MusicSending
    data object Sent : MusicSending
    data class Failed(val message: String) : MusicSending
}

/** How long after a page opens before its button takes a press: a quick second press on the card isn't a request. */
private const val MUSIC_SETTLE_MS = 400L

/**
 * Follow artist or Request album, or where it stands ("In your library" opens it), with what went wrong beside it.
 * One button throughout, whatever it says, so focus never lands on nothing.
 */
@Composable
private fun TvMusicRequestButton(client: FlexarrClient, item: MusicExternal, focus: FocusMemory) {
    val navigator = LocalNavigator.current
    var sending by remember(item.id) { mutableStateOf<MusicSending>(MusicSending.Idle) }
    val scope = rememberCoroutineScope()
    val shownAt = remember(item.id) { SystemClock.uptimeMillis() }
    val state = item.requestState(sending == MusicSending.Sent)
    val failure = (sending as? MusicSending.Failed)?.message
    val label = when {
        state != MusicRequestState.CanRequest -> state.label(item.isArtist)
        sending == MusicSending.InFlight -> if (item.isArtist) "Following…" else "Requesting…"
        failure != null -> "Try again"
        else -> state.label(item.isArtist)
    }
    Row(Modifier.padding(top = 4.dp), horizontalArrangement = Arrangement.spacedBy(15.dp), verticalAlignment = Alignment.CenterVertically) {
        TvActionButton(
            label,
            onClick = {
                when {
                    state == MusicRequestState.InLibrary -> navigator.openMusic(item)
                    state != MusicRequestState.CanRequest || sending == MusicSending.InFlight || SystemClock.uptimeMillis() - shownAt <= MUSIC_SETTLE_MS -> Unit
                    else -> {
                        sending = MusicSending.InFlight
                        scope.launch {
                            sending = try {
                                if (item.isArtist) client.followArtist(item.deezerId) else client.requestAlbum(item)
                                MusicSending.Sent
                            } catch (e: CancellationException) {
                                throw e
                            } catch (e: Exception) {
                                MusicSending.Failed(e.message ?: "Something went wrong.")
                            }
                        }
                    }
                }
            },
            icon = state.icon,
            prominent = state == MusicRequestState.CanRequest,
            modifier = Modifier.focusMemory(focus, "request"),
        )
        failure?.let {
            Text(it, fontSize = 12.sp, fontWeight = FontWeight.Medium, color = Flexarr.screen.copy(alpha = 0.85f), maxLines = 2, overflow = TextOverflow.Ellipsis, modifier = Modifier.widthIn(max = 350.dp))
        }
    }
}

/** The art at the left, then the name, what's under it, and the page's button, as the library's music pages. */
@Composable
private fun TvExternalMusicHeader(title: String, titleSize: Int, art: @Composable () -> Unit, below: @Composable () -> Unit, button: @Composable () -> Unit) {
    Row(Modifier.padding(horizontal = TvLayout.Edge), horizontalArrangement = Arrangement.spacedBy(30.dp), verticalAlignment = Alignment.CenterVertically) {
        art()
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            Text(title, style = marquee(titleSize).copy(fontWeight = FontWeight.ExtraBold), maxLines = 2, overflow = TextOverflow.Ellipsis)
            below()
            button()
        }
    }
}

private fun loadFailure(e: Exception, what: String): String =
    if (e is FlexarrException.Http && e.status == 404) "Flexarr can’t find that $what." else e.message ?: "Something went wrong."

/**
 * An artist from outside the library: photo and name, Follow artist, their albums each with Request album, and Fans
 * also like. One the household has opens as itself.
 */
@Composable
fun TvExternalArtistScreen(model: AppModel, deezerId: Long) {
    val client = model.client ?: return
    val page = rememberLoader(deezerId) {
        try {
            client.musicArtist(deezerId)
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            throw IllegalStateException(loadFailure(e, "artist"), e)
        }
    }
    (page.state as? Load.Loaded)?.value?.libraryItemId?.let { id ->
        TvArtistScreen(model, id)
        return
    }
    val focus = rememberFocusMemory()
    val loaded = page.state is Load.Loaded
    LaunchedEffect(loaded) { if (loaded) focus.restore("request") }
    RoomBackground(ambient = Flexarr.silver, intensity = 0.4f) {
        when (val state = page.state) {
            is Load.Loaded -> {
                val artist = state.value
                LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(top = 65.dp, bottom = 45.dp), verticalArrangement = Arrangement.spacedBy(14.dp)) {
                    item(key = "header") {
                        TvExternalMusicHeader(
                            title = artist.name,
                            titleSize = 42,
                            art = { Box(Modifier.shadow(15.dp, CircleShape)) { TvMusicArtwork(artist.image, artist.name, circular = true, size = 180.dp) } },
                            below = { if (artist.albums.isNotEmpty()) TvFactsLine(listOf(if (artist.albums.size == 1) "1 album" else "${artist.albums.size} albums")) },
                            button = { TvMusicRequestButton(client, artist.summary, focus) },
                        )
                    }
                    if (artist.albums.isNotEmpty()) {
                        item(key = "albums-heading") { TvRowHeading("Albums", Modifier.padding(top = 14.dp)) }
                        requestableAlbumRows(model, artist.albums, "albums")
                    }
                    if (artist.similar.isNotEmpty()) {
                        item(key = "similar") { TvMusicExternalRow("Fans also like", artist.similar, cardModifier = { Modifier.focusMemory(focus, "similar/${it.id}") }) }
                    }
                }
            }
            is Load.Failed -> TvStatusView(state.message, retry = page.retry)
            Load.Loading -> TvStatusView()
        }
    }
}

/** The artist as a card knows them: followed already reads as Requested ("Following"). */
private val MusicArtistDetail.summary: MusicExternal
    get() = MusicExternal("artist", deezerId, name, image = image, availability = if (followed) Availability.Requested else availability, libraryItemId = libraryItemId)

/**
 * An album from outside the library: cover, title, artist (opening theirs) and year, Request album, and its tracks.
 * One the household has opens as itself.
 */
@Composable
fun TvExternalAlbumScreen(model: AppModel, deezerId: Long) {
    val client = model.client ?: return
    val navigator = LocalNavigator.current
    val page = rememberLoader(deezerId) {
        try {
            client.musicAlbum(deezerId)
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            throw IllegalStateException(loadFailure(e, "album"), e)
        }
    }
    (page.state as? Load.Loaded)?.value?.libraryItemId?.let { id ->
        TvAlbumScreen(model, id)
        return
    }
    val focus = rememberFocusMemory()
    val loaded = page.state is Load.Loaded
    LaunchedEffect(loaded) { if (loaded) focus.restore("request") }
    RoomBackground(ambient = Flexarr.silver, intensity = 0.4f) {
        when (val state = page.state) {
            is Load.Loaded -> {
                val album = state.value
                LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(top = 65.dp, bottom = 45.dp), verticalArrangement = Arrangement.spacedBy(3.dp)) {
                    item(key = "header") {
                        TvExternalMusicHeader(
                            title = album.title,
                            titleSize = 36,
                            art = { Box(Modifier.shadow(15.dp, RoundedCornerShape(Tokens.radiusArt))) { TvMusicArtwork(album.image, album.title, circular = false, size = 210.dp) } },
                            below = {
                                val artistId = album.artistDeezerId
                                if (artistId != null) {
                                    TvPill(album.artist, selected = false, onClick = { navigator.openMusicArtist(artistId) })
                                } else {
                                    Text(album.artist, fontSize = 15.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen.copy(alpha = 0.9f))
                                }
                                TvFactsLine(albumFacts(album))
                            },
                            button = { TvMusicRequestButton(client, album.summary, focus) },
                        )
                    }
                    item(key = "gap") { Spacer(Modifier.height(21.dp)) }
                    val multiDisc = album.tracks.map { it.disc }.distinct().size > 1
                    itemsIndexed(album.tracks, key = { index, _ -> "track-$index" }) { index, track ->
                        if (multiDisc && (index == 0 || album.tracks[index - 1].disc != track.disc)) {
                            Text(
                                "Disc ${track.disc}",
                                style = marquee(17, Flexarr.usher).copy(fontWeight = FontWeight.Bold),
                                modifier = Modifier.padding(start = TvLayout.Edge + 15.dp, top = if (index == 0) 0.dp else 15.dp, bottom = 5.dp),
                            )
                        }
                        OutsideTrackRow(track, if (track.number > 0) track.number else index + 1, Modifier.padding(horizontal = TvLayout.Edge).widthIn(max = 700.dp).fillMaxWidth())
                    }
                }
            }
            is Load.Failed -> TvStatusView(state.message, retry = page.retry)
            Load.Loading -> TvStatusView()
        }
    }
}

/** "Album, 2001   14 tracks   1h 1m". */
private fun albumFacts(album: MusicAlbumDetail): List<String> = buildList {
    album.summary.kindLine?.let(::add)
    if (album.tracks.isNotEmpty()) add(if (album.tracks.size == 1) "1 track" else "${album.tracks.size} tracks")
    if (album.durationSeconds > 0) Format.runtime(album.durationSeconds.toDouble())?.let(::add)
}

/**
 * A track on an album the library doesn't have: number, title and length. Focusable, so the remote can move down the
 * list, though there's nothing to play.
 */
@Composable
private fun OutsideTrackRow(track: MusicTrack, number: Int, modifier: Modifier) {
    Surface(
        onClick = {},
        modifier = modifier.height(40.dp),
        shape = ClickableSurfaceDefaults.shape(RoundedCornerShape(Tokens.radiusControl)),
        colors = ClickableSurfaceDefaults.colors(
            containerColor = Color.Transparent,
            contentColor = Flexarr.screen.copy(alpha = 0.82f),
            focusedContainerColor = Flexarr.screen.copy(alpha = 0.16f),
            focusedContentColor = Flexarr.screen,
        ),
        scale = ClickableSurfaceDefaults.scale(focusedScale = 1.02f),
    ) {
        Row(Modifier.fillMaxSize().padding(horizontal = 15.dp), horizontalArrangement = Arrangement.spacedBy(14.dp), verticalAlignment = Alignment.CenterVertically) {
            Box(Modifier.width(28.dp), contentAlignment = Alignment.CenterEnd) {
                Text(number.toString(), fontSize = 13.sp, fontWeight = FontWeight.Medium, style = Tabular)
            }
            Text(track.title, fontSize = 14.sp, fontWeight = FontWeight.Medium, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f))
            if (track.durationSeconds > 0) Text(Format.timestamp(track.durationSeconds.toDouble()), fontSize = 12.sp, fontWeight = FontWeight.Medium, style = Tabular)
        }
    }
}
