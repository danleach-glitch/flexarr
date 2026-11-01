package io.github.flexarr.ui.subtitles

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Verified
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.github.flexarr.core.DownloadedSubtitles
import io.github.flexarr.core.FlexarrClient
import io.github.flexarr.core.SubtitleSearchResult
import io.github.flexarr.core.downloadSubtitles
import io.github.flexarr.core.searchSubtitles
import io.github.flexarr.ui.components.pressable
import io.github.flexarr.ui.theme.Flexarr
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch
import kotlin.coroutines.cancellation.CancellationException

private const val BEST_MATCH = "best-match"

/**
 * Subtitles Bazarr can find for a file: Best match first (Flexarr picks), then every result, best first. Choosing one
 * saves it beside the file and hands it to [onDownloaded] (the player opens again with it on; a title page says it's
 * saved). Opened from the player or a title page, so its words fit both. As the iPhone's SubtitleSearchView.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun SubtitleSearchSheet(
    client: FlexarrClient,
    itemId: String,
    fileId: String,
    onDownloaded: (DownloadedSubtitles) -> Unit,
    onClose: () -> Unit,
) {
    var results by remember { mutableStateOf<List<SubtitleSearchResult>?>(null) }
    var searchError by remember { mutableStateOf<String?>(null) }
    // The row being downloaded (BEST_MATCH for Best match), and the work, so closing calls it off.
    var downloading by remember { mutableStateOf<String?>(null) }
    var download by remember { mutableStateOf<Job?>(null) }
    var downloadError by remember { mutableStateOf<String?>(null) }
    val scope = rememberCoroutineScope()

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

    ModalBottomSheet(onDismissRequest = ::close, sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true), containerColor = Flexarr.room) {
        Row(Modifier.fillMaxWidth().padding(start = 20.dp, end = 8.dp), verticalAlignment = Alignment.CenterVertically) {
            Text("Search for subtitles", fontSize = 17.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen, modifier = Modifier.weight(1f))
            TextButton(onClick = ::close) { Text("Done", color = Flexarr.screen, fontWeight = FontWeight.SemiBold) }
        }
        LazyColumn(Modifier.navigationBarsPadding(), contentPadding = PaddingValues(horizontal = 8.dp, vertical = 8.dp)) {
            downloadError?.let { message ->
                item { Text(message, fontSize = 13.sp, color = Flexarr.danger, modifier = Modifier.padding(horizontal = 14.dp, vertical = 8.dp)) }
            }
            item {
                ResultRow("Best match", "Flexarr picks the closest one", provider = null, matchesFile = false, downloading = downloading == BEST_MATCH) { pick(null) }
            }
            val found = results
            when {
                searchError != null -> item { Status { Text(searchError!!, color = Flexarr.danger) } }
                found == null -> item {
                    Status {
                        CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp, color = Flexarr.usher)
                        Text("Searching…")
                    }
                }
                found.isEmpty() -> item { Status { Text("No subtitles found") } }
                else -> items(found, key = { it.id }) { result ->
                    ResultRow(result.languageLabel, result.release, result.provider, result.matchesFile, downloading == result.id) { pick(result.id) }
                }
            }
        }
    }
}

@Composable
private fun Status(content: @Composable () -> Unit) {
    Row(
        Modifier.padding(horizontal = 14.dp).padding(top = 16.dp, bottom = 24.dp),
        horizontalArrangement = Arrangement.spacedBy(12.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        androidx.compose.material3.ProvideTextStyle(androidx.compose.ui.text.TextStyle(fontSize = 13.sp, color = Flexarr.usher)) { content() }
    }
}

@Composable
private fun ResultRow(title: String, detail: String, provider: String?, matchesFile: Boolean, downloading: Boolean, onClick: () -> Unit) {
    Row(
        Modifier.fillMaxWidth().pressable(onClick).padding(horizontal = 14.dp, vertical = 10.dp),
        horizontalArrangement = Arrangement.spacedBy(16.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(3.dp)) {
            Text(title, fontSize = 16.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen, maxLines = 1, overflow = TextOverflow.Ellipsis)
            Text(if (downloading) "Downloading…" else detail, fontSize = 13.sp, color = Flexarr.usher, maxLines = 2, overflow = TextOverflow.Ellipsis)
            if (provider != null || matchesFile) {
                Row(horizontalArrangement = Arrangement.spacedBy(10.dp), verticalAlignment = Alignment.CenterVertically) {
                    provider?.let { Text(it, fontSize = 13.sp, color = Flexarr.usher, maxLines = 1) }
                    if (matchesFile) {
                        Row(horizontalArrangement = Arrangement.spacedBy(4.dp), verticalAlignment = Alignment.CenterVertically) {
                            Icon(Icons.Filled.Verified, contentDescription = null, tint = Flexarr.go, modifier = Modifier.size(14.dp))
                            Text("Matches your file", fontSize = 13.sp, color = Flexarr.go, maxLines = 1)
                        }
                    }
                }
            }
        }
        if (downloading) CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp, color = Flexarr.screen)
    }
}
