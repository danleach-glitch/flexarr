package io.github.flexarr.ui.downloads

import androidx.activity.compose.BackHandler
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
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.Download
import androidx.compose.material.icons.filled.ErrorOutline
import androidx.compose.material.icons.filled.HourglassTop
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.MusicNote
import androidx.compose.material.icons.filled.PlayArrow
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
import io.github.flexarr.ui.theme.RoomBackground
import io.github.flexarr.ui.theme.glowColor
import io.github.flexarr.ui.theme.marquee
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.launch

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
    // An album's size (or a playlist's first track's) is what servers from before music downloads didn't know.
    val offered by produceState(initialValue = true, source.id, model.client) {
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
                problem = e.message ?: "Couldn’t download this."
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
            title = { Text(if (here) "Remove this download?" else "Stop this download?") },
            text = if (here) ({ Text("${Format.bytes(store.bytesUsed(collection))} on this phone.") }) else null,
            confirmButton = {
                TextButton(onClick = { removing = false; store.removeCollection(collection.id) }) {
                    Text(if (here) "Remove download" else "Stop download", color = Flexarr.danger)
                }
            },
            dismissButton = { TextButton(onClick = { removing = false }) { Text("Cancel", color = Flexarr.screen) } },
            containerColor = Flexarr.seat,
        )
    }
    problem?.let { message ->
        AlertDialog(
            onDismissRequest = { problem = null },
            title = { Text("Couldn’t download this") },
            text = { Text(message) },
            confirmButton = { TextButton(onClick = { problem = null; start() }) { Text("Try again", color = Flexarr.screen) } },
            dismissButton = { TextButton(onClick = { problem = null }) { Text("Cancel", color = Flexarr.screen) } },
            containerColor = Flexarr.seat,
        )
    }
}

/** An album's or playlist's download at a glance: an arrow; an hourglass while it's got ready; a ring; a green arrow; an exclamation. */
@Composable
private fun CollectionStateIcon(status: CollectionStatus?, modifier: Modifier = Modifier) {
    Box(modifier.size(22.dp), contentAlignment = Alignment.Center) {
        when (status) {
            null -> Icon(Icons.Filled.Download, contentDescription = null, tint = Flexarr.screen)
            CollectionStatus.Preparing -> Icon(Icons.Filled.HourglassTop, contentDescription = null, tint = Flexarr.usher)
            is CollectionStatus.Downloading -> DownloadRing(status.fraction.toFloat())
            CollectionStatus.Downloaded -> Icon(Icons.Filled.ArrowCircleDown, contentDescription = null, tint = Flexarr.downloaded)
            is CollectionStatus.Failed -> Icon(Icons.Filled.ErrorOutline, contentDescription = null, tint = Flexarr.danger)
        }
    }
}

private fun collectionDescription(status: CollectionStatus?): String = when (status) {
    null -> "Download"
    CollectionStatus.Preparing -> "Getting ready on the server"
    is CollectionStatus.Downloading -> "Downloading, ${(status.fraction * 100).toInt()}%"
    CollectionStatus.Downloaded -> "Downloaded"
    is CollectionStatus.Failed -> "Couldn’t download"
}

/** A small filled green arrow that marks something downloaded (a track, an album). */
@Composable
fun DownloadedMark(modifier: Modifier = Modifier, size: Dp = 14.dp) {
    Icon(Icons.Filled.ArrowCircleDown, contentDescription = "Downloaded", tint = Flexarr.downloaded, modifier = modifier.size(size))
}

/** An album's or playlist's saved picture: its cover, or for a playlist without one a mosaic of its tracks' covers. */
@Composable
fun CollectionArtwork(model: AppModel, collection: LocalCollection, modifier: Modifier = Modifier) {
    val store = model.downloads
    Box(modifier.fillMaxWidth().aspectRatio(1f).clip(RoundedCornerShape(6.dp)).background(Flexarr.seat), contentAlignment = Alignment.Center) {
        val own = store.image(collection.imageFileName)
        val covers = remember(collection, store.all) { store.tracks(collection).mapNotNull { it.imageFileName }.distinct().mapNotNull(store::image) }
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
            IconButton(onClick = { menu = true }) { Icon(Icons.Filled.MoreVert, contentDescription = "More for ${collection.title}", tint = Flexarr.usher) }
            DropdownMenu(menu, onDismissRequest = { menu = false }) {
                DropdownMenuItem(
                    text = { Text(if (store.status(collection) == CollectionStatus.Downloaded) "Remove download" else "Stop download", color = Flexarr.danger) },
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
    val estimate = collection.estimatedBytes?.let { "about ${Format.bytes(it)}" }
    val total = collection.trackIds.size
    when (val status = store.status(collection)) {
        CollectionStatus.Preparing -> caption(listOfNotNull("Getting ready", estimate).joinToString("   "))
        is CollectionStatus.Downloading -> Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
            val here = store.tracks(collection).count { it.isDownloaded }
            caption(listOfNotNull("$here of $total tracks", estimate).joinToString("   "))
            LinearProgressIndicator(progress = { status.fraction.toFloat() }, color = Flexarr.silver, trackColor = Flexarr.aisle, modifier = Modifier.fillMaxWidth().padding(end = 4.dp))
        }
        CollectionStatus.Downloaded -> Row(horizontalArrangement = Arrangement.spacedBy(6.dp), verticalAlignment = Alignment.CenterVertically) {
            DownloadedMark()
            caption(listOf(if (total == 1) "1 track" else "$total tracks", Format.bytes(store.bytesUsed(collection))).joinToString("   "))
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
    RoomBackground(ambient = ambient, intensity = 0.3f) {
        if (collection == null) {
            StatusView("This download was removed.", empty = true)
        } else {
            val downloads = store.tracks(collection)
            val tracks = downloads.filter { it.isDownloaded }.map { it.item }
            val albums = listOfNotNull(collection.album)
            LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 32.dp)) {
                item {
                    Column(
                        Modifier.fillMaxWidth().statusBarsPadding().padding(top = 12.dp, start = Edge, end = Edge),
                        horizontalAlignment = Alignment.CenterHorizontally,
                        verticalArrangement = Arrangement.spacedBy(16.dp),
                    ) {
                        CollectionArtwork(model, collection, Modifier.width(240.dp).shadow(20.dp, RoundedCornerShape(6.dp)))
                        Column(horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(6.dp)) {
                            Text(collection.title, style = marquee(30), textAlign = TextAlign.Center, maxLines = 2, overflow = TextOverflow.Ellipsis)
                            collection.subtitle?.let { Text(it, fontSize = 17.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen.copy(alpha = 0.9f)) }
                            FactsLine(CollectionRules.facts(collection, tracks))
                        }
                        Row(horizontalArrangement = Arrangement.spacedBy(12.dp), verticalAlignment = Alignment.CenterVertically) {
                            ActionButton("Play", onClick = { music.play(tracks, albums = albums) }, icon = Icons.Filled.PlayArrow, prominent = true, ambient = ambient, enabled = tracks.isNotEmpty())
                            ActionButton("Shuffle", onClick = { music.play(tracks, shuffled = true, albums = albums) }, icon = Icons.Filled.Shuffle, ambient = ambient, enabled = tracks.size >= 2)
                        }
                    }
                }
                if (downloads.isEmpty()) {
                    item { Box(Modifier.height(160.dp)) { StatusView("Nothing here yet.", empty = true) } }
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
        Row(Modifier.fillMaxWidth().statusBarsPadding().padding(8.dp), horizontalArrangement = Arrangement.SpaceBetween) {
            IconButton(onClick = onBack, modifier = Modifier.clip(CircleShape).background(Flexarr.room.copy(alpha = 0.55f))) {
                Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = "Back", tint = Flexarr.screen)
            }
            if (collection != null) {
                IconButton(onClick = { removing = true }, modifier = Modifier.clip(CircleShape).background(Flexarr.room.copy(alpha = 0.55f))) {
                    Icon(Icons.Filled.Delete, contentDescription = "Remove download", tint = Flexarr.screen)
                }
            }
        }
    }
    if (removing && collection != null) {
        AlertDialog(
            onDismissRequest = { removing = false },
            title = { Text("Remove this download?") },
            text = { Text("${Format.bytes(store.bytesUsed(collection))} on this phone.") },
            confirmButton = {
                TextButton(onClick = {
                    removing = false
                    store.removeCollection(collection.id)
                    onBack()
                }) { Text("Remove download", color = Flexarr.danger) }
            },
            dismissButton = { TextButton(onClick = { removing = false }) { Text("Cancel", color = Flexarr.screen) } },
            containerColor = Flexarr.seat,
        )
    }
}
