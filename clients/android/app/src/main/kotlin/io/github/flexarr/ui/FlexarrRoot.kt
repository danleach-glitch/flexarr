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
import io.github.flexarr.ui.connect.WelcomeScreen
import io.github.flexarr.ui.connect.rememberWelcomed
import io.github.flexarr.ui.profiles.ProfilesScreen

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
                LaunchedEffect(phase.credentials.address, phase.credentials.apiKey) {
                    model.loadProfiles()
                    model.refreshRemoteAddress()
                }
                when {
                    !model.profilesLoaded -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) { CircularProgressIndicator() }
                    model.needsProfile -> ProfilesScreen(model)
                    else -> MainScreen(model)
                }
            }
        }
    }
}
