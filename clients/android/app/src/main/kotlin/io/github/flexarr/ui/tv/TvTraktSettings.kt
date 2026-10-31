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
        status == null -> null
        status.isConnected -> "connected/${status.importedAt == null}/${status.importing}/${trakt.preview != null}"
        code != null -> "code"
        else -> "connect"
    }
    val focus = remember { FocusRequester() }
    TakeFocus(focus, key = phase, ready = phase != null)

    TvSettingsPage("Trakt", summary = "Keep what you watch, your place and your watchlist in step with Trakt.") {
        when {
            status == null -> {}
            status.isConnected -> {
                item { TvSettingsFact("Account", status.username ?: "Connected") }
                item { TvSettingsFact("Synced", status.lastSync?.let(::relativeTime) ?: "Not yet") }
                if (status.queued > 0) item { TvSettingsFact("Waiting to send", "${status.queued}") }
                status.syncNote?.let { note -> item { TvSettingsNote(note, Modifier.padding(vertical = 4.dp)) } }

                val offersImport = status.importedAt == null
                if (offersImport) {
                    val preview = trakt.preview
                    item {
                        val top = Modifier.padding(top = 18.dp).focusRequester(focus)
                        when {
                            status.importing -> TvSettingsRow("Bringing your history across…", onClick = {}, modifier = top, icon = Icons.Filled.History)
                            preview != null -> TvSettingsRow(
                                "Bring across", onClick = { trakt.run { it.confirmTraktImport() } }, modifier = top, icon = Icons.Filled.History,
                                value = preview.summary(), help = preview.details() + " Nothing is marked unwatched, on either side.",
                            )
                            else -> TvSettingsRow(
                                "Bring your history across", onClick = { trakt.run { client -> trakt.preview = client.previewTraktImport() } },
                                modifier = top, icon = Icons.Filled.History, help = "See what changes first.",
                            )
                        }
                    }
                }
                item {
                    TvSettingsRow(
                        "Sync now", onClick = { trakt.run { it.syncTraktNow() } },
                        modifier = if (offersImport) Modifier else Modifier.padding(top = 18.dp).focusRequester(focus),
                        icon = Icons.Filled.Sync, help = "Flexarr syncs every few minutes by itself.",
                    )
                }
                item {
                    TvSettingsRow(
                        "Disconnect", onClick = { confirmDisconnect = true }, icon = Icons.Filled.LinkOff, destructive = true,
                        help = "Stop syncing this profile with Trakt.",
                    )
                }
                trakt.lists?.takeIf { it.isNotEmpty() }?.let { lists ->
                    item { TvSettingsGroupLabel("Lists", Modifier.padding(top = 10.dp)) }
                    lists.forEach { list ->
                        item(key = list.id) {
                            TvSettingsToggle(
                                list.name, isOn = list.selected, flip = { if (!trakt.savingLists) trakt.choose(list, !list.selected) },
                                icon = Icons.AutoMirrored.Filled.List, help = "${list.detail}. Chosen lists show as collections.",
                            )
                        }
                    }
                }
            }
            code != null -> {
                item {
                    Row(
                        Modifier.padding(start = 16.dp, bottom = 20.dp).clearAndSetSemantics {
                            contentDescription = "Go to ${code.shortVerificationUrl} and enter ${code.userCode.toList().joinToString(" ")}"
                        },
                        horizontalArrangement = Arrangement.spacedBy(28.dp),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        QrCode(code.qrUrl, Modifier.size(150.dp))
                        Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
                            Text("On your phone, go to", fontSize = 15.sp, color = Flexarr.usher)
                            Text(code.shortVerificationUrl, fontSize = 17.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen)
                            Text("and enter", fontSize = 15.sp, color = Flexarr.usher)
                            Text(code.userCode, fontSize = 34.sp, fontWeight = FontWeight.Black, fontFamily = FontFamily.Monospace, letterSpacing = 3.sp, color = Flexarr.screen)
                        }
                    }
                }
                item {
                    TvSettingsRow(
                        "Cancel", onClick = { trakt.run { it.cancelTraktConnect() } }, modifier = Modifier.focusRequester(focus),
                        icon = Icons.Filled.Close, help = "Flexarr waits for the code for 10 minutes.",
                    )
                }
            }
            else -> item {
                TvSettingsRow(
                    status.connectLabel, onClick = { trakt.run { it.connectTrakt() } }, modifier = Modifier.focusRequester(focus),
                    icon = Icons.Filled.Link, help = status.connectNote,
                )
            }
        }
        trakt.problem?.let { problem ->
            item { Text(problem, fontSize = 13.5.sp, color = Flexarr.danger, modifier = Modifier.padding(start = 16.dp, top = 12.dp)) }
        }
    }

    if (confirmDisconnect) {
        val cancel = remember { FocusRequester() }
        Dialog(onDismissRequest = { confirmDisconnect = false }) {
            Column(Modifier.width(400.dp).background(Flexarr.seat, RoundedCornerShape(14.dp)).padding(24.dp), verticalArrangement = Arrangement.spacedBy(14.dp)) {
                Text("Disconnect Trakt?", fontSize = 18.sp, fontWeight = FontWeight.Bold)
                Text("What you’ve watched stays as it is, here and on Trakt.", fontSize = 14.sp, color = Flexarr.usher)
                Row(Modifier.fillMaxWidth().height(44.dp), horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                    TvActionButton("Disconnect", onClick = {
                        confirmDisconnect = false
                        trakt.run { it.disconnectTrakt() }
                    }, prominent = true, ambient = Flexarr.danger)
                    TvActionButton("Cancel", onClick = { confirmDisconnect = false }, modifier = Modifier.focusRequester(cancel))
                }
            }
        }
        TakeFocus(cancel)
    }
}
