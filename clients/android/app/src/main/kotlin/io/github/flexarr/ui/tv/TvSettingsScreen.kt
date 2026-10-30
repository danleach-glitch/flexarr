package io.github.flexarr.ui.tv

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.window.Dialog
import androidx.tv.material3.ClickableSurfaceDefaults
import androidx.tv.material3.Surface
import androidx.tv.material3.Text
import io.github.flexarr.AppModel
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.RoomBackground
import io.github.flexarr.ui.theme.marquee

/**
 * Settings, so far: who's watching, the server, and signing out. The rest of the Apple TV's Settings (playback, Home,
 * libraries, the sidebar, diagnostics) comes with the other screens (docs/android-tv.md).
 */
@Composable
fun TvSettingsScreen(model: AppModel, onSwitchProfile: () -> Unit) {
    var confirmingSignOut by remember { mutableStateOf(false) }
    val first = remember { FocusRequester() }
    LaunchedEffect(Unit) { runCatching { first.requestFocus() } }
    RoomBackground(ambient = null) {
        Column(Modifier.fillMaxSize().padding(start = TvLayout.Edge, top = 40.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            Text("Settings", style = marquee(38), modifier = Modifier.padding(bottom = 14.dp))
            if (model.profiles.size > 1) {
                SettingsRow("Who’s watching", model.currentProfile?.name, onClick = onSwitchProfile, modifier = Modifier.focusRequester(first))
            }
            SettingsRow("Server", model.credentials?.address, onClick = {}, modifier = if (model.profiles.size > 1) Modifier else Modifier.focusRequester(first))
            SettingsRow("Sign out", null, onClick = { confirmingSignOut = true })
        }
    }
    if (confirmingSignOut) {
        val cancel = remember { FocusRequester() }
        Dialog(onDismissRequest = { confirmingSignOut = false }) {
            Column(Modifier.width(400.dp).background(Flexarr.seat, RoundedCornerShape(14.dp)).padding(24.dp), verticalArrangement = Arrangement.spacedBy(14.dp)) {
                Text("Sign out of this server?", fontSize = 18.sp, fontWeight = FontWeight.Bold)
                Text("This TV will need a new code to connect again.", fontSize = 14.sp, color = Flexarr.usher)
                Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                    TvActionButton("Sign out", onClick = {
                        confirmingSignOut = false
                        model.signOut()
                    }, prominent = true, ambient = Flexarr.danger)
                    TvActionButton("Cancel", onClick = { confirmingSignOut = false }, modifier = Modifier.focusRequester(cancel))
                }
            }
        }
        LaunchedEffect(Unit) { runCatching { cancel.requestFocus() } }
    }
}

/** A setting's name, with its value at the right. */
@Composable
private fun SettingsRow(title: String, value: String?, onClick: () -> Unit, modifier: Modifier = Modifier) {
    Surface(
        onClick = onClick,
        modifier = modifier.width(560.dp).height(46.dp),
        shape = ClickableSurfaceDefaults.shape(RoundedCornerShape(10.dp)),
        colors = ClickableSurfaceDefaults.colors(
            containerColor = Flexarr.screen.copy(alpha = 0.08f),
            contentColor = Flexarr.screen,
            focusedContainerColor = Flexarr.screen,
            focusedContentColor = Flexarr.room,
        ),
        scale = ClickableSurfaceDefaults.scale(focusedScale = 1.02f),
    ) {
        Row(Modifier.fillMaxWidth().height(46.dp).padding(horizontal = 18.dp), horizontalArrangement = Arrangement.SpaceBetween, verticalAlignment = Alignment.CenterVertically) {
            Text(title, fontSize = 15.sp, fontWeight = FontWeight.SemiBold)
            value?.let { Text(it, fontSize = 14.sp, maxLines = 1) }
        }
    }
}
