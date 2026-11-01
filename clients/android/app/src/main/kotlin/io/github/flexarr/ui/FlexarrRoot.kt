package io.github.flexarr.ui

import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Surface
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import io.github.flexarr.AppModel
import androidx.compose.animation.Crossfade
import io.github.flexarr.ui.connect.ConnectScreen
import io.github.flexarr.ui.connect.InviteDialog
import io.github.flexarr.ui.connect.TunnelProblemScreen
import io.github.flexarr.ui.connect.WelcomeScreen
import io.github.flexarr.ui.connect.rememberWelcomed
import io.github.flexarr.ui.profiles.ProfilesScreen
import kotlinx.coroutines.delay

/** The welcome pages (a fresh install), connecting, choosing who's watching, or the app itself. */
@Composable
fun FlexarrRoot(model: AppModel) {
    val (welcomed, welcome) = rememberWelcomed()
    Surface(Modifier.fillMaxSize()) {
        when (val phase = model.phase) {
            // Only a fresh install gets the welcome: anyone who has connected before knows about the server.
            AppModel.Phase.SignedOut -> Crossfade(!welcomed && model.store.lastAddress == null, label = "welcome") { welcoming ->
                if (welcoming) WelcomeScreen(onDone = welcome) else ConnectScreen(model)
            }
            is AppModel.Phase.SignedIn -> {
                LoadProfiles(model, phase)
                when {
                    // A guest whose tunnel can't reach its host: what to do about it (Downloads among it).
                    model.tunnelProblem != null -> TunnelProblemScreen(model)
                    !model.profilesLoaded -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) { CircularProgressIndicator() }
                    model.needsProfile -> ProfilesScreen(model)
                    else -> MainScreen(model)
                }
            }
        }
        InviteDialog(model)
    }
}

/** Signed in: who's watching is asked for, and asked again until the server answers. The phone's and the TV's roots. */
@Composable
internal fun LoadProfiles(model: AppModel, phase: AppModel.Phase.SignedIn) {
    LaunchedEffect(phase.credentials.address, phase.credentials.apiKey) {
        model.loadProfiles()
        model.refreshRemoteAddress()
        // A list that couldn't be read (the network not up yet as the app starts, say) is asked for again: without it
        // Home has no profile button and nobody can switch.
        var wait = 2_000L
        // Offline mode tries again by itself, so this stops once it's on.
        while (model.profiles.isEmpty() && !model.serverUnreachable) {
            delay(wait)
            model.loadProfiles()
            wait = (wait * 2).coerceAtMost(30_000L)
        }
    }
}
