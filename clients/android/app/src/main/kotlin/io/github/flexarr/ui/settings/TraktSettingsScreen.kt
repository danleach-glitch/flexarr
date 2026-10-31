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
            if (status != null) {
                val code = status.waitingCode
                when {
                    status.isConnected -> {
                        item {
                            Group(footer = status.syncNote) {
                                Fact("Account", status.username ?: "Connected")
                                Divider()
                                Fact("Synced", status.lastSync?.let(::relativeTime) ?: "Not yet")
                                if (status.queued > 0) {
                                    Divider()
                                    Fact("Waiting to send", "${status.queued}")
                                }
                                Divider()
                                ActionRow("Sync now", enabled = !trakt.working) { trakt.run { it.syncTraktNow() } }
                                Divider()
                                ActionRow("Disconnect", color = Flexarr.danger) { confirmDisconnect = true }
                            }
                        }
                        if (status.importedAt == null) item { HistoryImport(trakt, status) }
                        trakt.lists?.takeIf { it.isNotEmpty() }?.let { lists -> item { Lists(trakt, lists) } }
                    }
                    code != null -> item {
                        Group(footer = "Enter the code at ${code.shortVerificationUrl}. Flexarr waits, so you can leave this.") {
                            Text(
                                code.userCode, fontSize = 34.sp, fontWeight = FontWeight.Black, fontFamily = FontFamily.Monospace, letterSpacing = 4.sp,
                                color = Flexarr.screen, textAlign = TextAlign.Center, modifier = Modifier.fillMaxWidth().padding(vertical = 18.dp),
                            )
                            Divider()
                            ActionRow("Copy code and open Trakt", icon = Icons.AutoMirrored.Filled.OpenInNew) {
                                copy(context, code.userCode)
                                runCatching { uri.openUri(code.verificationUrl) }
                            }
                            Divider()
                            ActionRow("Cancel", color = Flexarr.screen) { trakt.run { it.cancelTraktConnect() } }
                        }
                    }
                    else -> item {
                        Group(footer = status.connectNote) {
                            ActionRow(status.connectLabel, icon = Icons.Filled.Link, enabled = !trakt.working) { trakt.run { it.connectTrakt() } }
                        }
                    }
                }
            }
            trakt.problem?.let { problem ->
                item { Text(problem, fontSize = 15.sp, color = Flexarr.danger, modifier = Modifier.padding(horizontal = 32.dp)) }
            }
        }
    }

    if (confirmDisconnect) {
        AlertDialog(
            onDismissRequest = { confirmDisconnect = false },
            title = { Text("Disconnect Trakt?") },
            text = { Text("What you’ve watched stays as it is, here and on Trakt.") },
            confirmButton = {
                TextButton(onClick = {
                    confirmDisconnect = false
                    trakt.run { it.disconnectTrakt() }
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
    Group(header = "Your history", footer = if (status.importing) null else if (preview == null) "See what changes first." else "Nothing is marked unwatched, on either side.") {
        when {
            status.importing -> Row(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 14.dp), horizontalArrangement = Arrangement.spacedBy(12.dp), verticalAlignment = Alignment.CenterVertically) {
                CircularProgressIndicator(Modifier.size(18.dp), color = Flexarr.usher, strokeWidth = 2.dp)
                Text("Bringing your history across…", fontSize = 17.sp, color = Flexarr.usher)
            }
            preview != null -> {
                Column(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 12.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                    Text(preview.summary(), fontSize = 17.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen)
                    Text(preview.details(), fontSize = 13.sp, color = Flexarr.usher)
                }
                Divider()
                ActionRow("Bring across", enabled = !trakt.working) { trakt.run { it.confirmTraktImport() } }
            }
            else -> ActionRow("Bring your Trakt history across", enabled = !trakt.working) { trakt.run { client -> trakt.preview = client.previewTraktImport() } }
        }
    }
}

@Composable
private fun Lists(trakt: TraktState, lists: List<TraktList>) {
    Group(header = "Lists", footer = "Chosen lists show as collections.") {
        lists.forEachIndexed { index, list ->
            if (index > 0) Divider()
            Toggle(list.name, on = list.selected, onChange = { trakt.choose(list, it) }, detail = list.detail, enabled = !trakt.savingLists)
        }
    }
}

/** A row that does something when pressed, in the accent colour (or [color]), with an optional icon before it. */
@Composable
private fun ActionRow(title: String, icon: ImageVector? = null, color: androidx.compose.ui.graphics.Color = Flexarr.silver, enabled: Boolean = true, onClick: () -> Unit) {
    Row(
        Modifier.fillMaxWidth().pressable(onClick, enabled = enabled).padding(horizontal = 16.dp, vertical = 14.dp),
        horizontalArrangement = Arrangement.spacedBy(14.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        icon?.let { Icon(it, contentDescription = null, tint = if (enabled) color else Flexarr.usher) }
        Text(title, fontSize = 17.sp, color = if (enabled) color else Flexarr.usher)
    }
}

private fun copy(context: Context, text: String) {
    val clipboard = context.getSystemService(ClipboardManager::class.java) ?: return
    clipboard.setPrimaryClip(ClipData.newPlainText("Trakt code", text))
}

/**
 * What the phone and TV Trakt pages share: the status (asked again every 2 s while a code waits or the import runs), the
 * import's preview, the lists once connected, and running one thing at a time.
 */
internal class TraktState(private val model: AppModel, private val scope: kotlinx.coroutines.CoroutineScope) {
    var status by mutableStateOf<TraktStatus?>(null)
    var preview by mutableStateOf<TraktImportPreview?>(null)
    var lists by mutableStateOf<List<TraktList>?>(null)
    var working by mutableStateOf(false)
    var savingLists by mutableStateOf(false)
    var problem by mutableStateOf<String?>(null)

    suspend fun load() {
        val client = model.client ?: return
        try {
            client.traktStatus()?.let { status = it }
        } catch (e: CancellationException) {
            throw e
        } catch (_: Exception) {
            // Keeps what it had; the next look may do better.
        }
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

    fun run(work: suspend (FlexarrClient) -> Unit) {
        val client = model.client ?: return
        if (working) return
        working = true
        problem = null
        scope.launch {
            try {
                work(client)
            } catch (e: CancellationException) {
                throw e
            } catch (_: Exception) {
                problem = "That didn’t work. Try again."
            }
            load()
            working = false
        }
    }

    /** Turns a list on or off at once, and saves the whole choice; turned back if it doesn't save. */
    fun choose(list: TraktList, on: Boolean) {
        val client = model.client ?: return
        val before = lists ?: return
        val after = before.map { if (it.id == list.id) it.copy(selected = on) else it }
        lists = after
        savingLists = true
        problem = null
        scope.launch {
            try {
                client.saveTraktLists(after.filter { it.selected }.map { it.id })
            } catch (e: CancellationException) {
                throw e
            } catch (_: Exception) {
                lists = before
                problem = "That didn’t save. Try again."
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
    // While a code waits, or the import runs, keep looking.
    val busy = state.status?.isBusy == true
    LaunchedEffect(state, busy) {
        while (busy) {
            delay(2_000)
            state.load()
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
