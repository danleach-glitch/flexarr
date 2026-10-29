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
import io.github.flexarr.ui.connect.ConnectScreen
import io.github.flexarr.ui.profiles.ProfilesScreen

/** Connecting, choosing who's watching, or the app itself. */
@Composable
fun FlexarrRoot(model: AppModel) {
    Surface(Modifier.fillMaxSize()) {
        when (val phase = model.phase) {
            AppModel.Phase.SignedOut -> ConnectScreen(model)
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
