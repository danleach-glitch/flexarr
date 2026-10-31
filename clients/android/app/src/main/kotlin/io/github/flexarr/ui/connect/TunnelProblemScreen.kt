package io.github.flexarr.ui.connect

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.widthIn
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.WifiOff
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.github.flexarr.AppModel
import io.github.flexarr.ui.components.ActionButton
import io.github.flexarr.ui.settings.LeaveDialog
import io.github.flexarr.ui.theme.Flexarr

/** "Can’t reach Dan’s Flexarr", and the line under it: shared by the phone's and the TV's problem screens. */
internal fun tunnelProblemWords(model: AppModel): Pair<String, String> {
    val owner = model.credentials?.guest?.ownerName?.ifEmpty { null }
    return if (owner != null) {
        "Can’t reach $owner’s Flexarr" to "Check this device’s internet, or that $owner’s server is on."
    } else {
        "Can’t reach this Flexarr" to "Check this device’s internet, or that its server is on."
    }
}

/**
 * A family guest's tunnel couldn't reach its host's server ([AppModel.tunnelProblem]): Try again, Downloads (which play
 * without the server) when there are some, and Leave, asked about first. As the Apple apps' TunnelProblemView.
 */
@Composable
fun TunnelProblemScreen(model: AppModel) {
    var confirmLeave by remember { mutableStateOf(false) }
    val (title, line) = tunnelProblemWords(model)
    Box(Modifier.fillMaxSize().safeDrawingPadding().padding(32.dp), contentAlignment = Alignment.Center) {
        Column(Modifier.widthIn(max = 520.dp), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(16.dp)) {
            Icon(Icons.Filled.WifiOff, contentDescription = null, tint = Flexarr.silver, modifier = Modifier.size(44.dp))
            Text(title, fontSize = 22.sp, fontWeight = FontWeight.Bold, color = Flexarr.screen, textAlign = TextAlign.Center)
            Text(line, fontSize = 17.sp, color = Flexarr.usher, textAlign = TextAlign.Center)
            FlowRow(
                Modifier.padding(top = 8.dp),
                horizontalArrangement = Arrangement.spacedBy(12.dp, Alignment.CenterHorizontally),
                verticalArrangement = Arrangement.spacedBy(12.dp),
            ) {
                ActionButton("Try again", onClick = model::retryGuestTunnel, prominent = true)
                if (model.downloads.downloads.isNotEmpty()) ActionButton("Downloads", onClick = model::browseOfflineAsGuest)
                ActionButton("Leave", onClick = { confirmLeave = true }, ambient = Flexarr.danger)
            }
        }
    }
    if (confirmLeave) LeaveDialog(model, onDismiss = { confirmLeave = false })
}
