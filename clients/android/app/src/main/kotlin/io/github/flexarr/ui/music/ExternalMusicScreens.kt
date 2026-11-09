package io.github.flexarr.ui.music

import io.github.flexarr.core.i18n.t
import io.github.flexarr.core.i18n.Words

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
import androidx.compose.material3.Text
import androidx.compose.foundation.text.BasicText
import androidx.compose.foundation.text.TextAutoSize
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.compositionLocalOf
import androidx.compose.ui.text.TextStyle
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.foundation.border
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import io.github.flexarr.ui.theme.mainButtonFill
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
import androidx.compose.foundation.lazy.grid.itemsIndexed
import androidx.compose.foundation.lazy.itemsIndexed as rowItemsIndexed
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
import io.github.flexarr.core.followLibraryArtist
import io.github.flexarr.core.releaseShelves
import androidx.compose.ui.semantics.Role
import io.github.flexarr.ui.theme.FlexarrIcons
import io.github.flexarr.core.requestAlbum
import io.github.flexarr.core.FlexarrClient
import io.github.flexarr.core.MusicActions
import io.github.flexarr.core.unfollowArtist
import io.github.flexarr.core.unrequestAlbum
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Icon
import androidx.compose.material3.TextButton
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.selected
import androidx.compose.ui.semantics.semantics
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

/** Where an outside artist or album stands, for its control: ask for it, asked, in, or nothing to offer. */
internal enum class MusicRequestState { CanRequest, Requested, InLibrary, None }

/** Where [item] stands, counting what this profile asked for here ([AppModel.musicAsked]) ahead of the server. */
internal fun AppModel.requestState(item: MusicExternal): MusicRequestState = when {
    item.libraryItemId != null || item.availability == Availability.Available || item.availability == Availability.PartlyAvailable -> MusicRequestState.InLibrary
    item.id in musicAsked -> MusicRequestState.Requested
    // Unrequested here: Request album again, though the page it came from still says the server's Requested.
    item.availability == Availability.Requested && item.id !in musicUndone -> MusicRequestState.Requested
    item.canRequest || (item.id in musicUndone && item.availability == Availability.Requested && (item.deezerId != 0L || item.musicBrainzId != null)) -> MusicRequestState.CanRequest
    // A kind from a newer server, or nothing to ask Lidarr by: no control at all.
    else -> MusicRequestState.None
}

/** The one quiet line when Lidarr is connected but not answering, as the Apple apps' MusicWords. */
internal val LidarrNotAnswering get() = t("Lidarr isn’t answering.")

/** Whether Lidarr can take a request now: not when it isn't answering. */
internal val AppModel.canAskForMusic: Boolean get() = !musicUnreachable

/**
 * The page's own answer said Lidarr didn't answer for it (`lidarrAnswered` false), so whether things on it are followed
 * or requested couldn't be checked: its Follow, Request album and album chips wait, and the page says so.
 */
internal val LocalMusicPaused = compositionLocalOf { false }

/** Whether Lidarr can take a request on this page now: not while it isn't answering, here ([LocalMusicPaused]) or at all. */
internal val AppModel.canAskForMusicHere: Boolean
    @Composable get() = canAskForMusic && !LocalMusicPaused.current

/** Whether this page says "Lidarr isn't answering." under its buttons. */
internal val AppModel.lidarrSilentHere: Boolean
    @Composable get() = musicUnreachable || LocalMusicPaused.current

/** "Request album", "Follow", "Requested", "Following" or "In your library"; null for nothing to offer. */
internal fun MusicRequestState.label(isArtist: Boolean): String? = when (this) {
    MusicRequestState.CanRequest -> if (isArtist) t(Words.follow) else t(Words.requestAlbum)
    MusicRequestState.Requested -> if (isArtist) t(Words.following) else t(Words.requested)
    MusicRequestState.InLibrary -> t(Words.inYourLibrary)
    MusicRequestState.None -> null
}

internal fun MusicRequestState.icon(isArtist: Boolean): ImageVector? = when (this) {
    MusicRequestState.CanRequest -> if (isArtist) FlexarrIcons.follow else FlexarrIcons.request
    MusicRequestState.Requested -> if (isArtist) FlexarrIcons.following else FlexarrIcons.requested
    MusicRequestState.InLibrary -> FlexarrIcons.music
    MusicRequestState.None -> null
}

/** The keys a library artist's follow is kept under: theirs here, and theirs on Deezer when known. */
internal fun libraryArtistKeys(id: String, deezerId: Long?): List<String> = listOfNotNull("library-artist-$id", deezerId?.let { "artist-$it" })

/** Follow or Following for an artist: [followed] by the server's word, or asked for here under any of [keys]. */
internal fun AppModel.followState(keys: List<String>, followed: Boolean): MusicRequestState = when {
    keys.any { it in musicAsked } -> MusicRequestState.Requested
    // Unfollowed here: Follow again, though the page still has the server's word.
    followed && keys.none { it in musicUndone } -> MusicRequestState.Requested
    else -> MusicRequestState.CanRequest
}

/** The question before Following or Requested is undone, its answer and the way out, as the web's. */
internal fun musicUndoTitle(isArtist: Boolean, name: String) = if (isArtist) t("Stop following {name}?", "name" to name) else t("Unrequest {name}?", "name" to name)
internal fun musicUndoConfirm(isArtist: Boolean) = if (isArtist) t(Words.stopFollowing) else t(Words.unrequest)
internal fun musicUndoKeep(isArtist: Boolean) = if (isArtist) t("Keep following") else t("Keep it")

/** Asked once before Following or Requested is undone. */
@Composable
internal fun MusicUndoDialog(isArtist: Boolean, name: String, onConfirm: () -> Unit, onDismiss: () -> Unit) {
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(musicUndoTitle(isArtist, name)) },
        confirmButton = { TextButton(onClick = { onDismiss(); onConfirm() }) { Text(musicUndoConfirm(isArtist)) } },
        dismissButton = { TextButton(onClick = onDismiss) { Text(musicUndoKeep(isArtist)) } },
    )
}

/**
 * Stops following an artist ([send] says how to name them), once; the snackbar says if it didn't work. [onDone] runs
 * once it has (a placeholder artist's own page goes back: the server took them out of the library).
 */
internal fun Navigator.unfollowMusic(model: AppModel, keys: List<String>, onDone: () -> Unit = {}, send: suspend (FlexarrClient) -> Unit) {
    watchlistChange(t("Couldn’t stop following this artist")) { if (model.undoMusic(keys, send)) onDone() }
}

/** Unrequests an album, once; the snackbar says if it didn't work. */
internal fun Navigator.unrequestMusic(model: AppModel, item: MusicExternal) {
    watchlistChange(t("Couldn’t unrequest this album")) { model.undoMusic(listOf(item.id)) { it.unrequestAlbum(item) } }
}

/**
 * The on look (Following, Requested): lit in the page's colour with a ring of it, as the Apple apps', so it reads as on
 * without colour alone; off, the quiet translucent seat.
 */
internal fun Modifier.musicOnStyle(on: Boolean, ambient: Color, shape: Shape, offFill: Color = Flexarr.screen.copy(alpha = 0.14f)): Modifier =
    if (on) background(ambient.copy(alpha = 0.32f), shape).border(1.5.dp, ambient.copy(alpha = 0.8f), shape) else background(offFill, shape)

/**
 * One of a music page's round icon-only buttons (Shuffle, Radio, Follow, Request), named by [description] for
 * TalkBack; [on] lights it ([musicOnStyle]).
 */
@Composable
internal fun MusicRoundButton(
    icon: ImageVector,
    description: String,
    onClick: () -> Unit,
    ambient: Color = Flexarr.silver,
    enabled: Boolean = true,
    on: Boolean = false,
    dimmed: Boolean = !enabled,
    onClickLabel: String? = null,
) {
    Box(
        Modifier.size(48.dp).clip(CircleShape)
            .musicOnStyle(on, ambient, CircleShape)
            .alpha(if (dimmed) 0.5f else 1f)
            .pressable(onClick, enabled = enabled, onClickLabel = onClickLabel)
            .semantics {
                contentDescription = description
                if (on) selected = true
            },
        contentAlignment = Alignment.Center,
    ) {
        Icon(icon, contentDescription = null, tint = Flexarr.screen, modifier = Modifier.size(22.dp))
    }
}

/**
 * Follow or Request album in a page's action row: a round icon after Play and the rest, or [labelled] (icon and words)
 * when it's the page's only action. Following or Requested lit up ([musicOnStyle]), pressing it asking once
 * ([MusicUndoDialog]) before [onUndo]. Greyed while Lidarr isn't answering (the page says so under the row).
 */
@Composable
internal fun MusicRequestRoundButton(
    model: AppModel,
    state: MusicRequestState,
    isArtist: Boolean,
    name: String,
    ambient: Color,
    onRequest: () -> Unit,
    onUndo: () -> Unit,
    labelled: Boolean = false,
) {
    var confirming by remember { mutableStateOf(false) }
    val on = state == MusicRequestState.Requested
    val enabled = model.canAskForMusicHere && (state == MusicRequestState.CanRequest || state == MusicRequestState.Requested)
    val icon = state.icon(isArtist) ?: return
    val label = if (on) (if (isArtist) t(Words.following) else t(Words.requested)) else state.label(isArtist).orEmpty()
    val press = { if (state == MusicRequestState.CanRequest) onRequest() else confirming = true }
    val dimmed = !enabled
    val onClickLabel = if (on) musicUndoConfirm(isArtist) else null
    if (labelled) {
        val shape = RoundedCornerShape(Tokens.radiusControl)
        Row(
            Modifier.heightIn(min = 48.dp).clip(shape)
                .musicOnStyle(on, ambient, shape, offFill = mainButtonFill(ambient))
                .alpha(if (dimmed) 0.5f else 1f)
                .pressable(press, enabled = enabled, onClickLabel = onClickLabel)
                .semantics { if (on) selected = true }
                .padding(horizontal = 20.dp),
            horizontalArrangement = Arrangement.spacedBy(8.dp, Alignment.CenterHorizontally),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            // Off, it's the page's main action (the main button's fill, room-coloured words); on, the lit seat.
            val tint = if (on) Flexarr.screen else Flexarr.room
            Icon(icon, contentDescription = null, tint = tint, modifier = Modifier.size(20.dp))
            Text(label, color = tint, fontSize = 17.sp, fontWeight = FontWeight.SemiBold, maxLines = 1)
        }
    } else {
        MusicRoundButton(icon, label, press, ambient = ambient, enabled = enabled, on = on, dimmed = dimmed, onClickLabel = onClickLabel)
    }
    if (confirming) MusicUndoDialog(isArtist, name, onConfirm = onUndo, onDismiss = { confirming = false })
}

/** "Lidarr isn't answering." under a page's action row while its Follow or Request album is greyed for it. */
@Composable
internal fun LidarrNotAnsweringNote(model: AppModel) {
    if (model.lidarrSilentHere) Text(LidarrNotAnswering, fontSize = 13.sp, fontWeight = FontWeight.Medium, color = Flexarr.usher, textAlign = TextAlign.Center)
}

/**
 * Asks Lidarr for an album or to follow an artist, once (a second press while it's on its way sends nothing), saying
 * so in the snackbar if that didn't work. [keys]: what it's kept under ([AppModel.musicAsked]), the item's id by default.
 */
internal fun Navigator.requestMusic(model: AppModel, item: MusicExternal, keys: List<String> = listOf(item.id)) {
    watchlistChange(if (item.isArtist) t("Couldn’t follow this artist") else t("Couldn’t request this album")) {
        model.askForMusic(keys) { client -> if (item.isArtist) client.followArtist(item.deezerId) else client.requestAlbum(item) }
    }
}

/** Follows a library artist, by its id here, once; the snackbar says if it didn't work. */
internal fun Navigator.followLibraryArtist(model: AppModel, id: String, deezerId: Long?) {
    watchlistChange(t("Couldn’t follow this artist")) {
        model.askForMusic(libraryArtistKeys(id, deezerId)) { it.followLibraryArtist(id) }
    }
}

/** "Album", "EP", "Single" or "Compilation". */
private val MusicExternal.releaseKind: String
    get() = when (recordType) {
        "ep" -> t("EP")
        "single" -> t("Single")
        "compile" -> t("Compilation")
        else -> t("Album")
    }

/** Whether [item] has a page to open: the household's own, or Deezer's (an album only Lidarr lists has neither). */
internal val MusicExternal.hasPage: Boolean get() = libraryItemId != null || deezerId != 0L

/**
 * An artist (round photo) or album (square cover) from outside the library, with its name and a quieter line. Tapping
 * opens it (as itself when the household has it); a long press offers Request album or Follow, or Unrequest, wherever
 * the card is. One with no page of its own (an album only Lidarr lists) offers those when tapped, and isn't a button
 * when there's nothing to offer. [showsRequest] puts that under the card too, for a page of an artist's albums.
 * [showsKind] says "Artist" or "Album, <artist>" under the name, as Search's film cards say "Film, 2014".
 */
@Composable
fun MusicExternalCard(model: AppModel, item: MusicExternal, width: Dp?, modifier: Modifier = Modifier, showsRequest: Boolean = false, showsKind: Boolean = false) {
    val navigator = LocalNavigator.current
    var menu by remember { mutableStateOf(false) }
    var confirming by remember { mutableStateOf(false) }
    val state = model.requestState(item)
    val request = { navigator.requestMusic(model, item) }
    val canAsk = model.canAskForMusicHere
    val actions = when {
        !canAsk -> emptyList()
        state == MusicRequestState.CanRequest -> listOf(MenuAction(state.label(item.isArtist)!!, state.icon(item.isArtist)) { request() })
        state == MusicRequestState.Requested && !item.isArtist ->
            listOf(MenuAction(musicUndoConfirm(isArtist = false), FlexarrIcons.requested) { confirming = true })
        else -> emptyList()
    }
    val hasPage = item.hasPage
    Box(modifier.then(if (width != null) Modifier.width(width) else Modifier)) {
        Column(verticalArrangement = Arrangement.spacedBy(6.dp), horizontalAlignment = if (item.isArtist) Alignment.CenterHorizontally else Alignment.Start) {
            Column(
                Modifier.pressable(
                    { if (hasPage) navigator.openMusic(item) else menu = true },
                    enabled = hasPage || actions.isNotEmpty(),
                    onLongClick = { if (actions.isNotEmpty()) menu = true },
                ).menuActions(actions),
                verticalArrangement = Arrangement.spacedBy(if (item.isArtist) 8.dp else 6.dp),
                horizontalAlignment = if (item.isArtist) Alignment.CenterHorizontally else Alignment.Start,
            ) {
                MusicExternalArtwork(item, if (item.isArtist) Modifier.padding(horizontal = 6.dp) else Modifier)
                Column(Modifier.fillMaxWidth(), verticalArrangement = Arrangement.spacedBy(1.dp), horizontalAlignment = if (item.isArtist) Alignment.CenterHorizontally else Alignment.Start) {
                    Text(item.title, fontSize = 13.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen.copy(alpha = 0.9f), maxLines = 1, overflow = TextOverflow.Ellipsis)
                    // Where it stands, after what it is: "Album, Daft Punk, Requested".
                    val standing = state.takeIf { it == MusicRequestState.InLibrary || it == MusicRequestState.Requested }?.label(item.isArtist)
                    val what = when {
                        showsKind -> if (item.isArtist) t("Artist") else listOfNotNull(item.releaseKind, item.artist).joinToString(", ")
                        item.isArtist -> null
                        showsRequest -> item.kindLine
                        else -> item.artist ?: item.kindLine
                    }
                    // Under the chip, the chip says where it stands.
                    val below = listOfNotNull(what, standing.takeIf { !showsRequest }).joinToString(", ").ifEmpty { null }
                    below?.let { Text(it, fontSize = 12.sp, color = Flexarr.usher, maxLines = 1, overflow = TextOverflow.Ellipsis) }
                }
            }
            if (showsRequest && state != MusicRequestState.None) MusicRequestChip(model, item, state, onRequest = request, onUndo = { navigator.unrequestMusic(model, item) })
        }
        ItemMenu(actions, menu, onDismiss = { menu = false })
    }
    if (confirming) MusicUndoDialog(isArtist = false, item.title, onConfirm = { navigator.unrequestMusic(model, item) }, onDismiss = { confirming = false })
}

/**
 * What an album's chip says, short enough for a narrow card: Request (named Request album for TalkBack), Requested, or
 * In your library; null for nothing to offer.
 */
internal fun MusicRequestState.chipLabel(): String? = when (this) {
    MusicRequestState.CanRequest -> t(Words.request)
    MusicRequestState.Requested -> t(Words.requested)
    MusicRequestState.InLibrary -> label(isArtist = false)
    MusicRequestState.None -> null
}

/**
 * Under an album on an artist's page, as the web's small quiet buttons: the request icon and Request; Requested
 * pressed in ([musicOnStyle]) with the requested icon, pressing it asking once ([MusicUndoDialog]) before [onUndo]; or
 * In your library, opening it. The whole width under the card takes the press; the button itself is small, its words
 * shrinking to fit a narrow card rather than being cut off.
 */
@Composable
private fun MusicRequestChip(model: AppModel, item: MusicExternal, state: MusicRequestState, onRequest: () -> Unit, onUndo: () -> Unit) {
    val navigator = LocalNavigator.current
    var confirming by remember { mutableStateOf(false) }
    val on = state == MusicRequestState.Requested
    val canAsk = model.canAskForMusicHere
    val enabled = when (state) {
        MusicRequestState.CanRequest, MusicRequestState.Requested -> canAsk
        MusicRequestState.InLibrary -> item.libraryItemId != null
        else -> false
    }
    val label = state.chipLabel() ?: return
    val icon = state.icon(item.isArtist)
    val shape = RoundedCornerShape(Tokens.radiusControl)
    Box(
        Modifier.fillMaxWidth().heightIn(min = 48.dp)
            .pressable(
                {
                    when (state) {
                        MusicRequestState.CanRequest -> onRequest()
                        MusicRequestState.Requested -> confirming = true
                        else -> navigator.openMusic(item)
                    }
                },
                enabled = enabled,
                onClickLabel = if (on) musicUndoConfirm(isArtist = false) else null,
            )
            .semantics {
                if (on) selected = true
                if (state == MusicRequestState.CanRequest) contentDescription = t(Words.requestAlbum)
            },
        contentAlignment = Alignment.CenterStart,
    ) {
        Row(
            Modifier.height(32.dp).clip(shape)
                .musicOnStyle(on, Flexarr.silver, shape, offFill = Flexarr.screen.copy(alpha = 0.09f))
                .then(if (on) Modifier else Modifier.border(1.dp, Flexarr.screen.copy(alpha = 0.22f), shape))
                .alpha(if (enabled) 1f else 0.5f)
                .padding(horizontal = 9.dp),
            horizontalArrangement = Arrangement.spacedBy(4.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            icon?.let { Icon(it, contentDescription = null, tint = Flexarr.screen, modifier = Modifier.size(15.dp)) }
            BasicText(
                label,
                style = TextStyle(color = Flexarr.screen, fontSize = 12.sp, fontWeight = FontWeight.SemiBold),
                maxLines = 1,
                autoSize = TextAutoSize.StepBased(minFontSize = 9.sp, maxFontSize = 12.sp),
            )
        }
    }
    if (confirming) MusicUndoDialog(isArtist = false, item.title, onConfirm = onUndo, onDismiss = { confirming = false })
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
            // The same artist or album can be in a list twice (a joint album under each artist): its place keeps the keys apart.
            rowItemsIndexed(items, key = { index, item -> "$index-${item.id}" }) { _, item -> MusicExternalCard(model, item, width = width) }
        }
    }
}

/** The header of an outside page: the art, the name and what's under it, its buttons, and "Lidarr isn't answering." under them when it isn't. */
@Composable
private fun ExternalMusicHeader(
    model: AppModel,
    art: @Composable () -> Unit,
    title: String,
    below: @Composable () -> Unit,
    buttons: @Composable () -> Unit,
) {
    Column(Modifier.fillMaxWidth().statusBarsPadding().padding(top = 12.dp, start = Edge, end = Edge), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(16.dp)) {
        art()
        Column(horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(6.dp)) {
            Text(title, style = marquee(30), textAlign = TextAlign.Center, maxLines = 2, overflow = TextOverflow.Ellipsis)
            below()
        }
        Column(horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Row(horizontalArrangement = Arrangement.spacedBy(12.dp), verticalAlignment = Alignment.CenterVertically) { buttons() }
            LidarrNotAnsweringNote(model)
        }
    }
}

/** An outside album the household has: "In your library", opening it. */
@Composable
private fun InYourLibraryButton(item: MusicExternal) {
    val navigator = LocalNavigator.current
    ActionButton(
        MusicRequestState.InLibrary.label(item.isArtist)!!,
        onClick = { navigator.openMusic(item) },
        icon = MusicRequestState.InLibrary.icon(item.isArtist),
        enabled = item.libraryItemId != null,
    )
}

/**
 * An artist from outside the library, as the iPhone app's ExternalArtistView: their photo and name, Follow,
 * their releases (albums, EPs and singles, compilations) each with Request album, and Fans also like. One the
 * household has adds In your library, opening theirs.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ExternalArtistScreen(model: AppModel, deezerId: Long) {
    val client = model.client ?: return
    val navigator = LocalNavigator.current
    val page = rememberLoader(deezerId) { client.musicArtist(deezerId) }
    RoomBackground(ambient = Flexarr.silver, intensity = 0.3f) {
        when (val state = page.state) {
            is Load.Loaded -> PullToRefreshBox(isRefreshing = page.refreshing, onRefresh = page.retry) {
                val artist = state.value
                val summary = artist.summary
                val libraryId = artist.libraryItemId
                val keys = listOfNotNull(summary.id, libraryId?.let { "library-artist-$it" })
                val follow = model.followState(keys, artist.followed || artist.availability == Availability.Requested)
                CompositionLocalProvider(LocalMusicPaused provides artist.lidarrPaused) { LazyVerticalGrid(
                    columns = GridCells.Adaptive(105.dp),
                    contentPadding = PaddingValues(start = Edge, end = Edge, bottom = 32.dp),
                    horizontalArrangement = Arrangement.spacedBy(12.dp),
                    verticalArrangement = Arrangement.spacedBy(20.dp),
                    modifier = Modifier.fillMaxSize(),
                ) {
                    fullWidth {
                        ExternalMusicHeader(
                            model,
                            art = { MusicArtwork(artist.image, artist.name, circular = true, modifier = Modifier.width(180.dp).shadow(20.dp, CircleShape)) },
                            title = artist.name,
                            below = { if (artist.albums.isNotEmpty()) FactsLine(listOf(t("{count, plural, one {# album} other {# albums}}", "count" to artist.albums.size))) },
                            buttons = {
                                if (libraryId != null) {
                                    ActionButton(t(Words.inYourLibrary), onClick = { navigator.openLibraryArtist(libraryId) }, icon = FlexarrIcons.music, prominent = true)
                                }
                                // Nothing of theirs plays here: Follow is the page's one action (MusicActions).
                                if (MusicActions.Button.FOLLOW in MusicActions.of(MusicActions.Input(MusicActions.Kind.ARTIST, hasMusic = false, canRequest = true, platform = MusicActions.Platform.PHONE))) {
                                    MusicRequestRoundButton(
                                        model, follow, isArtist = true, name = artist.name, ambient = Flexarr.silver,
                                        // Named when it's the page's only button; beside In your library, its icon alone.
                                        labelled = libraryId == null,
                                        onRequest = { navigator.requestMusic(model, summary, keys) },
                                        onUndo = { navigator.unfollowMusic(model, keys) { it.unfollowArtist(deezerId = deezerId) } },
                                    )
                                }
                            },
                        )
                    }
                    artist.albums.releaseShelves().forEach { (title, albums) ->
                        fullWidth { SectionHeading(title, Modifier.bleed()) }
                        itemsIndexed(albums, key = { index, album -> "$title-$index-${album.id}" }) { _, album -> MusicExternalCard(model, album, width = null, showsRequest = true) }
                    }
                    if (artist.similar.isNotEmpty()) {
                        fullWidth { MusicExternalShelf(model, t(Words.fansAlsoLike), artist.similar, Modifier.bleed()) }
                    }
                } }
            }
            is Load.Failed -> StatusView(state.message, page.retry)
            Load.Loading -> StatusView()
        }
        BackButton()
    }
}

/** The artist as a card knows them, for Follow. */
private val MusicArtistDetail.summary: MusicExternal
    get() = MusicExternal("artist", deezerId, name, image = image, availability = availability, libraryItemId = libraryItemId)

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
    RoomBackground(ambient = Flexarr.silver, intensity = 0.3f) {
        when (val state = page.state) {
            is Load.Loaded -> PullToRefreshBox(isRefreshing = page.refreshing, onRefresh = page.retry) {
                val album = state.value
                val summary = album.summary
                CompositionLocalProvider(LocalMusicPaused provides album.lidarrPaused) { LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 32.dp)) {
                    item {
                        ExternalMusicHeader(
                            model,
                            art = { MusicArtwork(album.image, album.title, circular = false, modifier = Modifier.width(240.dp).shadow(20.dp, RoundedCornerShape(Tokens.radiusArt)), imageWidth = 800) },
                            title = album.title,
                            below = {
                                val artistId = album.artistDeezerId
                                // The artist's name opens theirs: a button, big enough to tap.
                                Box(
                                    if (artistId != null) Modifier.heightIn(min = 48.dp).pressable({ navigator.openMusicArtist(artistId) }, role = Role.Button) else Modifier,
                                    contentAlignment = Alignment.Center,
                                ) {
                                    Text(
                                        album.artist,
                                        fontSize = 17.sp,
                                        fontWeight = FontWeight.SemiBold,
                                        color = if (artistId != null) Flexarr.silver else Flexarr.screen.copy(alpha = 0.9f),
                                    )
                                }
                                FactsLine(albumFacts(album))
                            },
                            buttons = {
                                val request = model.requestState(summary)
                                if (request == MusicRequestState.InLibrary) {
                                    InYourLibraryButton(summary)
                                } else if (request != MusicRequestState.None) {
                                    // Nothing plays here: Request album is the page's one action, so it says so.
                                    MusicRequestRoundButton(
                                        model, request, isArtist = false, name = album.title, ambient = Flexarr.silver,
                                        labelled = true,
                                        onRequest = { navigator.requestMusic(model, summary) },
                                        onUndo = { navigator.unrequestMusic(model, summary) },
                                    )
                                }
                            },
                        )
                    }
                    album.tracks.forEachIndexed { index, track ->
                        item(key = "track-$index") {
                            val disc = track.disc.takeIf { album.tracks.any { it.disc != album.tracks.first().disc } }
                            if (disc != null && (index == 0 || album.tracks[index - 1].disc != track.disc)) {
                                Text(t("Disc {disc}", "disc" to disc), style = marquee(20, Flexarr.usher).copy(fontWeight = FontWeight.Bold), modifier = Modifier.padding(start = Edge + 4.dp, top = if (index == 0) 20.dp else 40.dp, bottom = 6.dp))
                            }
                            Box(Modifier.padding(horizontal = Edge - 8.dp).padding(top = if (index == 0 && disc == null) 20.dp else 0.dp)) {
                                OutsideTrackRow(track, if (track.number > 0) track.number else index + 1)
                            }
                        }
                    }
                } }
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
    if (album.tracks.isNotEmpty()) add(t("{count, plural, one {# track} other {# tracks}}", "count" to album.tracks.size))
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
