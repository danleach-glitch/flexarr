package io.github.flexarr

import okhttp3.HttpUrl.Companion.toHttpUrlOrNull
import io.github.flexarr.logging.FlexarrLog
import android.content.Intent
import android.content.pm.ActivityInfo
import android.graphics.Color
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.SystemBarStyle
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.core.splashscreen.SplashScreen.Companion.installSplashScreen
import kotlinx.coroutines.launch
import io.github.flexarr.core.FlexarrClient
import io.github.flexarr.core.GuestInvite
import io.github.flexarr.core.PushPayload
import io.github.flexarr.notifications.FlexarrMessagingService
import io.github.flexarr.player.PlayerActivity
import io.github.flexarr.ui.FlexarrRoot
import io.github.flexarr.ui.theme.FlexarrTheme
import io.github.flexarr.ui.tv.TvRoot

private val flexarrLog = FlexarrLog("launch")

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        installSplashScreen()
        enableEdgeToEdge(SystemBarStyle.dark(Color.TRANSPARENT), SystemBarStyle.dark(Color.TRANSPARENT))
        super.onCreate(savedInstanceState)
        // Upright on a phone; a tablet turns either way, as an iPad does (and a TV is a TV).
        requestedOrientation = if (isTelevision || resources.configuration.smallestScreenWidthDp >= 600) {
            ActivityInfo.SCREEN_ORIENTATION_UNSPECIFIED
        } else {
            ActivityInfo.SCREEN_ORIENTATION_PORTRAIT
        }
        if (savedInstanceState == null) {
            signInFromLaunch(intent)
            openLink(intent)
        }
        setContent {
            FlexarrTheme {
                if (usesTvInterface) TvRoot(appModel) else FlexarrRoot(appModel)
            }
        }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        openLink(intent)
    }

    /**
     * A notification, shortcut, the widget or another app's `flexarr://` link opened the app somewhere ([AppLink]);
     * [AppLink.received] keeps one from another app from playing anything.
     */
    private fun openLink(intent: Intent) {
        when (val link = AppLink.received(this, intent)) {
            null -> Unit
            // Asked about first ("Join Dan's Flexarr as Gran?"): it could have come from any app or web page.
            // Not in builds without family invites yet (BuildConfig.GUEST_INVITES), which say so.
            is AppLink.Join ->
                if (BuildConfig.GUEST_INVITES) appModel.offerInvite(GuestInvite.decode(link.payload)) else appModel.inviteProblem = "Update Flexarr to join."
            else -> appModel.pendingLink = link
        }
    }

    /**
     * Debug builds (and the ones profiled for the baseline profile) sign in from the command line, like the Apple apps' launch arguments, for previews and testing:
     * `adb shell am start -n com.danleach.flexarr/io.github.flexarr.MainActivity --es server <url> --es apiKey <key> --ez noHistory true`.
     * `--es remote <url>` gives the address for away from home up front (try an unreachable `server` to be away).
     * `--es profile <id>` chooses who's watching without the picker, and `--es play <itemId>` (with a position in
     * seconds as `--ei at <seconds>`) opens the player on that title, so a test needn't tap its way there. With it,
     * `--es subtitle <key>`, `--es subtitlePosition Top`, `--es subtitleSize Large` and `--ez blackBars true` set the
     * subtitles as the menu would, without saving them. `--es notify '<payload json>'` shows a notification as the
     * server would send it.
     */
    private fun signInFromLaunch(intent: Intent) {
        if (!BuildConfig.LAUNCH_ARGUMENTS) return
        val model = appModel
        if (intent.hasExtra("noHistory")) model.recordsHistory = !intent.getBooleanExtra("noHistory", false)
        // Debug builds only: `--es pairRelay http://10.0.2.2:8795/api/pair` points "Join someone’s Flexarr" at a local relay.
        intent.getStringExtra("pairRelay")?.toHttpUrlOrNull()?.let { model.pairRelay = it }
        val server = intent.getStringExtra("server")
        val key = intent.getStringExtra("apiKey")
        if (server != null && key != null) model.signInForThisLaunch(server, key, remote = intent.getStringExtra("remote"))
        // `--es notify '<payload json>'` shows a notification as if it had come from the server, to try its look and buttons.
        intent.getStringExtra("notify")?.let { json ->
            runCatching { FlexarrClient.json.decodeFromString(PushPayload.serializer(), json) }
                .onSuccess { payload -> Thread { FlexarrMessagingService.show(applicationContext, payload) }.start() }
        }
        val profile = intent.getStringExtra("profile")
        val play = intent.getStringExtra("play")
        if (profile == null && play == null) return
        model.scope.launch {
            if (profile != null) {
                model.loadProfiles()
                val chosen = model.profiles.firstOrNull { it.id == profile }
                val result = chosen?.let { runCatching { model.chooseProfile(it) } }
                flexarrLog.info("Debug launch: profile $profile ${if (chosen == null) "not found among ${model.profiles.size}" else if (result?.isSuccess == true) "chosen" else "failed: ${result?.exceptionOrNull()}"}")
            }
            flexarrLog.info("Debug launch: playing $play")
            if (play != null) {
                val player = PlayerActivity.intent(this@MainActivity, play, fromBeginning = false, startAt = intent.getIntExtra("at", -1).takeIf { it >= 0 })
                listOf("subtitle", "subtitlePosition", "subtitleSize").forEach { key -> intent.getStringExtra(key)?.let { player.putExtra(key, it) } }
                if (intent.hasExtra("blackBars")) player.putExtra("blackBars", intent.getBooleanExtra("blackBars", false))
                startActivity(player)
            }
        }
    }
}
