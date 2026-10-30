package io.github.flexarr.player

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Verified
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.tv.material3.ClickableSurfaceDefaults
import androidx.tv.material3.Icon
import androidx.tv.material3.LocalContentColor
import androidx.tv.material3.Surface
import androidx.tv.material3.Text
import io.github.flexarr.core.DownloadedSubtitles
import io.github.flexarr.core.FlexarrClient
import io.github.flexarr.core.SubtitleSearchResult
import io.github.flexarr.core.downloadSubtitles
import io.github.flexarr.core.searchSubtitles
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.tv.TakeFocus
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch
import kotlin.coroutines.cancellation.CancellationException

/**
 * The phone's SubtitleSearchSheet for the remote: a panel down the right of the paused picture, Best match first and
 * then every result, best first. Choosing one saves it beside the file and the title opens again with it on
 * ([onDownloaded]); Back closes it.
 */
@Composable
internal fun TvSubtitleSearch(
    client: FlexarrClient,
    itemId: String,
    fileId: String,
    onDownloaded: (DownloadedSubtitles) -> Unit,
    onClose: () -> Unit,
    modifier: Modifier = Modifier,
) {
    var results by remember { mutableStateOf<List<SubtitleSearchResult>?>(null) }
    var searchError by remember { mutableStateOf<String?>(null) }
    // The row being downloaded (BEST_MATCH for Best match), and the work, so closing calls it off.
    var downloading by remember { mutableStateOf<String?>(null) }
    var download by remember { mutableStateOf<Job?>(null) }
    var downloadError by remember { mutableStateOf<String?>(null) }
    val scope = rememberCoroutineScope()
    val first = remember { FocusRequester() }
    TakeFocus(first)

    LaunchedEffect(itemId, fileId) {
        try {
            results = client.searchSubtitles(itemId, fileId)
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            searchError = e.message
        }
    }

    fun pick(resultId: String?) {
        if (downloading != null) return
        downloading = resultId ?: BEST_MATCH
        downloadError = null
        download = scope.launch {
            try {
                onDownloaded(client.downloadSubtitles(itemId, fileId, resultId))
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                downloadError = e.message
                downloading = null
            }
        }
    }

    fun close() {
        download?.cancel()
        onClose()
    }

    BackHandler(onBack = ::close)
    Column(
        modifier.fillMaxHeight().width(380.dp).padding(20.dp).glass(RoundedCornerShape(22.dp)).padding(vertical = 18.dp),
        verticalArrangement = Arrangement.spacedBy(10.dp),
    ) {
        Text("Search for subtitles", fontSize = 17.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen, modifier = Modifier.padding(horizontal = 20.dp))
        downloadError?.let { Text(it, fontSize = 13.sp, color = Flexarr.danger, modifier = Modifier.padding(horizontal = 20.dp)) }
        LazyColumn(contentPadding = PaddingValues(horizontal = 12.dp, vertical = 4.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
            item {
                ResultRow("Best match", "Flexarr picks the closest one", provider = null, matchesFile = false, downloading = downloading == BEST_MATCH, modifier = Modifier.focusRequester(first)) { pick(null) }
            }
            val found = results
            when {
                searchError != null -> item { Status(searchError!!, color = Flexarr.danger) }
                found == null -> item { Status("Searching…", busy = true) }
                found.isEmpty() -> item { Status("No subtitles found") }
                else -> items(found, key = { it.id }) { result ->
                    ResultRow(result.languageLabel, result.release, result.provider, result.matchesFile, downloading == result.id) { pick(result.id) }
                }
            }
        }
    }
}

@Composable
private fun Status(text: String, busy: Boolean = false, color: androidx.compose.ui.graphics.Color = Flexarr.usher) {
    Row(Modifier.padding(horizontal = 8.dp, vertical = 14.dp), horizontalArrangement = Arrangement.spacedBy(12.dp), verticalAlignment = Alignment.CenterVertically) {
        if (busy) CircularProgressIndicator(Modifier.size(16.dp), strokeWidth = 2.dp, color = Flexarr.usher)
        Text(text, fontSize = 13.sp, color = color)
    }
}

@Composable
private fun ResultRow(title: String, detail: String, provider: String?, matchesFile: Boolean, downloading: Boolean, modifier: Modifier = Modifier, onClick: () -> Unit) {
    Surface(
        onClick = onClick,
        modifier = modifier.fillMaxWidth().heightIn(min = 44.dp),
        shape = ClickableSurfaceDefaults.shape(RoundedCornerShape(10.dp)),
        colors = ClickableSurfaceDefaults.colors(
            containerColor = Flexarr.screen.copy(alpha = 0.07f),
            contentColor = Flexarr.screen,
            focusedContainerColor = Flexarr.screen,
            focusedContentColor = Flexarr.room,
        ),
        scale = ClickableSurfaceDefaults.scale(focusedScale = 1.02f),
    ) {
        Row(Modifier.padding(horizontal = 14.dp, vertical = 9.dp), horizontalArrangement = Arrangement.spacedBy(12.dp), verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                Text(title, fontSize = 14.sp, fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis)
                val quiet = LocalContentColor.current.copy(alpha = 0.7f)
                Text(if (downloading) "Downloading…" else detail, fontSize = 12.sp, color = quiet, maxLines = 2, overflow = TextOverflow.Ellipsis)
                if (provider != null || matchesFile) {
                    Row(horizontalArrangement = Arrangement.spacedBy(10.dp), verticalAlignment = Alignment.CenterVertically) {
                        provider?.let { Text(it, fontSize = 12.sp, color = quiet, maxLines = 1) }
                        if (matchesFile) {
                            Icon(Icons.Filled.Verified, contentDescription = null, modifier = Modifier.size(12.dp))
                            Text("Matches your file", fontSize = 12.sp, maxLines = 1)
                        }
                    }
                }
            }
            if (downloading) CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp, color = LocalContentColor.current)
        }
    }
}

private const val BEST_MATCH = "best-match"
