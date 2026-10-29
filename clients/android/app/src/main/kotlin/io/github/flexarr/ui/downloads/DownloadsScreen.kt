package io.github.flexarr.ui.downloads

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyListScope
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ArrowCircleDown
import androidx.compose.material.icons.filled.Download
import androidx.compose.material.icons.filled.ErrorOutline
import androidx.compose.material.icons.filled.HourglassTop
import androidx.compose.material.icons.filled.Mic
import androidx.compose.material.icons.filled.Movie
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.Wifi
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.minimumInteractiveComponentSize
import androidx.compose.material3.pulltorefresh.PullToRefreshBox
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import coil3.compose.AsyncImage
import io.github.flexarr.AppModel
import io.github.flexarr.core.DownloadOption
import io.github.flexarr.core.DownloadQuality
import io.github.flexarr.core.DownloadRules
import io.github.flexarr.core.Format
import io.github.flexarr.core.ItemSummary
import io.github.flexarr.core.LocalDownload
import io.github.flexarr.core.LocalPhase
import io.github.flexarr.core.MediaKind
import io.github.flexarr.core.asPodcastEpisode
import io.github.flexarr.core.bytes
import io.github.flexarr.core.downloadOptions
import io.github.flexarr.core.episodeSubtitle
import io.github.flexarr.player.PlayerActivity
import io.github.flexarr.ui.components.Edge
import io.github.flexarr.ui.components.PageTitle
import io.github.flexarr.ui.components.ProgressBar
import io.github.flexarr.ui.components.SectionHeading
import io.github.flexarr.ui.components.StatusView
import io.github.flexarr.ui.components.pressable
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.RoomBackground
import kotlinx.coroutines.launch

// The iPhone app's DownloadsView: everything downloaded (or on its way), playable with no connection; and the download
// button a title's page and a podcast episode's row carry.

/** Films, then each show's episodes, then podcast episodes, with the space they take. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun DownloadsScreen(model: AppModel) {
    val store = model.downloads
    val scope = rememberCoroutineScope()
    var refreshing by remember { mutableStateOf(false) }
    RoomBackground(ambient = null) {
        Column {
            PageTitle("Downloads")
            if (store.downloads.isEmpty()) {
                StatusView(
                    "Download films, episodes and podcast episodes to watch and listen with no connection. Look for Download on a title’s page.",
                    empty = true,
                )
                return@Column
            }
            PullToRefreshBox(
                isRefreshing = refreshing,
                onRefresh = {
                    refreshing = true
                    scope.launch {
                        store.catchUp()
                        refreshing = false
                    }
                },
            ) {
                LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(top = 8.dp, bottom = 32.dp), verticalArrangement = Arrangement.spacedBy(24.dp)) {
                    section("Films", store.films) { DownloadRow(model, it) }
                    store.shows.forEach { (show, episodes) -> section(show, episodes) { DownloadRow(model, it) } }
                    section("Podcasts", store.podcastEpisodes) { PodcastDownloadRow(model, it) }
                    item {
                        Text(
                            "${Format.bytes(store.bytesUsed)} used on this phone.",
                            fontSize = 13.sp,
                            color = Flexarr.usher,
                            modifier = Modifier.padding(horizontal = Edge),
                        )
                    }
                }
            }
        }
    }
}

private fun LazyListScope.section(title: String, downloads: List<LocalDownload>, row: @Composable (LocalDownload) -> Unit) {
    if (downloads.isEmpty()) return
    item(key = "heading-$title") { SectionHeading(title) }
    item(key = "rows-$title") {
        Column(Modifier.padding(horizontal = Edge).clip(RoundedCornerShape(12.dp)).background(Flexarr.seat)) {
            downloads.forEach { row(it) }
        }
    }
}

/** A film or episode: its picture, title and what it's doing; tapping plays it once it's here. */
@Composable
private fun DownloadRow(model: AppModel, download: LocalDownload) {
    val context = LocalContext.current
    DownloadListRow(
        model,
        download,
        title = if (download.item.kind == MediaKind.Episode) download.item.episodeSubtitle ?: download.item.title else download.item.title,
        subtitle = null,
        artWidth = 112.dp,
        placeholder = Icons.Filled.Movie,
        doneLine = listOfNotNull(download.quality.label.takeIf { download.quality != DownloadQuality.Unknown }, download.sizeBytes?.let(Format::bytes), if (download.finishedAtMillis != null) "Watched" else null),
        onPlay = { context.startActivity(PlayerActivity.offlineIntent(context, download.id)) },
    )
}

/** A podcast episode on the phone: its artwork, title and show; tapping plays it once it's here. */
@Composable
private fun PodcastDownloadRow(model: AppModel, download: LocalDownload) {
    DownloadListRow(
        model,
        download,
        title = download.item.title,
        subtitle = download.item.seriesTitle,
        artWidth = 63.dp,
        placeholder = Icons.Filled.Mic,
        doneLine = listOfNotNull(download.sizeBytes?.let(Format::bytes), if (download.finishedAtMillis != null) "Played" else null),
        onPlay = { model.podcasts.play(download.item.asPodcastEpisode()) },
    )
}

@Composable
private fun DownloadListRow(
    model: AppModel,
    download: LocalDownload,
    title: String,
    subtitle: String?,
    artWidth: Dp,
    placeholder: androidx.compose.ui.graphics.vector.ImageVector,
    doneLine: List<String>,
    onPlay: () -> Unit,
) {
    val store = model.downloads
    var menu by remember { mutableStateOf(false) }
    Row(
        Modifier.fillMaxWidth().pressable(onPlay, enabled = download.isDownloaded, onLongClick = { menu = true }).padding(start = 12.dp, top = 10.dp, bottom = 10.dp),
        horizontalArrangement = Arrangement.spacedBy(12.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Box(Modifier.size(artWidth, 63.dp).clip(RoundedCornerShape(6.dp)).background(Flexarr.aisle), contentAlignment = Alignment.Center) {
            Icon(placeholder, contentDescription = null, tint = Flexarr.usher)
            store.image(download)?.let { AsyncImage(model = it, contentDescription = null, contentScale = ContentScale.Crop, modifier = Modifier.fillMaxSize()) }
            val shown = download.position
            if (download.isDownloaded && download.finishedAtMillis == null && download.durationSeconds > 0 && shown > if (download.isPodcastEpisode) 0.0 else 30.0) {
                ProgressBar((shown / download.durationSeconds).coerceAtMost(1.0), Modifier.align(Alignment.BottomCenter).padding(5.dp))
            }
        }
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(3.dp)) {
            Text(title, fontSize = 15.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen, maxLines = 2, overflow = TextOverflow.Ellipsis)
            subtitle?.let { Text(it, fontSize = 12.sp, color = Flexarr.usher, maxLines = 1, overflow = TextOverflow.Ellipsis) }
            DownloadStatusLine(model, download, doneLine)
        }
        Box {
            IconButton(onClick = { menu = true }) { Icon(Icons.Filled.MoreVert, contentDescription = "More for $title", tint = Flexarr.usher) }
            DropdownMenu(menu, onDismissRequest = { menu = false }) {
                if (download.phase == LocalPhase.Failed) {
                    DropdownMenuItem(text = { Text("Try again") }, onClick = {
                        menu = false
                        model.scope.launch { runCatching { store.download(download.item.id, download.quality, automatic = false) } }
                    })
                }
                DropdownMenuItem(
                    text = { Text(if (download.isDownloaded) "Delete download" else "Stop download", color = Flexarr.danger) },
                    onClick = { menu = false; store.delete(download.id) },
                )
            }
        }
    }
}

@Composable
private fun DownloadStatusLine(model: AppModel, download: LocalDownload, doneLine: List<String>) {
    val store = model.downloads
    @Composable fun caption(text: String) = Text(text, fontSize = 12.sp, color = Flexarr.usher, style = io.github.flexarr.ui.podcasts.Tabular)
    when {
        store.isWaitingForWiFi(download) -> caption("Waiting for Wi-Fi")
        download.phase == LocalPhase.Preparing -> caption(gettingReady(download.progress))
        download.phase == LocalPhase.Downloading -> Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
            caption("Downloading, ${(download.progress * 100).toInt()}%")
            LinearProgressIndicator(progress = { download.progress.toFloat() }, color = Flexarr.silver, trackColor = Flexarr.aisle, modifier = Modifier.fillMaxWidth().padding(end = 4.dp))
        }
        download.phase == LocalPhase.Failed -> Text(download.failure ?: "Couldn’t download this.", fontSize = 12.sp, color = Flexarr.danger)
        else -> Row(horizontalArrangement = Arrangement.spacedBy(6.dp), verticalAlignment = Alignment.CenterVertically) {
            Icon(Icons.Filled.ArrowCircleDown, contentDescription = "Downloaded", tint = Flexarr.downloaded, modifier = Modifier.size(14.dp))
            caption(doneLine.joinToString("   "))
        }
    }
}

private fun gettingReady(progress: Double) = if (progress > 0 && progress < 1) "Getting ready, ${(progress * 100).toInt()}%" else "Getting ready"

/**
 * What a download is doing, at a glance, the same everywhere: an arrow to download; an hourglass while the server gets
 * it ready; Wi-Fi while it waits for some; a ring that fills only as the file comes down; a filled green arrow once it's
 * here (never a tick, which means played);
 * an exclamation when it failed.
 */
@Composable
fun DownloadStateIcon(download: LocalDownload?, waitingForWiFi: Boolean, size: Dp = 22.dp) {
    Box(Modifier.size(size), contentAlignment = Alignment.Center) {
        when {
            waitingForWiFi -> Icon(Icons.Filled.Wifi, contentDescription = null, tint = Flexarr.usher)
            download == null -> Icon(Icons.Filled.Download, contentDescription = null, tint = Flexarr.screen)
            download.phase == LocalPhase.Preparing -> Icon(Icons.Filled.HourglassTop, contentDescription = null, tint = Flexarr.usher)
            download.phase == LocalPhase.Downloading -> DownloadRing(download.progress.toFloat())
            download.phase == LocalPhase.Downloaded -> Icon(Icons.Filled.ArrowCircleDown, contentDescription = null, tint = Flexarr.downloaded)
            else -> Icon(Icons.Filled.ErrorOutline, contentDescription = null, tint = Flexarr.danger)
        }
    }
}

/** How much of a file has come down, as a ring that fills, with a stop square in the middle. */
@Composable
private fun DownloadRing(fraction: Float) {
    Canvas(Modifier.fillMaxSize().padding(1.dp)) {
        val stroke = 2.dp.toPx()
        val inset = stroke / 2
        val arc = Size(size.width - stroke, size.height - stroke)
        drawArc(Flexarr.screen.copy(alpha = 0.25f), 0f, 360f, false, topLeft = Offset(inset, inset), size = arc, style = Stroke(stroke))
        drawArc(Flexarr.screen, -90f, 360f * fraction.coerceIn(0.02f, 1f), false, topLeft = Offset(inset, inset), size = arc, style = Stroke(stroke, cap = StrokeCap.Round))
        val square = size.width * 0.28f
        drawRect(Flexarr.screen, topLeft = Offset((size.width - square) / 2, (size.height - square) / 2), size = Size(square, square))
    }
}

/** What a screen reader says for a download's state. */
fun downloadDescription(download: LocalDownload?, waitingForWiFi: Boolean): String = when {
    waitingForWiFi -> "Waiting for Wi-Fi"
    download == null -> "Download"
    download.phase == LocalPhase.Preparing -> "Getting ready on the server"
    download.phase == LocalPhase.Downloading -> "Downloading, ${(download.progress * 100).toInt()}%"
    download.phase == LocalPhase.Downloaded -> "Downloaded"
    else -> "Couldn’t download"
}

/**
 * Download on a film's or episode's page, among the round actions under Play. Tapping it offers the sizes the server can
 * make, with about how big each is (Settings' size first); once it's on its way or here, tapping offers to stop or
 * delete it; when it failed, tapping says why and offers to try again.
 */
@Composable
fun DownloadAction(model: AppModel, item: ItemSummary, modifier: Modifier = Modifier) {
    val store = model.downloads
    val download = store.entry(item.id)
    val waiting = download?.let(store::isWaitingForWiFi) ?: false
    var choosing by remember { mutableStateOf(false) }
    var options by remember(item.id) { mutableStateOf<List<DownloadOption>?>(null) }
    var removing by remember { mutableStateOf(false) }
    var problem by remember { mutableStateOf<String?>(null) }

    fun start(quality: DownloadQuality) {
        model.scope.launch {
            try {
                store.download(item, quality)
            } catch (e: Exception) {
                problem = e.message ?: "Couldn’t download this."
            }
        }
    }

    val label = when {
        waiting -> "Waiting for Wi-Fi"
        download == null -> "Download"
        download.phase == LocalPhase.Preparing -> "Getting ready"
        download.phase == LocalPhase.Downloading -> "${(download.progress * 100).toInt()}%"
        download.phase == LocalPhase.Downloaded -> "Downloaded"
        else -> "Couldn’t download"
    }
    Box(modifier) {
        Column(
            Modifier
                .fillMaxWidth()
                .pressable({
                    when {
                        download == null -> {
                            choosing = true
                            if (options == null) model.scope.launch { options = runCatching { DownloadRules.offered(model.client!!.downloadOptions(item.id)) }.getOrDefault(emptyList()) }
                        }
                        download.phase == LocalPhase.Failed -> problem = download.failure ?: "Couldn’t download this."
                        else -> removing = true
                    }
                })
                .semantics { contentDescription = downloadDescription(download, waiting) },
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.spacedBy(6.dp),
        ) {
            Box(Modifier.size(48.dp).clip(CircleShape).background(Flexarr.screen.copy(alpha = 0.14f)), contentAlignment = Alignment.Center) {
                DownloadStateIcon(download, waiting)
            }
            androidx.compose.foundation.text.BasicText(
                label,
                style = androidx.compose.ui.text.TextStyle(fontWeight = FontWeight.Medium, color = Flexarr.usher, textAlign = androidx.compose.ui.text.style.TextAlign.Center, fontSize = 12.sp),
                maxLines = 1,
                autoSize = androidx.compose.foundation.text.TextAutoSize.StepBased(minFontSize = 9.sp, maxFontSize = 12.sp),
            )
        }
        DropdownMenu(choosing, onDismissRequest = { choosing = false }) {
            val list = options
            if (list == null) {
                DropdownMenuItem(text = { Text("Finding sizes…", color = Flexarr.usher) }, onClick = {}, enabled = false)
            } else if (list.isEmpty()) {
                // The server didn't say: Settings' size.
                DropdownMenuItem(text = { Text("Download") }, onClick = { choosing = false; start(DownloadRules.quality(store.quality, null)) })
            } else {
                // Settings' size first, then the rest in the server's order.
                list.sortedBy { if (it.quality == store.quality) 0 else 1 }.forEach { option ->
                    DropdownMenuItem(
                        text = {
                            Column {
                                Text(option.quality.label)
                                Text(
                                    when {
                                        !option.available -> "Won’t play on this phone"
                                        option.quality == store.quality -> "About ${Format.bytes(option.estimatedBytes)}   Your usual size"
                                        else -> "About ${Format.bytes(option.estimatedBytes)}"
                                    },
                                    fontSize = 12.sp,
                                    color = Flexarr.usher,
                                )
                            }
                        },
                        enabled = option.available,
                        onClick = { choosing = false; start(option.quality) },
                    )
                }
            }
        }
    }
    if (removing && download != null) {
        AlertDialog(
            onDismissRequest = { removing = false },
            title = { Text(if (download.isDownloaded) "Delete this download?" else "Stop this download?") },
            confirmButton = {
                TextButton(onClick = { removing = false; store.delete(download.id) }) {
                    Text(if (download.isDownloaded) "Delete download" else "Stop download", color = Flexarr.danger)
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
            confirmButton = {
                TextButton(onClick = {
                    problem = null
                    download?.let { store.delete(it.id) }
                    start(download?.quality ?: DownloadRules.quality(store.quality, null))
                }) { Text("Try again", color = Flexarr.screen) }
            },
            dismissButton = { TextButton(onClick = { problem = null }) { Text("Cancel", color = Flexarr.screen) } },
            containerColor = Flexarr.seat,
        )
    }
}

/**
 * A podcast episode's download: an arrow, a ring while it comes, a tick once it's here. Tapping downloads it (as it is,
 * over any connection, since it was asked for); tapping one on its way or here offers to stop or delete it.
 */
@Composable
fun EpisodeDownloadButton(model: AppModel, episodeId: String, title: String) {
    val store = model.downloads
    val download = store.entry(episodeId)
    val waiting = download?.let(store::isWaitingForWiFi) ?: false
    var removing by remember { mutableStateOf(false) }
    var problem by remember { mutableStateOf<String?>(null) }
    Box(
        Modifier.minimumInteractiveComponentSize().size(40.dp).pressable({
            when {
                download == null -> model.scope.launch { runCatching { store.downloadEpisode(episodeId) }.onFailure { problem = it.message } }
                download.phase == LocalPhase.Failed -> problem = download.failure
                else -> removing = true
            }
        }).semantics { contentDescription = "${downloadDescription(download, waiting)}: $title" },
        contentAlignment = Alignment.Center,
    ) {
        DownloadStateIcon(download, waiting, size = 20.dp)
    }
    if (removing && download != null) {
        AlertDialog(
            onDismissRequest = { removing = false },
            title = { Text(if (download.isDownloaded) "Delete this download?" else "Stop this download?") },
            confirmButton = {
                TextButton(onClick = { removing = false; store.delete(download.id) }) {
                    Text(if (download.isDownloaded) "Delete download" else "Stop download", color = Flexarr.danger)
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
            confirmButton = {
                TextButton(onClick = {
                    problem = null
                    download?.let { store.delete(it.id) }
                    model.scope.launch { runCatching { store.downloadEpisode(episodeId) } }
                }) { Text("Try again", color = Flexarr.screen) }
            },
            dismissButton = { TextButton(onClick = { problem = null }) { Text("Cancel", color = Flexarr.screen) } },
            containerColor = Flexarr.seat,
        )
    }
}
