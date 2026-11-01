package io.github.flexarr.ui.tv

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
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
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.tv.material3.Icon
import androidx.tv.material3.Text
import io.github.flexarr.AppModel
import io.github.flexarr.ui.theme.Flexarr
import kotlinx.coroutines.launch

/**
 * The server can't be reached (no network, or no answer within a few seconds). There's nothing to play without it on
 * the TV, so it says so straight away, tries again by itself, and goes on to Home once the server answers. As the
 * Apple TV's OfflineView.
 */
@Composable
fun TvOfflineScreen(model: AppModel) {
    val again = remember { FocusRequester() }
    val scope = rememberCoroutineScope()
    var checking by remember { mutableStateOf(false) }
    Column(
        Modifier.fillMaxSize().padding(60.dp),
        verticalArrangement = Arrangement.spacedBy(16.dp, Alignment.CenterVertically),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Icon(Icons.Filled.WifiOff, contentDescription = null, tint = Flexarr.silver, modifier = Modifier.size(40.dp))
        Text("Can’t reach your server", fontSize = 26.sp, fontWeight = FontWeight.Bold, textAlign = TextAlign.Center)
        Text("Check the network, or that Flexarr is running.", fontSize = 16.sp, color = Flexarr.usher, textAlign = TextAlign.Center)
        TvActionButton(
            if (checking) "Trying…" else "Try again",
            onClick = {
                if (!checking) scope.launch {
                    checking = true
                    model.checkConnection()
                    checking = false
                }
            },
            prominent = true,
            modifier = Modifier.padding(top = 10.dp).focusRequester(again),
        )
    }
    TakeFocus(again)
}
