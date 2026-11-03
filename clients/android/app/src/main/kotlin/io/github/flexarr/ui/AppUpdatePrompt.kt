package io.github.flexarr.ui

import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.SnackbarDuration
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.SnackbarResult
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.window.Dialog
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.tv.material3.Text
import io.github.flexarr.AppUpdates
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.Tokens
import io.github.flexarr.ui.tv.TvActionButton
import kotlinx.coroutines.delay

private const val READY = "An update is ready"
private const val RESTART = "Restart to update"

/** [AppUpdates] for this screen, checking at launch and each time the app comes back in front. */
@Composable
private fun rememberAppUpdates(): AppUpdates {
    val context = LocalContext.current
    val updates = remember { AppUpdates.create(context.applicationContext) }
    DisposableEffect(updates) { onDispose { updates.close() } }
    // Play's own download prompt; what it answers comes through AppUpdates' listener.
    val launcher = rememberLauncherForActivityResult(ActivityResultContracts.StartIntentSenderForResult()) {}
    val lifecycle by LocalLifecycleOwner.current.lifecycle.currentStateFlow.collectAsState()
    val inFront = lifecycle.isAtLeast(Lifecycle.State.RESUMED)
    LaunchedEffect(inFront) { if (inFront) updates.check(launcher) }
    return updates
}

/** The phone's: a downloaded update offers a restart along the bottom, until it's taken or dismissed. */
@Composable
fun AppUpdatePrompt(notices: SnackbarHostState) {
    val updates = rememberAppUpdates()
    LaunchedEffect(updates.isReady) {
        if (!updates.isReady) return@LaunchedEffect
        val result = notices.showSnackbar(READY, actionLabel = RESTART, withDismissAction = true, duration = SnackbarDuration.Indefinite)
        if (result == SnackbarResult.ActionPerformed) updates.restart()
    }
}

/** The TV's: a downloaded update asks in a dialog, once a launch (Later puts it off until the next). */
@Composable
fun TvAppUpdatePrompt() {
    val updates = rememberAppUpdates()
    var later by rememberSaveable { mutableStateOf(false) }
    if (!updates.isReady || later) return
    val restart = remember { FocusRequester() }
    Dialog(onDismissRequest = { later = true }) {
        Column(
            Modifier.width(380.dp).background(Flexarr.seat, RoundedCornerShape(Tokens.radiusPanel)).padding(22.dp),
            verticalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            Text(READY, fontSize = 16.sp, fontWeight = FontWeight.Bold, color = Flexarr.screen, modifier = Modifier.padding(bottom = 4.dp))
            TvActionButton(RESTART, onClick = updates::restart, prominent = true, modifier = Modifier.fillMaxWidth().focusRequester(restart))
            TvActionButton("Later", onClick = { later = true }, modifier = Modifier.fillMaxWidth())
        }
    }
    LaunchedEffect(Unit) {
        delay(50)
        runCatching { restart.requestFocus() }
    }
}
