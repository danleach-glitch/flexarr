package io.github.flexarr.ui.components

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.WifiOff
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.compositionLocalOf
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.semantics.LiveRegionMode
import androidx.compose.ui.semantics.heading
import androidx.compose.ui.semantics.liveRegion
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.github.flexarr.AppModel
import io.github.flexarr.ui.theme.Flexarr

/** The app is in offline mode: screens that fail say just that (the Offline bar says the rest). */
val LocalOffline = compositionLocalOf { false }

/** Something's downloaded for this profile: films, episodes, music, podcasts or books. */
val AppModel.hasDownloads: Boolean
    get() = downloads.downloads.isNotEmpty() || downloads.musicCollections.isNotEmpty() || bookDownloads.books.isNotEmpty()

/**
 * Home, Libraries and Search while the server can't be reached: one calm screen pointing at the downloads, which play
 * without it, rather than a spinner or an error. With nothing downloaded it just says so. The app swaps back by itself
 * once the server answers. As iPhone's. [title] keeps the tab's title, so it doesn't lose its bearings.
 */
@Composable
fun OfflineScreen(model: AppModel, title: String, onDownloads: () -> Unit) {
    Column(Modifier.fillMaxSize().background(Flexarr.room).testTag("offline")) {
        PageTitle(title)
        Column(
            Modifier.fillMaxSize().padding(32.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp, Alignment.CenterVertically),
            horizontalAlignment = Alignment.CenterHorizontally,
        ) {
            Icon(Icons.Filled.WifiOff, contentDescription = null, tint = Flexarr.silver, modifier = Modifier.size(40.dp))
            Text("You’re offline", fontSize = 22.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen, modifier = Modifier.semantics { heading() })
            if (model.hasDownloads) {
                Text("Your downloads still play.", color = Flexarr.usher, textAlign = TextAlign.Center)
                ActionButton("Go to Downloads", onClick = onDownloads, prominent = true, modifier = Modifier.padding(top = 8.dp))
            }
        }
    }
}

/**
 * The slim bar over every tab while offline. It takes the status bar's space, so the screens under it don't. Said aloud
 * as it appears.
 */
@Composable
fun OfflineBar() {
    Box(Modifier.fillMaxWidth().background(Flexarr.room).statusBarsPadding().padding(vertical = 4.dp), contentAlignment = Alignment.Center) {
        Row(
            Modifier.background(Flexarr.seat, CircleShape).padding(horizontal = 14.dp, vertical = 6.dp).testTag("offlineBar")
                .semantics { liveRegion = LiveRegionMode.Polite },
            horizontalArrangement = Arrangement.spacedBy(6.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Icon(Icons.Filled.WifiOff, contentDescription = null, tint = Flexarr.screen, modifier = Modifier.size(16.dp))
            Text("Offline", fontSize = 13.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen)
        }
    }
}
