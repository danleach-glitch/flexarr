package io.github.flexarr.ui.livetv

import android.text.format.Formatter
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
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.Text
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
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.github.flexarr.AppModel
import io.github.flexarr.core.FlexarrClient
import io.github.flexarr.core.Recording
import io.github.flexarr.core.Recordings
import io.github.flexarr.core.recordings
import io.github.flexarr.core.removeRecording
import io.github.flexarr.core.removeSeries
import io.github.flexarr.ui.components.Edge
import io.github.flexarr.ui.components.LocalNavigator
import io.github.flexarr.ui.components.PageTitle
import io.github.flexarr.ui.components.SectionHeading
import io.github.flexarr.ui.components.StatusView
import io.github.flexarr.ui.components.pressable
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.RoomBackground
import android.content.Context
import android.text.format.DateFormat
import io.github.flexarr.ui.components.RepeatWhileShowing
import java.util.Locale
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.launch
import java.time.ZoneId
import java.time.format.DateTimeFormatter

/**
 * The DVR: what's coming up, the series being recorded, and what's been recorded. Each row's menu takes one off (or
 * stops it, or deletes a recording); tap a recording to play it. Programmes are set to record from the guide. As the
 * iPhone's LiveRecordingsView.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun LiveRecordingsScreen(model: AppModel) {
    val client = model.client ?: return
    val navigator = LocalNavigator.current
    val scope = rememberCoroutineScope()
    var recordings by remember { mutableStateOf<Recordings?>(null) }
    var failed by remember { mutableStateOf<String?>(null) }
    var refreshing by remember { mutableStateOf(false) }

    suspend fun load() {
        try {
            recordings = client.recordings()
            failed = null
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            if (recordings == null) failed = e.message
        }
    }
    RepeatWhileShowing(30_000, model.credentials) { load() }
    fun act(action: suspend (FlexarrClient) -> Unit) {
        scope.launch {
            runCatching { action(client) }
            load()
        }
    }

    RoomBackground(ambient = null) {
        Column {
            PageTitle("Recordings", onBack = { navigator.back() })
            val loaded = recordings
            when {
                loaded == null && failed != null -> StatusView(failed, retry = { scope.launch { load() } })
                loaded == null -> StatusView()
                else -> PullToRefreshBox(isRefreshing = refreshing, onRefresh = { refreshing = true; scope.launch { load(); refreshing = false } }) {
                    LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 32.dp), verticalArrangement = Arrangement.spacedBy(24.dp)) {
                        section("Coming up") {
                            if (loaded.upcoming.isEmpty()) {
                                Text(
                                    "Nothing set to record. In the Guide, hold a programme and choose Record, or Record series.",
                                    fontSize = 13.sp,
                                    color = Flexarr.usher,
                                    modifier = Modifier.padding(16.dp),
                                )
                            }
                            loaded.upcoming.forEachIndexed { index, recording ->
                                if (index > 0) Divider()
                                RecordingRow(recording, action = if (recording.isRecordingNow) "Stop" else "Don’t record") { act { it.removeRecording(recording.id) } }
                            }
                        }
                        if (loaded.series.isNotEmpty()) {
                            section("Series") {
                                loaded.series.forEachIndexed { index, series ->
                                    if (index > 0) Divider()
                                    Row(Modifier.fillMaxWidth().padding(start = 16.dp, top = 10.dp, bottom = 10.dp), verticalAlignment = Alignment.CenterVertically) {
                                        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(3.dp)) {
                                            Text(series.title, fontSize = 16.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen)
                                            Text(
                                                "${series.channelName?.let { "On $it" } ?: "On any channel"} · ${series.upcoming} coming up · ${series.recorded} recorded" +
                                                    if (series.newOnly) " · New only" else "",
                                                fontSize = 12.sp,
                                                color = Flexarr.usher,
                                            )
                                        }
                                        RowMenu("Stop series") { act { it.removeSeries(series.id) } }
                                    }
                                }
                            }
                        }
                        if (loaded.recent.isNotEmpty()) {
                            section("Recorded") {
                                loaded.recent.forEachIndexed { index, recording ->
                                    if (index > 0) Divider()
                                    RecordingRow(
                                        recording,
                                        action = if (recording.state == "completed") "Delete" else "Clear",
                                        onOpen = recording.itemId?.let { item -> { navigator.play(item) } },
                                    ) { act { it.removeRecording(recording.id) } }
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}

private fun LazyListScope.section(title: String, content: @Composable () -> Unit) {
    item(key = title) {
        Column(Modifier.padding(horizontal = Edge)) {
            SectionHeading(title, Modifier.padding(bottom = 8.dp))
            Column(Modifier.clip(RoundedCornerShape(12.dp)).background(Flexarr.seat)) { content() }
        }
    }
}

@Composable
private fun Divider() = HorizontalDivider(Modifier.padding(start = 16.dp), color = Flexarr.aisle.copy(alpha = 0.6f))

@Composable
private fun RecordingRow(recording: Recording, action: String, onOpen: (() -> Unit)? = null, onAction: () -> Unit) {
    val context = LocalContext.current
    Row(
        Modifier.fillMaxWidth().then(if (onOpen != null) Modifier.pressable(onOpen) else Modifier).padding(start = 16.dp, top = 10.dp, bottom = 10.dp),
        horizontalArrangement = Arrangement.spacedBy(10.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        if (recording.isRecordingNow) Box(Modifier.size(9.dp).clip(CircleShape).background(RecordRed).semantics { contentDescription = "Recording now" })
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(3.dp)) {
            Text(recording.title, fontSize = 16.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen)
            val parts = buildList {
                if (recording.season != null && recording.episode != null) add("S${recording.season} E${recording.episode}")
                recording.subtitle?.let(::add)
                add(recording.channelName)
                add(dayFormat(context).format(recording.start.atZone(ZoneId.systemDefault())))
                recording.size?.let { add(Formatter.formatShortFileSize(context, it)) }
            }
            Text(parts.joinToString(" · "), fontSize = 12.sp, color = Flexarr.usher, maxLines = 1, overflow = TextOverflow.Ellipsis)
            when {
                recording.clashes -> Text("Won’t record: every stream is wanted by recordings scheduled before it.", fontSize = 12.sp, color = Flexarr.danger)
                recording.reason != null && recording.state != "scheduled" -> Text(recording.reason!!, fontSize = 12.sp, color = Flexarr.usher)
            }
        }
        RowMenu(action, onAction)
    }
}

/** A row's one action, behind its ⋮ (the iPhone's swipe). */
@Composable
private fun RowMenu(label: String, onClick: () -> Unit) {
    var open by remember { mutableStateOf(false) }
    Box {
        IconButton(onClick = { open = true }) { Icon(Icons.Filled.MoreVert, contentDescription = "More", tint = Flexarr.usher) }
        DropdownMenu(open, onDismissRequest = { open = false }, containerColor = Flexarr.seat) {
            DropdownMenuItem(text = { Text(label, color = Flexarr.danger) }, onClick = { open = false; onClick() })
        }
    }
}

/** "Tue 4 Nov 21:00", in the phone's own order and its 12 or 24-hour clock. */
private fun dayFormat(context: Context): DateTimeFormatter {
    val skeleton = if (DateFormat.is24HourFormat(context)) "EEEdMMMHHmm" else "EEEdMMMhmma"
    return DateTimeFormatter.ofPattern(DateFormat.getBestDateTimePattern(Locale.getDefault(), skeleton), Locale.getDefault())
}
