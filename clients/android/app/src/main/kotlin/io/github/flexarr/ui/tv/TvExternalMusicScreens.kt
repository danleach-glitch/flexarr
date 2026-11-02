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
import io.github.flexarr.core.unfollowArtist
import io.github.flexarr.core.unrequestAlbum
import io.github.flexarr.ui.components.MenuAction
import io.github.flexarr.ui.music.isUndoingMusic
import io.github.flexarr.ui.music.musicUndoConfirm
import io.github.flexarr.ui.music.musicUndoKeep
import io.github.flexarr.ui.music.musicUndoTitle
import androidx.compose.ui.semantics.hideFromAccessibility
import androidx.compose.ui.semantics.semantics
import io.github.flexarr.ui.components.Load
import io.github.flexarr.ui.components.LocalNavigator
import io.github.flexarr.ui.components.RemoteImage
import io.github.flexarr.ui.components.rememberLoader
import androidx.compose.ui.platform.LocalContext
import io.github.flexarr.appModel
import io.github.flexarr.core.releaseShelves
import io.github.flexarr.ui.music.LidarrNotAnswering
import io.github.flexarr.ui.music.MusicRequestState
import io.github.flexarr.ui.music.canAskForMusic
import io.github.flexarr.ui.music.followState
import io.github.flexarr.ui.music.icon
import io.github.flexarr.ui.music.label
import io.github.flexarr.ui.music.requestState
import io.github.flexarr.ui.theme.FlexarrIcons
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
 * An artist (round) or album (square) from outside the library, with its name and where it stands (asked for here
 * included). Pressing opens it, as itself when the household has it.
 */
@Composable
internal fun TvMusicExternalCard(item: MusicExternal, onClick: () -> Unit, modifier: Modifier = Modifier, showsArtist: Boolean = true) {
    val model = LocalContext.current.appModel
    val state = model.requestState(item)
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
 * A music request sent from one control: once (a second press while it's on its way sends nothing), on the app's
 * scope so leaving the page doesn't cut it off, and with what went wrong kept for saying so beside the control.
 */
internal class TvMusicAsk(private val model: AppModel) {
    var failure by mutableStateOf<String?>(null)
        private set

    fun send(keys: List<String>, request: suspend (FlexarrClient) -> Unit) {
        failure = null
        model.scope.launch {
            try {
                model.askForMusic(keys, request)
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                failure = e.message ?: "Something went wrong."
            }
        }
    }

    /** Unfollows or unrequests, once, as [send] does its asking. */
    fun undo(keys: List<String>, request: suspend (FlexarrClient) -> Unit) {
        failure = null
        model.scope.launch {
            try {
                model.undoMusic(keys, request)
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                failure = e.message ?: "Something went wrong."
            }
        }
    }
}

@Composable
internal fun rememberTvMusicAsk(model: AppModel, key: String): TvMusicAsk = remember(key) { TvMusicAsk(model) }

/** What a request control says: where it stands, Try again after a failure, or nothing to offer (null). */
internal fun MusicRequestState.tvLabel(isArtist: Boolean, failure: String?): String? =
    if (this == MusicRequestState.CanRequest && failure != null) "Try again" else label(isArtist)

/** What went wrong, beside or under a request control. */
@Composable
internal fun TvMusicFailure(message: String, modifier: Modifier = Modifier) {
    Text(message, fontSize = 12.sp, fontWeight = FontWeight.Medium, color = Flexarr.screen.copy(alpha = 0.85f), maxLines = 2, overflow = TextOverflow.Ellipsis, modifier = modifier.widthIn(max = 350.dp))
}

/**
 * An album from outside the library with Request album under it (or where it stands: "In your library" opens it);
 * nothing under it when there's nothing to offer. Down from the cover reaches it. A failed request says so under it,
 * with Try again. Both remember focus in [focus], so coming back from the album gives it back.
 */
@Composable
internal fun TvRequestableAlbum(model: AppModel, item: MusicExternal, focus: FocusMemory?, modifier: Modifier = Modifier) {
    val navigator = LocalNavigator.current
    val ask = rememberTvMusicAsk(model, item.id)
    val state = model.requestState(item)
    val label = state.tvLabel(isArtist = false, ask.failure)
    fun Modifier.remembered(key: String) = if (focus != null) focusMemory(focus, key) else this
    Column(modifier.width(TvLayout.PosterWidth), verticalArrangement = Arrangement.spacedBy(6.dp)) {
        TvMusicExternalCard(item, onClick = { navigator.openMusic(item) }, showsArtist = false, modifier = Modifier.remembered("album/${item.id}"))
        if (label != null) {
            TvPill(
                label,
                selected = state == MusicRequestState.CanRequest,
                onClick = {
                    when (state) {
                        MusicRequestState.CanRequest -> if (model.canAskForMusic) ask.send(listOf(item.id)) { it.requestAlbum(item) }
                        MusicRequestState.InLibrary -> navigator.openMusic(item)
                        else -> Unit
                    }
                },
                modifier = Modifier.remembered("album/${item.id}/request"),
            )
        }
        ask.failure?.takeIf { state == MusicRequestState.CanRequest }?.let { TvMusicFailure(it) }
    }
}

/** Outside albums six across, each with Request album, in rows of the page's list. */
internal fun LazyListScope.requestableAlbumRows(model: AppModel, albums: List<MusicExternal>, key: String, focus: FocusMemory? = null) {
    val across = 6
    val spacing = (960.dp - TvLayout.Edge * 2 - TvLayout.PosterWidth * across) / (across - 1)
    itemsIndexed(albums.chunked(across), key = { index, _ -> "$key-$index" }) { _, row ->
        Row(Modifier.padding(start = TvLayout.Edge, end = TvLayout.Edge, bottom = 10.dp), horizontalArrangement = Arrangement.spacedBy(spacing), verticalAlignment = Alignment.Top) {
            row.forEach { TvRequestableAlbum(model, it, focus) }
        }
    }
}

/** How long after a page opens before its button takes a press: a quick second press on the card isn't a request. */
private const val MUSIC_SETTLE_MS = 400L

/**
 * Follow artist or Request album ([state]), or where it stands ("In your library" opens it), with what went wrong
 * beside it and Try again. One button throughout, whatever it says, so focus never lands on nothing; none when there's
 * nothing to offer.
 */
@Composable
internal fun TvMusicRequestButton(
    model: AppModel,
    item: MusicExternal,
    state: MusicRequestState,
    keys: List<String>,
    modifier: Modifier = Modifier,
    ambient: Color = Flexarr.silver,
    prominent: Boolean = state == MusicRequestState.CanRequest,
    request: suspend (FlexarrClient) -> Unit,
) {
    val navigator = LocalNavigator.current
    val ask = rememberTvMusicAsk(model, keys.first())
    val shownAt = remember(item.id) { SystemClock.uptimeMillis() }
    val label = state.tvLabel(item.isArtist, ask.failure) ?: return
    Row(horizontalArrangement = Arrangement.spacedBy(15.dp), verticalAlignment = Alignment.CenterVertically) {
        TvActionButton(
            label,
            onClick = {
                when {
                    state == MusicRequestState.InLibrary -> navigator.openMusic(item)
                    state != MusicRequestState.CanRequest || SystemClock.uptimeMillis() - shownAt <= MUSIC_SETTLE_MS -> Unit
                    else -> ask.send(keys, request)
                }
            },
            icon = state.icon(item.isArtist),
            prominent = prominent,
            ambient = ambient,
            enabled = (state == MusicRequestState.CanRequest && model.canAskForMusic) || state == MusicRequestState.InLibrary,
            modifier = modifier,
        )
        ask.failure?.takeIf { state == MusicRequestState.CanRequest }?.let { TvMusicFailure(it) }
    }
}

/**
 * Follow artist or Request album as a round icon in a page's row, its name under the row while it has focus
 * ([onFocusName]): Following or Requested lit, and pressing it asks once before undoing. What went wrong shows beside
 * it. Nothing for [MusicRequestState.InLibrary] or [MusicRequestState.None]: the page's call.
 */
@Composable
internal fun TvMusicRequestRound(
    model: AppModel,
    item: MusicExternal,
    state: MusicRequestState,
    keys: List<String>,
    onFocusName: (String?) -> Unit,
    modifier: Modifier = Modifier,
    ambient: Color = Flexarr.silver,
    request: suspend (FlexarrClient) -> Unit,
    undo: suspend (FlexarrClient) -> Unit,
) {
    val ask = rememberTvMusicAsk(model, keys.first())
    val shownAt = remember(item.id) { SystemClock.uptimeMillis() }
    var confirming by remember { mutableStateOf(false) }
    val icon = state.icon(item.isArtist) ?: return
    if (state == MusicRequestState.InLibrary) return
    val isArtist = item.isArtist
    val on = state == MusicRequestState.Requested || state == MusicRequestState.Sending
    val name = when {
        on -> if (isArtist) "Following" else "Requested"
        ask.failure != null -> "Try again"
        else -> state.label(isArtist).orEmpty()
    }
    val enabled = model.canAskForMusic && !model.isUndoingMusic(keys) && (state == MusicRequestState.CanRequest || state == MusicRequestState.Requested)
    Row(horizontalArrangement = Arrangement.spacedBy(15.dp), verticalAlignment = Alignment.CenterVertically) {
        RoundAction(name, icon, ambient, onFocusName, modifier, enabled = enabled, selected = on) {
            when {
                state == MusicRequestState.Requested -> confirming = true
                state != MusicRequestState.CanRequest || SystemClock.uptimeMillis() - shownAt <= MUSIC_SETTLE_MS -> Unit
                else -> ask.send(keys, request)
            }
        }
        ask.failure?.let { TvMusicFailure(it) }
    }
    if (confirming) {
        TvItemMenu(
            musicUndoTitle(isArtist, item.title),
            listOf(MenuAction(musicUndoConfirm(isArtist)) { ask.undo(keys, undo) }, MenuAction(musicUndoKeep(isArtist)) {}),
            onDismiss = { confirming = false },
        )
    }
}

/**
 * The art at the left, then the name, what's under it, and the page's buttons, as the library's music pages; the
 * focused round button's name under them.
 */
@Composable
private fun TvExternalMusicHeader(title: String, titleSize: Int, art: @Composable () -> Unit, below: @Composable () -> Unit, buttons: @Composable (onFocusName: (String?) -> Unit) -> Unit) {
    var focusedName by remember { mutableStateOf<String?>(null) }
    Row(Modifier.padding(horizontal = TvLayout.Edge), horizontalArrangement = Arrangement.spacedBy(30.dp), verticalAlignment = Alignment.CenterVertically) {
        art()
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            Text(title, style = marquee(titleSize).copy(fontWeight = FontWeight.ExtraBold), maxLines = 2, overflow = TextOverflow.Ellipsis)
            below()
            Column(verticalArrangement = Arrangement.spacedBy(9.dp)) {
                Row(Modifier.padding(top = 4.dp), horizontalArrangement = Arrangement.spacedBy(13.dp), verticalAlignment = Alignment.CenterVertically) { buttons { focusedName = it } }
                // The line keeps its height so nothing jumps.
                Text(focusedName ?: " ", fontSize = 12.sp, fontWeight = FontWeight.Medium, color = Flexarr.screen.copy(alpha = 0.75f), modifier = Modifier.semantics { hideFromAccessibility() })
            }
        }
    }
}

private fun loadFailure(e: Exception, what: String): String =
    if (e is FlexarrException.Http && e.status == 404) "Flexarr can’t find that $what." else e.message ?: "Something went wrong."

/**
 * An artist from outside the library: photo and name, Follow artist, their releases (albums, EPs and singles,
 * compilations) each with Request album, and Fans also like. One the household has adds In your library, opening
 * theirs. Coming back from one of their albums, focus goes back to it.
 */
@Composable
fun TvExternalArtistScreen(model: AppModel, deezerId: Long) {
    val client = model.client ?: return
    val navigator = LocalNavigator.current
    val page = rememberLoader(deezerId) {
        try {
            client.musicArtist(deezerId)
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            throw IllegalStateException(loadFailure(e, "artist"), e)
        }
    }
    val focus = rememberFocusMemory()
    val loaded = page.state is Load.Loaded
    LaunchedEffect(loaded) { if (loaded) focus.restore("request") }
    RoomBackground(ambient = Flexarr.silver, intensity = 0.4f) {
        when (val state = page.state) {
            is Load.Loaded -> {
                val artist = state.value
                val summary = artist.summary
                val libraryId = artist.libraryItemId
                val keys = listOfNotNull(summary.id, libraryId?.let { "library-artist-$it" })
                val follow = model.followState(keys, artist.followed || artist.availability == Availability.Requested)
                LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(top = 65.dp, bottom = 45.dp), verticalArrangement = Arrangement.spacedBy(14.dp)) {
                    item(key = "header") {
                        TvExternalMusicHeader(
                            title = artist.name,
                            titleSize = 42,
                            art = { Box(Modifier.shadow(15.dp, CircleShape)) { TvMusicArtwork(artist.image, artist.name, circular = true, size = 180.dp) } },
                            below = {
                                if (model.musicUnreachable) Text(LidarrNotAnswering, fontSize = 13.sp, fontWeight = FontWeight.Medium, color = Flexarr.usher)
                                if (artist.albums.isNotEmpty()) TvFactsLine(listOf(if (artist.albums.size == 1) "1 album" else "${artist.albums.size} albums"))
                            },
                            buttons = { onFocusName ->
                                if (libraryId != null) {
                                    TvActionButton("In your library", onClick = { navigator.openLibraryArtist(libraryId) }, icon = FlexarrIcons.music, prominent = true, modifier = Modifier.focusMemory(focus, "library"))
                                }
                                // Nothing of theirs plays here: Follow is the page's one action (MusicActions).
                                TvMusicRequestRound(
                                    model, summary, follow, keys, onFocusName,
                                    modifier = Modifier.focusMemory(focus, "request"),
                                    request = { it.followArtist(deezerId) },
                                    undo = { it.unfollowArtist(deezerId = deezerId) },
                                )
                            },
                        )
                    }
                    artist.albums.releaseShelves().forEach { (title, albums) ->
                        item(key = "heading-$title") { TvRowHeading(title, Modifier.padding(top = 14.dp)) }
                        requestableAlbumRows(model, albums, title, focus)
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

/** The artist as a card knows them, for Follow artist. */
private val MusicArtistDetail.summary: MusicExternal
    get() = MusicExternal("artist", deezerId, name, image = image, availability = availability, libraryItemId = libraryItemId)

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
    // The request button, or (with nothing to request) the artist, else the first track.
    LaunchedEffect(loaded) {
        val album = (page.state as? Load.Loaded)?.value ?: return@LaunchedEffect
        val fallback = when {
            model.requestState(album.summary) != MusicRequestState.None -> "request"
            album.artistDeezerId != null -> "artist"
            else -> "track-0"
        }
        focus.restore(fallback)
    }
    RoomBackground(ambient = Flexarr.silver, intensity = 0.4f) {
        when (val state = page.state) {
            is Load.Loaded -> {
                val album = state.value
                val summary = album.summary
                LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(top = 65.dp, bottom = 45.dp), verticalArrangement = Arrangement.spacedBy(3.dp)) {
                    item(key = "header") {
                        TvExternalMusicHeader(
                            title = album.title,
                            titleSize = 36,
                            art = { Box(Modifier.shadow(15.dp, RoundedCornerShape(Tokens.radiusArt))) { TvMusicArtwork(album.image, album.title, circular = false, size = 210.dp) } },
                            below = {
                                val artistId = album.artistDeezerId
                                if (artistId != null) {
                                    TvPill(album.artist, selected = false, onClick = { navigator.openMusicArtist(artistId) }, modifier = Modifier.focusMemory(focus, "artist"))
                                } else {
                                    Text(album.artist, fontSize = 15.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen.copy(alpha = 0.9f))
                                }
                                TvFactsLine(albumFacts(album))
                                if (model.musicUnreachable) Text(LidarrNotAnswering, fontSize = 13.sp, fontWeight = FontWeight.Medium, color = Flexarr.usher)
                            },
                            buttons = { onFocusName ->
                                val request = model.requestState(summary)
                                if (request == MusicRequestState.InLibrary) {
                                    TvMusicRequestButton(model, summary, request, listOf(summary.id), modifier = Modifier.focusMemory(focus, "request")) { it.requestAlbum(summary) }
                                } else {
                                    TvMusicRequestRound(
                                        model, summary, request, listOf(summary.id), onFocusName,
                                        modifier = Modifier.focusMemory(focus, "request"),
                                        request = { it.requestAlbum(summary) },
                                        undo = { it.unrequestAlbum(summary) },
                                    )
                                }
                            },
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
                        OutsideTrackRow(track, if (track.number > 0) track.number else index + 1, Modifier.padding(horizontal = TvLayout.Edge).widthIn(max = 700.dp).fillMaxWidth().focusMemory(focus, "track-$index"))
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
