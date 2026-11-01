package io.github.flexarr.ui.tv

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.WifiOff
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.semantics.heading
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.window.Dialog
import androidx.tv.material3.Icon
import androidx.tv.material3.Text
import io.github.flexarr.AppModel
import io.github.flexarr.ui.connect.tunnelProblemWords
import io.github.flexarr.ui.theme.Flexarr
import kotlinx.coroutines.launch

/**
 * The server can't be reached (no network, or no answer within a few seconds). There's nothing to play without it on
 * the TV, so it says so straight away, tries again by itself, and carries on once the server answers. Sign out (Leave,
 * for a family guest) is the way out when the server has gone for good, or moved. As the Apple TV's OfflineView.
 */
@Composable
fun TvOfflineScreen(model: AppModel) {
    val again = remember { FocusRequester() }
    val scope = rememberCoroutineScope()
    var checking by remember { mutableStateOf(false) }
    var confirmSignOut by remember { mutableStateOf(false) }
    val owner = model.credentials?.guest?.ownerName?.ifEmpty { null }
    // As the tunnel's problem screen words it.
    val (title, line) = tunnelProblemWords(model)
    Column(
        Modifier.fillMaxSize().padding(60.dp).testTag("offline"),
        verticalArrangement = Arrangement.spacedBy(16.dp, Alignment.CenterVertically),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Icon(Icons.Filled.WifiOff, contentDescription = null, tint = Flexarr.silver, modifier = Modifier.size(40.dp))
        Text(title, fontSize = 26.sp, fontWeight = FontWeight.Bold, textAlign = TextAlign.Center, modifier = Modifier.semantics { heading() })
        Text(line, fontSize = 16.sp, color = Flexarr.usher, textAlign = TextAlign.Center, modifier = Modifier.widthIn(max = 560.dp))
        Row(Modifier.padding(top = 10.dp), horizontalArrangement = Arrangement.spacedBy(15.dp)) {
            // Keeps its label while it tries: a press meanwhile simply waits for the same check.
            TvActionButton(
                "Try again",
                onClick = {
                    if (!checking) scope.launch {
                        checking = true
                        model.checkConnection(ignoringNetwork = true)
                        checking = false
                    }
                },
                prominent = true,
                modifier = Modifier.focusRequester(again),
            )
            TvActionButton(if (owner == null) "Sign out" else "Leave", onClick = { confirmSignOut = true })
        }
    }
    TakeFocus(again, key = confirmSignOut, ready = !confirmSignOut)

    if (confirmSignOut) {
        val cancel = remember { FocusRequester() }
        Dialog(onDismissRequest = { confirmSignOut = false }) {
            Column(Modifier.width(400.dp).background(Flexarr.seat, RoundedCornerShape(14.dp)).padding(24.dp), verticalArrangement = Arrangement.spacedBy(14.dp)) {
                Text(if (owner != null) "Leave $owner’s Flexarr?" else "Sign out of this server?", fontSize = 18.sp, fontWeight = FontWeight.Bold)
                if (owner != null) Text("Joining again takes a new code.", fontSize = 14.sp, color = Flexarr.usher)
                // As the Apple TV: Cancel first, and where focus starts.
                Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                    TvActionButton("Cancel", onClick = { confirmSignOut = false }, prominent = true, modifier = Modifier.focusRequester(cancel))
                    TvActionButton(if (owner != null) "Leave" else "Sign out", onClick = {
                        confirmSignOut = false
                        model.signOut()
                    }, ambient = Flexarr.danger)
                }
            }
        }
        TakeFocus(cancel)
    }
}
