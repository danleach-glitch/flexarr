package io.github.flexarr.ui.downloads

import io.github.flexarr.core.i18n.t
import io.github.flexarr.core.i18n.Words

import androidx.activity.compose.BackHandler
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
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.KeyboardArrowRight
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Icon
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import coil3.compose.AsyncImage
import io.github.flexarr.AppModel
import io.github.flexarr.core.DownloadedShow
import io.github.flexarr.core.EpisodeGroupRules
import io.github.flexarr.core.EpisodeGroupState
import io.github.flexarr.core.Format
import io.github.flexarr.core.LocalDownload
import io.github.flexarr.core.bytes
import io.github.flexarr.ui.components.Edge
import io.github.flexarr.ui.components.PageTitle
import io.github.flexarr.ui.components.SectionHeading
import io.github.flexarr.ui.components.StatusView
import io.github.flexarr.ui.components.pressable
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.FlexarrIcons
import io.github.flexarr.ui.theme.RoomBackground
import io.github.flexarr.ui.theme.Tokens

// A show's downloaded episodes, as the iPhone app's DownloadedShows: one row per show in the Downloads list, its
// episodes on a page of their own under each season. Only what's kept with the downloads, so it all works offline.

/** A show in the Downloads list: a picture from one of its episodes, its name and how many episodes are here. */
@Composable
internal fun DownloadedShowRow(model: AppModel, show: DownloadedShow, onOpen: () -> Unit) {
    val store = model.downloads
    var removing by remember { mutableStateOf(false) }
    Row(
        Modifier.fillMaxWidth().pressable(onOpen, onLongClick = { removing = true }).padding(start = 12.dp, top = 10.dp, bottom = 10.dp, end = 8.dp),
        horizontalArrangement = Arrangement.spacedBy(12.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Box(Modifier.size(112.dp, 63.dp).clip(RoundedCornerShape(Tokens.radiusArt)).background(Flexarr.aisle), contentAlignment = Alignment.Center) {
            Icon(FlexarrIcons.shows, contentDescription = null, tint = Flexarr.usher)
            show.episodes.firstNotNullOfOrNull { store.image(it) }?.let {
                AsyncImage(model = it, contentDescription = null, contentScale = ContentScale.Crop, modifier = Modifier.fillMaxSize())
            }
        }
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(3.dp)) {
            Text(show.title, fontSize = 15.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen, maxLines = 2, overflow = TextOverflow.Ellipsis)
            ShowStatusLine(model, show)
        }
        Icon(Icons.AutoMirrored.Filled.KeyboardArrowRight, contentDescription = null, tint = Flexarr.usher)
    }
    if (removing) {
        RemoveDownloadsDialog(
            onRemove = {
                // A Download show still asking for episodes stops too, or they'd come back as Downloading.
                EpisodeBatches.halt(show.episodes.map { it.item.id }.toSet())
                store.forgetStarted(EpisodeGroupRules.groupsHolding(show.episodes.map { it.item }))
                store.removeAll(show.episodes.map { it.id })
            },
            onDismiss = { removing = false },
        )
    }
}

@Composable
private fun ShowStatusLine(model: AppModel, show: DownloadedShow) {
    val store = model.downloads
    @Composable fun caption(text: String) = Text(text, fontSize = 12.sp, color = Flexarr.usher, style = io.github.flexarr.ui.podcasts.Tabular)
    val count = t("{count, plural, one {# episode} other {# episodes}}", "count" to show.episodes.size)
    when (val state = EpisodeGroupRules.state(show.episodes, progress = store::progress)) {
        is EpisodeGroupState.Downloading -> Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
            caption(count)
            LinearProgressIndicator(progress = { state.fraction.toFloat() }, color = Flexarr.silver, trackColor = Flexarr.aisle, modifier = Modifier.fillMaxWidth().padding(end = 4.dp))
        }
        EpisodeGroupState.Downloaded -> Row(horizontalArrangement = Arrangement.spacedBy(6.dp), verticalAlignment = Alignment.CenterVertically) {
            DownloadedMark()
            caption(listOf(count, Format.bytes(show.episodes.sumOf { it.sizeBytes ?: 0 })).joinToString("   "))
        }
        is EpisodeGroupState.Failed -> Column(verticalArrangement = Arrangement.spacedBy(3.dp)) {
            caption(count)
            Text(state.message, fontSize = 12.sp, color = Flexarr.danger, maxLines = 1, overflow = TextOverflow.Ellipsis)
        }
        is EpisodeGroupState.NotDownloaded -> caption(count)
    }
}

/**
 * A downloaded show, from the Downloads list: its episodes on this phone under each season, each playable with no
 * connection, as they are in the list.
 */
@Composable
internal fun DownloadedShowScreen(model: AppModel, key: String, onBack: () -> Unit) {
    BackHandler(onBack = onBack)
    val show = model.downloads.shows.firstOrNull { it.key == key }
    RoomBackground(ambient = null) {
        Column {
            PageTitle(show?.title.orEmpty(), onBack = onBack)
            if (show == null) {
                StatusView(t("Nothing downloaded."), empty = true)
                return@Column
            }
            LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(top = 8.dp, bottom = 32.dp), verticalArrangement = Arrangement.spacedBy(24.dp)) {
                show.seasons.forEach { (number, episodes) ->
                    val heading = number?.let { t("Season {number}", "number" to it) } ?: t("Special")
                    item(key = "heading-$number") { SectionHeading(heading) }
                    item(key = "rows-$number") { SeasonRows(model, episodes) }
                }
            }
        }
    }
}

@Composable
private fun SeasonRows(model: AppModel, episodes: List<LocalDownload>) {
    Column(Modifier.padding(horizontal = Edge).clip(RoundedCornerShape(Tokens.radiusPanel)).background(Flexarr.seat)) {
        episodes.forEach { DownloadRow(model, it) }
    }
}

/** "Remove these downloads?", for a show's or a season's episodes. */
@Composable
internal fun RemoveDownloadsDialog(onRemove: () -> Unit, onDismiss: () -> Unit) {
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(t("Remove these downloads?")) },
        confirmButton = { TextButton(onClick = { onDismiss(); onRemove() }) { Text(t("Remove downloads"), color = Flexarr.danger) } },
        dismissButton = { TextButton(onClick = onDismiss) { Text(t(Words.cancel), color = Flexarr.screen) } },
        containerColor = Flexarr.seat,
    )
}

/** "Stop these downloads?", for a show's or a season's episodes on their way. */
@Composable
internal fun StopDownloadsDialog(onStop: () -> Unit, onDismiss: () -> Unit) {
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(t("Stop these downloads?")) },
        confirmButton = { TextButton(onClick = { onDismiss(); onStop() }) { Text(t("Stop downloads"), color = Flexarr.danger) } },
        dismissButton = { TextButton(onClick = onDismiss) { Text(t(Words.cancel), color = Flexarr.screen) } },
        containerColor = Flexarr.seat,
    )
}
