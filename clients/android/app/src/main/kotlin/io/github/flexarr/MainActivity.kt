package io.github.flexarr

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
import io.github.flexarr.core.PushPayload
import io.github.flexarr.notifications.FlexarrMessagingService
import io.github.flexarr.player.PlayerActivity
import io.github.flexarr.ui.FlexarrRoot
import io.github.flexarr.ui.theme.FlexarrTheme

private val flexarrLog = FlexarrLog("launch")

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        installSplashScreen()
        enableEdgeToEdge(SystemBarStyle.dark(Color.TRANSPARENT), SystemBarStyle.dark(Color.TRANSPARENT))
        super.onCreate(savedInstanceState)
        // Upright on a phone; a tablet turns either way, as an iPad does.
        requestedOrientation = if (resources.configuration.smallestScreenWidthDp >= 600) {
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
                FlexarrRoot(appModel)
            }
        }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        openLink(intent)
    }

    /** A notification, shortcut or the widget opened the app somewhere ([AppLink]). */
    private fun openLink(intent: Intent) {
        AppLink.parse(intent.data)?.let { appModel.pendingLink = it }
    }

    /**
     * Debug builds sign in from the command line, like the Apple apps' launch arguments, for previews and testing:
     * `adb shell am start -n com.danleach.flexarr/io.github.flexarr.MainActivity --es server <url> --es apiKey <key> --ez noHistory true`.
     * `--es remote <url>` gives the address for away from home up front (try an unreachable `server` to be away).
     * `--es profile <id>` chooses who's watching without the picker, and `--es play <itemId>` (with a position in
     * seconds as `--ei at <seconds>`) opens the player on that title, so a test needn't tap its way there. With it,
     * `--es subtitle <key>`, `--es subtitlePosition Top`, `--es subtitleSize Large` and `--ez blackBars true` set the
     * subtitles as the menu would, without saving them. `--es notify '<payload json>'` shows a notification as the
     * server would send it.
     */
    private fun signInFromLaunch(intent: Intent) {
        if (!BuildConfig.DEBUG) return
        val model = appModel
        if (intent.hasExtra("noHistory")) model.recordsHistory = !intent.getBooleanExtra("noHistory", false)
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
