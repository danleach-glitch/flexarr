package io.github.flexarr.tv

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import androidx.tvprovider.media.tv.TvContractCompat
import io.github.flexarr.appModel
import io.github.flexarr.data.CredentialStore
import io.github.flexarr.logging.FlexarrLog
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch

/**
 * Someone took a card out of the home screen's Watch Next row: the title leaves Continue Watching too, as the app's own
 * Remove does (servers 0.51 on; an older one keeps it, and [WatchNext] leaves the card out until it goes).
 */
class WatchNextReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != TvContractCompat.ACTION_WATCH_NEXT_PROGRAM_BROWSABLE_DISABLED) return
        val programId = intent.getLongExtra(TvContractCompat.EXTRA_WATCH_NEXT_PROGRAM_ID, -1L).takeIf { it >= 0 } ?: return
        val pending = goAsync()
        scope.launch {
            try {
                val card = WatchNext.removed(context, programId) ?: return@launch
                // An album isn't in Continue Watching: its card just stays out of the row until it's played again.
                if (card.isAlbum) return@launch
                val credentials = context.appModel.credentials ?: CredentialStore(context).load() ?: return@launch
                if (credentials.isGuest) return@launch
                val client = credentials.client()
                // Only for the profile whose card it was: a row left from before a switch isn't the new profile's.
                val current = runCatching { client.profiles().firstOrNull { it.isCurrent } }.getOrNull()
                if (current?.id != card.owner) return@launch
                runCatching { client.removeFromContinueWatching(card.id) }
                    .onSuccess { context.appModel.let { model -> launch(Dispatchers.Main) { model.watchStateChanged() } } }
                    .onFailure { log.info("Couldn't take a title out of Continue Watching", it) }
            } finally {
                pending.finish()
            }
        }
    }

    private companion object {
        val log = FlexarrLog("watch-next")
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    }
}
