package io.github.flexarr.ui.settings

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.OpenInNew
import androidx.compose.material.icons.filled.Link
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalUriHandler
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.text.BasicText
import androidx.compose.foundation.text.TextAutoSize
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.ui.semantics.clearAndSetSemantics
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.text.TextStyle
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.lifecycle.repeatOnLifecycle
import io.github.flexarr.core.TraktWords
import io.github.flexarr.ui.components.Edge
import io.github.flexarr.AppModel
import io.github.flexarr.core.FlexarrClient
import io.github.flexarr.core.TraktImportPreview
import io.github.flexarr.core.TraktList
import io.github.flexarr.core.TraktStatus
import io.github.flexarr.core.cancelTraktConnect
import io.github.flexarr.core.confirmTraktImport
import io.github.flexarr.core.connectTrakt
import io.github.flexarr.core.disconnectTrakt
import io.github.flexarr.core.previewTraktImport
import io.github.flexarr.core.relativeTime
import io.github.flexarr.core.saveTraktLists
import io.github.flexarr.core.syncTraktNow
import io.github.flexarr.core.traktLists
import io.github.flexarr.core.traktStatus
import io.github.flexarr.ui.components.LocalNavigator
import io.github.flexarr.ui.components.PageTitle
import io.github.flexarr.ui.components.pressable
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.RoomBackground
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

/**
 * This profile's Trakt account, as the iPhone's TraktSettingsView: connect by a code entered at trakt.tv/activate, how
 * the sync is going, the first history import (previewed first), the Trakt lists that come across as collections, and
 * disconnecting.
 */
@Composable
fun TraktSettingsScreen(model: AppModel) {
    val navigator = LocalNavigator.current
    val trakt = rememberTrakt(model)
    val status = trakt.status
    val context = LocalContext.current
    val uri = LocalUriHandler.current
    var confirmDisconnect by remember { mutableStateOf(false) }

    RoomBackground(ambient = null) {
        LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 32.dp), verticalArrangement = Arrangement.spacedBy(28.dp)) {
            item { PageTitle("Trakt", onBack = { navigator.back() }) }
            when {
                trakt.unreachable -> item {
                    Group {
                        Line(TraktWords.UNREACHABLE)
                        Divider()
                        ActionRow(TraktWords.TRY_AGAIN, icon = Icons.Filled.Refresh) { trakt.retry() }
                    }
                }
                !trakt.loaded -> item {
                    Group {
                        Box(Modifier.fillMaxWidth().padding(vertical = 16.dp), contentAlignment = Alignment.Center) {
                            CircularProgressIndicator(Modifier.size(22.dp), color = Flexarr.usher, strokeWidth = 2.dp)
                        }
                    }
                }
                status == null || !status.available -> item { Group { Line(TraktWords.UNAVAILABLE) } }
                status.isConnected -> {
                    item {
                        WithProblem(trakt.problemAt(TraktSpot.Account)) {
                            Group(footer = status.syncNote) {
                                Fact("Account", status.username ?: "Connected")
                                Divider()
                                Line(status.syncedText())
                                if (status.queued > 0) {
                                    Divider()
                                    Fact("Waiting to send", "${status.queued}")
                                }
                                Divider()
                                ActionRow(if (trakt.syncing) TraktWords.SYNCING else "Sync now", busy = trakt.syncing, enabled = !trakt.working) { trakt.sync() }
                                Divider()
                                ActionRow("Disconnect", color = Flexarr.danger, enabled = !trakt.working) { confirmDisconnect = true }
                            }
                        }
                    }
                    item { WithProblem(trakt.historyProblem(status)) { HistoryImport(trakt, status) } }
                    trakt.lists?.takeIf { it.isNotEmpty() }?.let { lists -> item { WithProblem(trakt.problemAt(TraktSpot.Lists)) { Lists(trakt, lists) } } }
                }
                status.waitingCode != null -> item {
                    val code = status.waitingCode!!
                    WithProblem(trakt.problemAt(TraktSpot.Account)) {
                        Group(footer = "${TraktWords.ENTER_CODE} ${TraktWords.WAITS}") {
                            BasicText(
                                code.userCode,
                                style = TextStyle(
                                    color = Flexarr.screen, fontSize = 34.sp, fontWeight = FontWeight.Black, fontFamily = FontFamily.Monospace,
                                    letterSpacing = 4.sp, textAlign = TextAlign.Center,
                                ),
                                maxLines = 1,
                                softWrap = false,
                                // Stays on one line at the largest font sizes, shrinking to fit.
                                autoSize = TextAutoSize.StepBased(minFontSize = 12.sp, maxFontSize = 34.sp),
                                modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 18.dp)
                                    .clearAndSetSemantics { contentDescription = "Code ${TraktWords.spelled(code.userCode)}" },
                            )
                            Divider()
                            ActionRow("Copy code and open Trakt", icon = Icons.AutoMirrored.Filled.OpenInNew) {
                                copy(context, code.userCode)
                                runCatching { uri.openUri(code.verificationUrl) }
                            }
                            Divider()
                            ActionRow("Cancel", color = Flexarr.screen, enabled = !trakt.working) { trakt.run(TraktSpot.Account) { it.cancelTraktConnect() } }
                        }
                    }
                }
                else -> item {
                    WithProblem(trakt.problemAt(TraktSpot.Account)) {
                        Group(footer = status.connectNote) {
                            ActionRow(status.connectLabel, icon = Icons.Filled.Link, enabled = !trakt.working) { trakt.run(TraktSpot.Account) { it.connectTrakt() } }
                        }
                    }
                }
            }
        }
    }

    if (confirmDisconnect) {
        AlertDialog(
            onDismissRequest = { confirmDisconnect = false },
            title = { Text(TraktWords.DISCONNECT_TITLE) },
            text = { Text(TraktWords.DISCONNECT_TEXT) },
            confirmButton = {
                TextButton(onClick = {
                    confirmDisconnect = false
                    trakt.run(TraktSpot.Account) { it.disconnectTrakt() }
                }) { Text("Disconnect", color = Flexarr.danger) }
            },
            dismissButton = { TextButton(onClick = { confirmDisconnect = false }) { Text("Cancel", color = Flexarr.screen) } },
            containerColor = Flexarr.seat,
        )
    }
}

@Composable
private fun HistoryImport(trakt: TraktState, status: TraktStatus) {
    val preview = trakt.preview
    val footer = when {
        status.importing || status.importedAt != null -> null
        preview == null -> "See what changes first."
        else -> "Nothing is marked unwatched, on either side."
    }
    Group(header = "Your history", footer = footer) {
        when {
            status.importing -> Row(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 14.dp), horizontalArrangement = Arrangement.spacedBy(12.dp), verticalAlignment = Alignment.CenterVertically) {
                CircularProgressIndicator(Modifier.size(18.dp), color = Flexarr.usher, strokeWidth = 2.dp)
                Text(TraktWords.IMPORTING, fontSize = 17.sp, color = Flexarr.usher)
            }
            status.importedAt != null -> Fact(TraktWords.IMPORTED, status.imported?.let(::relativeTime).orEmpty())
            preview != null -> {
                Column(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 12.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                    Text(preview.summary(), fontSize = 17.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen)
                    Text(preview.details(), fontSize = 13.sp, color = Flexarr.usher)
                }
                Divider()
                ActionRow(TraktWords.IMPORT_CONFIRM, enabled = !trakt.working) { trakt.run(TraktSpot.History) { it.confirmTraktImport() } }
            }
            else -> ActionRow(TraktWords.IMPORT, enabled = !trakt.working) { trakt.run(TraktSpot.History) { client -> trakt.preview = client.previewTraktImport() } }
        }
    }
}

@Composable
private fun Lists(trakt: TraktState, lists: List<TraktList>) {
    Group(header = "Lists", footer = TraktWords.LISTS_NOTE) {
        lists.forEachIndexed { index, list ->
            if (index > 0) Divider()
            Toggle(list.name, on = list.selected, onChange = { trakt.choose(list, it) }, detail = list.detail, enabled = !trakt.savingLists)
        }
    }
}

/** [content], with what went wrong there (if anything) right under it. */
@Composable
private fun WithProblem(problem: String?, content: @Composable () -> Unit) {
    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        content()
        problem?.let { Text(it, fontSize = 15.sp, color = Flexarr.danger, modifier = Modifier.padding(horizontal = Edge + 16.dp)) }
    }
}

/** A line of plain words in a group. */
@Composable
private fun Line(text: String) {
    Text(text, fontSize = 17.sp, color = Flexarr.screen, modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 12.dp))
}

/** A row that does something when pressed, in the accent colour (or [color]), with an optional icon (or a spinner, [busy]) before it. */
@Composable
private fun ActionRow(
    title: String,
    icon: ImageVector? = null,
    color: androidx.compose.ui.graphics.Color = Flexarr.silver,
    enabled: Boolean = true,
    busy: Boolean = false,
    onClick: () -> Unit,
) {
    Row(
        Modifier.fillMaxWidth().pressable(onClick, enabled = enabled).padding(horizontal = 16.dp, vertical = 14.dp),
        horizontalArrangement = Arrangement.spacedBy(14.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        when {
            busy -> CircularProgressIndicator(Modifier.size(18.dp), color = Flexarr.usher, strokeWidth = 2.dp)
            icon != null -> Icon(icon, contentDescription = null, tint = if (enabled) color else Flexarr.usher)
        }
        Text(title, fontSize = 17.sp, color = if (enabled) color else Flexarr.usher)
    }
}

private fun copy(context: Context, text: String) {
    val clipboard = context.getSystemService(ClipboardManager::class.java) ?: return
    clipboard.setPrimaryClip(ClipData.newPlainText("Trakt code", text))
}

/** Where on the page a failed call says so: under the account (or connect) part, the history, or the lists. */
internal enum class TraktSpot { Account, History, Lists }

/**
 * What the phone and TV Trakt pages share: the status (asked again every 2 s while a code waits or the import runs,
 * and only while the page is showing), the import's preview, the lists once connected, and running one thing at a time.
 */
internal class TraktState(private val model: AppModel, private val scope: kotlinx.coroutines.CoroutineScope) {
    var status by mutableStateOf<TraktStatus?>(null)
    var preview by mutableStateOf<TraktImportPreview?>(null)
    var lists by mutableStateOf<List<TraktList>?>(null)

    /** The server has answered once (with no Trakt at all, perhaps). */
    var loaded by mutableStateOf(false)

    /** The first look didn't get an answer. */
    var unreachable by mutableStateOf(false)
    var working by mutableStateOf(false)
    var syncing by mutableStateOf(false)
    var savingLists by mutableStateOf(false)
    private var problem by mutableStateOf<Pair<TraktSpot, String>?>(null)

    fun problemAt(spot: TraktSpot): String? = problem?.takeIf { it.first == spot }?.second

    /** What went wrong with the history: the last call there, or why the last import didn't finish. */
    fun historyProblem(status: TraktStatus): String? =
        problemAt(TraktSpot.History) ?: status.importError?.takeIf { it.isNotBlank() && !status.importing }

    suspend fun load() {
        val client = model.client ?: return
        try {
            status = client.traktStatus()
            loaded = true
            unreachable = false
        } catch (e: CancellationException) {
            throw e
        } catch (_: Exception) {
            // Once there's a page, it stays; the next look may do better.
            if (!loaded) unreachable = true
        }
    }

    fun retry() {
        unreachable = false
        scope.launch { load() }
    }

    suspend fun loadLists() {
        val client = model.client ?: return
        lists = try {
            client.traktLists()
        } catch (e: CancellationException) {
            throw e
        } catch (_: Exception) {
            null
        }
    }

    /** Runs [work], then looks at the status again; a failure shows at [spot], in the server's words if it gave some. */
    fun run(spot: TraktSpot, work: suspend (FlexarrClient) -> Unit) {
        val client = model.client ?: return
        if (working) return
        working = true
        problem = null
        scope.launch {
            try {
                work(client)
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                problem = spot to TraktWords.problem(e)
            }
            try {
                load()
            } finally {
                working = false
                syncing = false
            }
        }
    }

    /** Syncs now: the server answers once the sync is done, so the button stays busy until then. */
    fun sync() {
        if (working || model.client == null) return
        syncing = true
        run(TraktSpot.Account) { it.syncTraktNow() }
    }

    /** Turns a list on or off at once, and saves the whole choice; turned back if it doesn't save. */
    fun choose(list: TraktList, on: Boolean) {
        val client = model.client ?: return
        val before = lists ?: return
        val after = before.map { if (it.id == list.id) it.copy(selected = on) else it }
        lists = after
        savingLists = true
        // Only the lists' own problem goes: one under the account stays until something there is tried again.
        if (problem?.first == TraktSpot.Lists) problem = null
        scope.launch {
            try {
                client.saveTraktLists(after.filter { it.selected }.map { it.id })
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                lists = before
                problem = TraktSpot.Lists to TraktWords.problem(e)
            } finally {
                savingLists = false
            }
        }
    }
}

@Composable
internal fun rememberTrakt(model: AppModel): TraktState {
    val scope = rememberCoroutineScope()
    val state = remember(model.currentProfile?.id) { TraktState(model, scope) }
    LaunchedEffect(state) { state.load() }
    // While a code waits, or the import runs, keep looking: only while the page is showing, not from the background.
    val busy = state.status?.isBusy == true
    val lifecycle = LocalLifecycleOwner.current.lifecycle
    LaunchedEffect(state, busy, lifecycle) {
        if (!busy) return@LaunchedEffect
        lifecycle.repeatOnLifecycle(Lifecycle.State.STARTED) {
            while (true) {
                delay(2_000)
                state.load()
            }
        }
    }
    // The lists, once connected (and again on connecting as someone else).
    val account = state.status?.takeIf { it.isConnected }?.username
    val connected = state.status?.isConnected == true
    LaunchedEffect(state, connected, account) {
        if (connected) state.loadLists() else state.lists = null
    }
    return state
}
