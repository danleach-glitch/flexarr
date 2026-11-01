package io.github.flexarr.ui.tv

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import io.github.flexarr.AppModel
import io.github.flexarr.ui.LoadProfiles
import io.github.flexarr.ui.theme.Flexarr

/** The TV's way in, as the phone's FlexarrRoot: connecting, choosing who's watching, or the app itself. */
@Composable
fun TvRoot(model: AppModel) {
    TvTheme {
        Box(Modifier.fillMaxSize().background(Flexarr.room)) {
            when (val phase = model.phase) {
                AppModel.Phase.SignedOut -> TvConnectScreen(model)
                is AppModel.Phase.SignedIn -> {
                    LoadProfiles(model, phase)
                    when {
                        model.tunnelProblem != null -> TvTunnelProblemScreen(model)
                        model.serverUnreachable -> TvOfflineScreen(model)
                        !model.profilesLoaded -> CircularProgressIndicator(Modifier.align(Alignment.Center), color = Flexarr.screen)
                        model.needsProfile -> TvProfilePicker(model)
                        else -> TvMainScreen(model)
                    }
                }
            }
        }
    }
}
