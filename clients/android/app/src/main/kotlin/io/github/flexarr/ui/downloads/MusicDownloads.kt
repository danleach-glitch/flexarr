package io.github.flexarr.ui.downloads

import io.github.flexarr.core.i18n.t
import io.github.flexarr.core.i18n.Words

import androidx.activity.compose.BackHandler
import io.github.flexarr.ui.components.StatusBarShade
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.automirrored.filled.QueueMusic
import androidx.compose.material.icons.filled.ArrowCircleDown
import androidx.compose.material.icons.filled.Download
import androidx.compose.material.icons.filled.ErrorOutline
import androidx.compose.material.icons.filled.HourglassTop
import androidx.compose.material.icons.filled.MusicNote
import androidx.compose.material.icons.filled.Shuffle
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import coil3.compose.AsyncImage
import io.github.flexarr.AppModel
import io.github.flexarr.core.CollectionKind
import io.github.flexarr.core.CollectionRules
import io.github.flexarr.core.CollectionStatus
import io.github.flexarr.core.FlexarrException
import io.github.flexarr.core.Format
import io.github.flexarr.core.ItemSummary
import io.github.flexarr.core.LocalCollection
import io.github.flexarr.core.PlaylistSummary
import io.github.flexarr.core.artistName
import io.github.flexarr.core.bytes
import io.github.flexarr.core.downloadOptions
import io.github.flexarr.ui.components.ActionButton
import io.github.flexarr.ui.components.Edge
import io.github.flexarr.ui.components.FactsLine
import io.github.flexarr.ui.components.StatusView
import io.github.flexarr.ui.components.pressable
import io.github.flexarr.ui.music.TrackRow
import io.github.flexarr.ui.podcasts.Tabular
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.FlexarrIcons
import io.github.flexarr.ui.theme.RoomBackground
import io.github.flexarr.ui.theme.glowColor
import io.github.flexarr.ui.theme.marquee
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.launch
import io.github.flexarr.ui.theme.Tokens

// The iPhone app's MusicDownloads.swift: albums and playlists downloaded whole, from the download button beside Play and
// Shuffle; their rows in Downloads' Music section, and their page there, playable with no connection.

/** What a music download button or row stands for: an album with its tracks, or a playlist with its. */
sealed interface MusicDownloadSource {
    val id: String
    val tracks: List<ItemSummary>

    data class Album(val album: ItemSummary, override val tracks: List<ItemSummary>) : MusicDownloadSource {
        override val id: String get() = album.id
    }

    data class Playlist(val playlist: PlaylistSummary, override val tracks: List<ItemSummary>) : MusicDownloadSource {
        override val id: String get() = playlist.id
    }
}

/**
 * The round download button beside Play and Shuffle on an album or playlist: downloads every track at once; while they
 * come, a ring fills; once here, a filled green arrow. Tapping again offers to delete it (or to stop it). A server from
 * before music downloads doesn't offer them, so the button doesn't show there.
 */
@Composable
fun MusicDownloadButton(model: AppModel, source: MusicDownloadSource) {
    val store = model.downloads
    val collection = store.collection(source.id)
    val status = collection?.let(store::status)
    var removing by remember { mutableStateOf(false) }
    var problem by remember { mutableStateOf<String?>(null) }
    // An album's size (or a playlist's first track's) is what servers from before music downloads didn't know. Keyed on
    // the sign-in, not model.client: that's a new object at every read, so every progress tick would ask again.
    val offered by produceState(initialValue = true, source.id, model.credentials) {
        val client = model.client ?: return@produceState
        val probe = (source as? MusicDownloadSource.Album)?.album?.id ?: source.tracks.firstOrNull()?.id ?: return@produceState
        value = try {
            client.downloadOptions(probe)
            true
        } catch (e: FlexarrException.Http) {
            e.status != 404
        } catch (e: CancellationException) {
            throw e
        } catch (_: Exception) {
            true
        }
    }
    if (!offered && collection == null) return

    fun start() {
        model.scope.launch {
            try {
                when (source) {
                    is MusicDownloadSource.Album -> store.download(source.album, source.tracks)
                    is MusicDownloadSource.Playlist -> store.download(source.playlist, source.tracks)
                }
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                problem = e.message ?: t("Couldn’t download this")
            }
        }
    }

    val enabled = source.tracks.isNotEmpty()
    Box(
        Modifier.size(48.dp).clip(CircleShape).background(Flexarr.screen.copy(alpha = 0.14f))
            .pressable({
                when (status) {
                    null -> start()
                    is CollectionStatus.Failed -> problem = status.message
                    else -> removing = true
                }
            }, enabled = enabled)
            .semantics { contentDescription = collectionDescription(status) },
        contentAlignment = Alignment.Center,
    ) {
        CollectionStateIcon(status, Modifier.alpha(if (enabled) 1f else 0.5f))
    }
    if (removing && collection != null) {
        val here = status == CollectionStatus.Downloaded
        AlertDialog(
            onDismissRequest = { removing = false },
            title = { Text(if (here) t("Remove this download?") else t("Stop this download?")) },
            text = if (here) ({ Text(t("{size} on this phone.", "size" to Format.bytes(store.bytesUsed(collection)))) }) else null,
            confirmButton = {
                TextButton(onClick = { removing = false; store.removeCollection(collection.id) }) {
                    Text(if (here) t("Remove download") else t("Stop download"), color = Flexarr.danger)
                }
            },
            dismissButton = { TextButton(onClick = { removing = false }) { Text(t(Words.cancel), color = Flexarr.screen) } },
            containerColor = Flexarr.seat,
        )
    }
    problem?.let { message ->
        AlertDialog(
            onDismissRequest = { problem = null },
            title = { Text(t("Couldn’t download this")) },
            text = { Text(message) },
            confirmButton = { TextButton(onClick = { problem = null; start() }) { Text(t("Try again"), color = Flexarr.screen) } },
            dismissButton = { TextButton(onClick = { problem = null }) { Text(t(Words.cancel), color = Flexarr.screen) } },
            containerColor = Flexarr.seat,
        )
    }
}

/** An album's or playlist's download at a glance: an arrow; an hourglass while it's got ready; a ring; a green arrow; an exclamation. */
@Composable
private fun CollectionStateIcon(status: CollectionStatus?, modifier: Modifier = Modifier) {
    Box(modifier.size(22.dp), contentAlignment = Alignment.Center) {
        when (status) {
            null -> Icon(FlexarrIcons.download, contentDescription = null, tint = Flexarr.screen)
            CollectionStatus.Preparing -> Icon(Icons.Filled.HourglassTop, contentDescription = null, tint = Flexarr.usher)
            is CollectionStatus.Downloading -> DownloadRing(status.fraction.toFloat())
            CollectionStatus.Downloaded -> Icon(Icons.Filled.ArrowCircleDown, contentDescription = null, tint = Flexarr.go)
            is CollectionStatus.Failed -> Icon(Icons.Filled.ErrorOutline, contentDescription = null, tint = Flexarr.danger)
        }
    }
}

private fun collectionDescription(status: CollectionStatus?): String = when (status) {
    null -> t(Words.download)
    CollectionStatus.Preparing -> t("Getting ready on the server")
    is CollectionStatus.Downloading -> t("Downloading, {percent}%", "percent" to (status.fraction * 100).toInt())
    CollectionStatus.Downloaded -> t("Downloaded")
    is CollectionStatus.Failed -> t("Couldn’t download")
}

/** A small filled green arrow that marks something downloaded (a track, an album). */
@Composable
fun DownloadedMark(modifier: Modifier = Modifier, size: Dp = 14.dp) {
    Icon(Icons.Filled.ArrowCircleDown, contentDescription = t("Downloaded"), tint = Flexarr.go, modifier = modifier.size(size))
}

/**
 * The downloaded mark in a card's bottom-right corner, the same on posters, albums and books; lifted clear of a progress
 * bar along the bottom ([raised]). Call it inside the artwork's [androidx.compose.foundation.layout.Box].
 */
@Composable
fun androidx.compose.foundation.layout.BoxScope.CardDownloadedMark(shown: Boolean, raised: Boolean = false) {
    if (!shown) return
    DownloadedMark(
        Modifier.align(Alignment.BottomEnd).padding(5.dp).padding(bottom = if (raised) 10.dp else 0.dp)
            .clip(CircleShape).background(Flexarr.room.copy(alpha = 0.85f)).padding(4.dp),
    )
}

/** An album's or playlist's saved picture: its cover, or for a playlist without one a mosaic of its tracks' covers. */
@Composable
fun CollectionArtwork(model: AppModel, collection: LocalCollection, modifier: Modifier = Modifier) {
    val store = model.downloads
    Box(modifier.fillMaxWidth().aspectRatio(1f).clip(RoundedCornerShape(Tokens.radiusArt)).background(Flexarr.seat), contentAlignment = Alignment.Center) {
        val own = store.image(collection.imageFileName)
        // The mosaic's four, when there's no cover of its own: not every track's picture looked for on the phone each
        // time the downloads list changes, which for a long playlist coming down was hundreds of files a time.
        val covers = remember(collection, store.all, own == null) {
            if (own != null) emptyList()
            else store.tracks(collection).asSequence().mapNotNull { it.imageFileName }.distinct().mapNotNull(store::image).take(4).toList()
        }
        when {
            own != null -> AsyncImage(model = own, contentDescription = null, contentScale = ContentScale.Crop, modifier = Modifier.fillMaxSize())
            covers.size >= 4 -> Column(Modifier.fillMaxSize()) {
                Row(Modifier.weight(1f)) {
                    AsyncImage(model = covers[0], contentDescription = null, contentScale = ContentScale.Crop, modifier = Modifier.weight(1f).fillMaxSize())
                    AsyncImage(model = covers[1], contentDescription = null, contentScale = ContentScale.Crop, modifier = Modifier.weight(1f).fillMaxSize())
                }
                Row(Modifier.weight(1f)) {
                    AsyncImage(model = covers[2], contentDescription = null, contentScale = ContentScale.Crop, modifier = Modifier.weight(1f).fillMaxSize())
                    AsyncImage(model = covers[3], contentDescription = null, contentScale = ContentScale.Crop, modifier = Modifier.weight(1f).fillMaxSize())
                }
            }
            covers.isNotEmpty() -> AsyncImage(model = covers.first(), contentDescription = null, contentScale = ContentScale.Crop, modifier = Modifier.fillMaxSize())
            else -> Icon(
                if (collection.kind == CollectionKind.Playlist) Icons.AutoMirrored.Filled.QueueMusic else Icons.Filled.MusicNote,
                contentDescription = null,
                tint = Flexarr.usher,
                modifier = Modifier.size(28.dp),
            )
        }
    }
}

/** An album or playlist in Downloads' Music section: its picture, name and what it's doing; tapping opens it. */
@Composable
fun CollectionRow(model: AppModel, collection: LocalCollection, onOpen: () -> Unit) {
    val store = model.downloads
    var menu by remember { mutableStateOf(false) }
    Row(
        Modifier.fillMaxWidth().pressable(onOpen, onLongClick = { menu = true }).padding(start = 12.dp, top = 10.dp, bottom = 10.dp),
        horizontalArrangement = Arrangement.spacedBy(12.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        CollectionArtwork(model, collection, Modifier.size(63.dp))
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(3.dp)) {
            Text(collection.title, fontSize = 15.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen, maxLines = 2, overflow = TextOverflow.Ellipsis)
            collection.subtitle?.let { Text(it, fontSize = 12.sp, color = Flexarr.usher, maxLines = 1, overflow = TextOverflow.Ellipsis) }
            CollectionStatusLine(model, collection)
        }
        Box {
            IconButton(onClick = { menu = true }) { Icon(FlexarrIcons.more, contentDescription = t("More for {title}", "title" to collection.title), tint = Flexarr.usher) }
            DropdownMenu(menu, onDismissRequest = { menu = false }) {
                DropdownMenuItem(
                    text = { Text(if (store.status(collection) == CollectionStatus.Downloaded) t("Remove download") else t("Stop download"), color = Flexarr.danger) },
                    onClick = { menu = false; store.removeCollection(collection.id) },
                )
            }
        }
    }
}

@Composable
private fun CollectionStatusLine(model: AppModel, collection: LocalCollection) {
    val store = model.downloads
    @Composable fun caption(text: String) = Text(text, fontSize = 12.sp, color = Flexarr.usher, style = Tabular)
    val estimate = collection.estimatedBytes?.let { t("about {size}", "size" to Format.bytes(it)) }
    val total = collection.trackIds.size
    when (val status = store.status(collection)) {
        CollectionStatus.Preparing -> caption(listOfNotNull(t("Getting ready"), estimate).joinToString("   "))
        is CollectionStatus.Downloading -> Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
            val here = store.tracks(collection).count { it.isDownloaded }
            caption(listOfNotNull(t("{here} of {total, plural, one {# track} other {# tracks}}", "here" to here, "total" to total), estimate).joinToString("   "))
            LinearProgressIndicator(progress = { status.fraction.toFloat() }, color = Flexarr.silver, trackColor = Flexarr.aisle, modifier = Modifier.fillMaxWidth().padding(end = 4.dp))
        }
        CollectionStatus.Downloaded -> Row(horizontalArrangement = Arrangement.spacedBy(6.dp), verticalAlignment = Alignment.CenterVertically) {
            DownloadedMark()
            caption(listOf(t("{count, plural, one {# track} other {# tracks}}", "count" to total), Format.bytes(store.bytesUsed(collection))).joinToString("   "))
        }
        is CollectionStatus.Failed -> Text(status.message, fontSize = 12.sp, color = Flexarr.danger)
    }
}

/**
 * A downloaded album or playlist, from the Downloads tab: its saved cover, Play and Shuffle, and its tracks (the ones
 * here playable), all with no connection.
 */
@Composable
fun OfflineCollectionScreen(model: AppModel, collectionId: String, onBack: () -> Unit) {
    val store = model.downloads
    val music = model.music
    val collection = store.collection(collectionId)
    var removing by remember { mutableStateOf(false) }
    BackHandler(onBack = onBack)
    val ambient = collection?.let { glowColor(it.album?.accentColor ?: it.accentColor) } ?: Flexarr.silver
    val list = rememberLazyListState()
    RoomBackground(ambient = ambient, intensity = 0.3f) {
        if (collection == null) {
            StatusView(t("This download was removed."), empty = true)
        } else {
            val downloads = store.tracks(collection)
            val tracks = downloads.filter { it.isDownloaded }.map { it.item }
            val albums = listOfNotNull(collection.album)
            LazyColumn(Modifier.fillMaxSize(), state = list, contentPadding = PaddingValues(bottom = 32.dp)) {
                item {
                    Column(
                        Modifier.fillMaxWidth().statusBarsPadding().padding(top = 12.dp, start = Edge, end = Edge),
                        horizontalAlignment = Alignment.CenterHorizontally,
                        verticalArrangement = Arrangement.spacedBy(16.dp),
                    ) {
                        CollectionArtwork(model, collection, Modifier.width(240.dp).shadow(20.dp, RoundedCornerShape(Tokens.radiusArt)))
                        Column(horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(6.dp)) {
                            Text(collection.title, style = marquee(30), textAlign = TextAlign.Center, maxLines = 2, overflow = TextOverflow.Ellipsis)
                            collection.subtitle?.let { Text(it, fontSize = 17.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen.copy(alpha = 0.9f)) }
                            FactsLine(CollectionRules.facts(collection, tracks))
                        }
                        Row(horizontalArrangement = Arrangement.spacedBy(12.dp), verticalAlignment = Alignment.CenterVertically) {
                            ActionButton(t(Words.play), onClick = { music.play(tracks, albums = albums) }, icon = FlexarrIcons.play, prominent = true, ambient = ambient, enabled = tracks.isNotEmpty())
                            ActionButton(t(Words.shuffle), onClick = { music.play(tracks, shuffled = true, albums = albums) }, icon = FlexarrIcons.shuffle, ambient = ambient, enabled = tracks.size >= 2)
                        }
                    }
                }
                if (downloads.isEmpty()) {
                    item { Box(Modifier.height(160.dp)) { StatusView(t("Nothing here yet."), empty = true) } }
                }
                itemsIndexed(downloads, key = { _, download -> download.id }) { offset, download ->
                    val track = download.item
                    Box(Modifier.padding(horizontal = Edge - 8.dp).padding(top = if (offset == 0) 20.dp else 0.dp)) {
                        TrackRow(
                            track,
                            if (collection.kind == CollectionKind.Album) track.trackNumber ?: (offset + 1) else offset + 1,
                            isCurrent = music.current?.id == track.id,
                            isPlaying = music.isPlaying,
                            subtitle = if (collection.kind == CollectionKind.Album) track.trackArtist else track.artistName,
                            isDownloaded = download.isDownloaded,
                            isDimmed = !download.isDownloaded,
                        ) {
                            if (music.current?.id == track.id) music.togglePlayPause()
                            else tracks.indexOf(track).takeIf { it >= 0 }?.let { music.play(tracks, start = it, albums = albums) }
                        }
                    }
                }
            }
        }
        StatusBarShade(list)
        Row(Modifier.fillMaxWidth().statusBarsPadding().padding(8.dp), horizontalArrangement = Arrangement.SpaceBetween) {
            IconButton(onClick = onBack, modifier = Modifier.clip(CircleShape).background(Flexarr.room.copy(alpha = 0.55f))) {
                Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = t("Back"), tint = Flexarr.screen)
            }
            if (collection != null) {
                IconButton(onClick = { removing = true }, modifier = Modifier.clip(CircleShape).background(Flexarr.room.copy(alpha = 0.55f))) {
                    Icon(FlexarrIcons.delete, contentDescription = t("Remove download"), tint = Flexarr.screen)
                }
            }
        }
    }
    if (removing && collection != null) {
        AlertDialog(
            onDismissRequest = { removing = false },
            title = { Text(t("Remove this download?")) },
            text = { Text(t("{size} on this phone.", "size" to Format.bytes(store.bytesUsed(collection)))) },
            confirmButton = {
                TextButton(onClick = {
                    removing = false
                    store.removeCollection(collection.id)
                    onBack()
                }) { Text(t("Remove download"), color = Flexarr.danger) }
            },
            dismissButton = { TextButton(onClick = { removing = false }) { Text(t(Words.cancel), color = Flexarr.screen) } },
            containerColor = Flexarr.seat,
        )
    }
}
