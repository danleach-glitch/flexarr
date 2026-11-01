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
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
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
import io.github.flexarr.ui.theme.Tokens

/**
 * A family guest's tunnel couldn't reach its host's server ([AppModel.tunnelProblem]), on the TV: Try again (focused)
 * and Leave, asked about first. As the Apple TV's TunnelProblemView.
 */
@Composable
fun TvTunnelProblemScreen(model: AppModel) {
    var confirmLeave by remember { mutableStateOf(false) }
    val again = remember { FocusRequester() }
    val (title, line) = tunnelProblemWords(model)
    Column(
        Modifier.fillMaxSize().padding(60.dp),
        verticalArrangement = Arrangement.spacedBy(16.dp, Alignment.CenterVertically),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Icon(Icons.Filled.WifiOff, contentDescription = null, tint = Flexarr.silver, modifier = Modifier.size(40.dp))
        Text(title, fontSize = 26.sp, fontWeight = FontWeight.Bold, textAlign = TextAlign.Center)
        Text(line, fontSize = 16.sp, color = Flexarr.usher, textAlign = TextAlign.Center, modifier = Modifier.widthIn(max = 560.dp))
        Row(Modifier.padding(top = 10.dp), horizontalArrangement = Arrangement.spacedBy(15.dp)) {
            TvActionButton("Try again", onClick = model::retryGuestTunnel, prominent = true, modifier = Modifier.focusRequester(again))
            TvActionButton("Leave", onClick = { confirmLeave = true })
        }
    }
    TakeFocus(again, key = confirmLeave, ready = !confirmLeave)

    if (confirmLeave) {
        val cancel = remember { FocusRequester() }
        val owner = model.credentials?.guest?.ownerName?.ifEmpty { null }
        Dialog(onDismissRequest = { confirmLeave = false }) {
            Column(Modifier.width(400.dp).background(Flexarr.seat, RoundedCornerShape(Tokens.radiusPanel)).padding(24.dp), verticalArrangement = Arrangement.spacedBy(14.dp)) {
                Text(owner?.let { "Leave $it’s Flexarr?" } ?: "Leave this Flexarr?", fontSize = 18.sp, fontWeight = FontWeight.Bold)
                Text("Joining again takes a new code.", fontSize = 14.sp, color = Flexarr.usher)
                // As the Apple TV: Cancel first, and where focus starts; Leave after it.
                Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                    TvActionButton("Cancel", onClick = { confirmLeave = false }, prominent = true, modifier = Modifier.focusRequester(cancel))
                    TvActionButton("Leave", onClick = {
                        confirmLeave = false
                        model.signOut()
                    }, ambient = Flexarr.danger)
                }
            }
        }
        TakeFocus(cancel)
    }
}
