package io.github.flexarr.ui.tv

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.List
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.History
import androidx.compose.material.icons.filled.Link
import androidx.compose.material.icons.filled.LinkOff
import androidx.compose.material.icons.filled.Sync
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.semantics.clearAndSetSemantics
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.window.Dialog
import androidx.tv.material3.Text
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.text.BasicText
import androidx.compose.foundation.text.TextAutoSize
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.ui.text.TextStyle
import io.github.flexarr.core.TraktWords
import io.github.flexarr.ui.settings.TraktSpot
import io.github.flexarr.AppModel
import io.github.flexarr.core.cancelTraktConnect
import io.github.flexarr.core.confirmTraktImport
import io.github.flexarr.core.connectTrakt
import io.github.flexarr.core.disconnectTrakt
import io.github.flexarr.core.previewTraktImport
import io.github.flexarr.core.relativeTime
import io.github.flexarr.core.syncTraktNow
import io.github.flexarr.ui.settings.rememberTrakt
import io.github.flexarr.ui.theme.Flexarr

/**
 * This profile's Trakt account on the TV, as the Apple TV's TraktSettingsView: connect with a code and a QR code of
 * Trakt's activation page (nothing to type on the TV), how the sync is going, the first history import (previewed
 * first), the Trakt lists that come across as collections, and disconnecting.
 */
@Composable
internal fun TvTraktSettings(model: AppModel) {
    val trakt = rememberTrakt(model)
    val status = trakt.status
    var confirmDisconnect by remember { mutableStateOf(false) }
    val code = status?.waitingCode
    // The first control of whichever part is showing takes focus, and again when the part changes (the import's row
    // turning into Bring across, say).
    val phase = when {
        trakt.unreachable -> "unreachable"
        !trakt.loaded || status == null || !status.available -> null
        status.isConnected -> "connected/${status.importedAt == null}/${status.importing}/${trakt.preview != null}"
        code != null -> "code"
        else -> "connect"
    }
    val focus = remember { FocusRequester() }
    TakeFocus(focus, key = phase, ready = phase != null)

    TvSettingsPage("Trakt", summary = "Keep what you watch, your place and your watchlist in step with Trakt.") {
        when {
            trakt.unreachable -> {
                item { TvSettingsNote(TraktWords.UNREACHABLE, Modifier.padding(vertical = 4.dp)) }
                item { TvSettingsRow("Retry", onClick = { trakt.retry() }, modifier = Modifier.focusRequester(focus), icon = Icons.Filled.Refresh) }
            }
            !trakt.loaded -> item {
                Box(Modifier.fillMaxWidth().padding(vertical = 16.dp), contentAlignment = Alignment.Center) {
                    CircularProgressIndicator(Modifier.size(22.dp), color = Flexarr.usher, strokeWidth = 2.dp)
                }
            }
            status == null || !status.available -> item { TvSettingsNote(TraktWords.UNAVAILABLE, Modifier.padding(vertical = 4.dp)) }
            status.isConnected -> {
                item { TvSettingsFact("Account", status.username ?: "Connected") }
                item { TvSettingsFact(status.syncedText(), "") }
                if (status.queued > 0) item { TvSettingsFact("Waiting to send", "${status.queued}") }
                status.syncNote?.let { note -> item { TvSettingsNote(note, Modifier.padding(vertical = 4.dp)) } }

                val offersImport = status.importedAt == null || status.importing
                if (offersImport) {
                    val preview = trakt.preview
                    item {
                        val top = Modifier.padding(top = 18.dp).focusRequester(focus)
                        when {
                            status.importing -> TvSettingsRow(TraktWords.IMPORTING, onClick = {}, modifier = top, icon = Icons.Filled.History)
                            preview != null -> TvSettingsRow(
                                TraktWords.IMPORT_CONFIRM, onClick = { trakt.run(TraktSpot.History) { it.confirmTraktImport() } }, modifier = top, icon = Icons.Filled.History,
                                value = preview.summary(), help = preview.details() + " Nothing is marked unwatched, on either side.",
                            )
                            else -> TvSettingsRow(
                                TraktWords.IMPORT, onClick = { trakt.run(TraktSpot.History) { client -> trakt.preview = client.previewTraktImport() } },
                                modifier = top, icon = Icons.Filled.History, help = "See what changes first.",
                            )
                        }
                    }
                } else {
                    item { TvSettingsFact(TraktWords.IMPORTED, status.imported?.let(::relativeTime).orEmpty()) }
                }
                trakt.problemAt(TraktSpot.History)?.let { item { Problem(it) } }
                item {
                    TvSettingsRow(
                        if (trakt.syncing) TraktWords.SYNCING else "Sync now", onClick = { trakt.sync() },
                        modifier = if (offersImport) Modifier else Modifier.padding(top = 18.dp).focusRequester(focus),
                        icon = Icons.Filled.Sync, help = "Flexarr syncs every few minutes by itself.",
                    )
                }
                item {
                    TvSettingsRow(
                        "Disconnect", onClick = { if (!trakt.working) confirmDisconnect = true }, icon = Icons.Filled.LinkOff, destructive = true,
                        help = "Stop syncing this profile with Trakt.",
                    )
                }
                trakt.problemAt(TraktSpot.Account)?.let { item { Problem(it) } }
                trakt.lists?.takeIf { it.isNotEmpty() }?.let { lists ->
                    item { TvSettingsGroupLabel("Lists", Modifier.padding(top = 10.dp)) }
                    lists.forEach { list ->
                        item(key = list.id) {
                            TvSettingsToggle(
                                list.name, isOn = list.selected, flip = { if (!trakt.savingLists) trakt.choose(list, !list.selected) },
                                icon = Icons.AutoMirrored.Filled.List, help = "${list.detail}. ${TraktWords.LISTS_NOTE}",
                            )
                        }
                    }
                    trakt.problemAt(TraktSpot.Lists)?.let { item { Problem(it) } }
                }
            }
            code != null -> {
                item {
                    Row(
                        Modifier.padding(start = 16.dp, bottom = 20.dp).clearAndSetSemantics {
                            contentDescription = "${TraktWords.ENTER_CODE} ${TraktWords.spelled(code.userCode)}. ${TraktWords.WAITS}"
                        },
                        horizontalArrangement = Arrangement.spacedBy(28.dp),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        QrCode(code.qrUrl, Modifier.size(150.dp))
                        Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
                            Text(TraktWords.ENTER_CODE, fontSize = 15.sp, color = Flexarr.usher)
                            BasicText(
                                code.userCode,
                                style = TextStyle(color = Flexarr.screen, fontSize = 34.sp, fontWeight = FontWeight.Black, fontFamily = FontFamily.Monospace, letterSpacing = 3.sp),
                                maxLines = 1,
                                softWrap = false,
                                autoSize = TextAutoSize.StepBased(minFontSize = 12.sp, maxFontSize = 34.sp),
                            )
                            Text(TraktWords.WAITS, fontSize = 15.sp, color = Flexarr.usher)
                        }
                    }
                }
                item {
                    TvSettingsRow(
                        "Cancel", onClick = { trakt.run(TraktSpot.Account) { it.cancelTraktConnect() } }, modifier = Modifier.focusRequester(focus),
                        icon = Icons.Filled.Close,
                    )
                }
                trakt.problemAt(TraktSpot.Account)?.let { item { Problem(it) } }
            }
            else -> {
                item {
                    TvSettingsRow(
                        status.connectLabel, onClick = { trakt.run(TraktSpot.Account) { it.connectTrakt() } }, modifier = Modifier.focusRequester(focus),
                        icon = Icons.Filled.Link, help = status.connectNote,
                    )
                }
                trakt.problemAt(TraktSpot.Account)?.let { item { Problem(it) } }
            }
        }
    }

    if (confirmDisconnect) {
        val cancel = remember { FocusRequester() }
        Dialog(onDismissRequest = { confirmDisconnect = false }) {
            Column(Modifier.width(400.dp).background(Flexarr.seat, RoundedCornerShape(14.dp)).padding(24.dp), verticalArrangement = Arrangement.spacedBy(14.dp)) {
                Text(TraktWords.DISCONNECT_TITLE, fontSize = 18.sp, fontWeight = FontWeight.Bold)
                Text(TraktWords.DISCONNECT_TEXT, fontSize = 14.sp, color = Flexarr.usher)
                Row(Modifier.fillMaxWidth().height(44.dp), horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                    TvActionButton("Disconnect", onClick = {
                        confirmDisconnect = false
                        trakt.run(TraktSpot.Account) { it.disconnectTrakt() }
                    }, prominent = true, ambient = Flexarr.danger)
                    TvActionButton("Cancel", onClick = { confirmDisconnect = false }, modifier = Modifier.focusRequester(cancel))
                }
            }
        }
        TakeFocus(cancel)
    }
}

/** What went wrong, under the part it went wrong in. */
@Composable
private fun Problem(text: String) {
    Text(text, fontSize = 13.5.sp, color = Flexarr.danger, modifier = Modifier.padding(start = 16.dp, top = 6.dp, bottom = 4.dp))
}
