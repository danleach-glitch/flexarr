package io.github.flexarr

import android.content.Intent
import android.graphics.Color
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.SystemBarStyle
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import kotlinx.coroutines.launch
import io.github.flexarr.player.PlayerActivity
import io.github.flexarr.ui.FlexarrRoot
import io.github.flexarr.ui.theme.FlexarrTheme

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        enableEdgeToEdge(SystemBarStyle.dark(Color.TRANSPARENT), SystemBarStyle.dark(Color.TRANSPARENT))
        super.onCreate(savedInstanceState)
        if (savedInstanceState == null) signInFromLaunch(intent)
        setContent {
            FlexarrTheme {
                FlexarrRoot(appModel)
            }
        }
    }

    /**
     * Debug builds sign in from the command line, like the Apple apps' launch arguments, for previews and testing:
     * `adb shell am start -n com.danleach.flexarr/io.github.flexarr.MainActivity --es server <url> --es apiKey <key> --ez noHistory true`.
     * `--es profile <id>` chooses who's watching without the picker, and `--es play <itemId>` (with a position in
     * seconds as `--ei at <seconds>`) opens the player on that title, so a test needn't tap its way there. With it,
     * `--es subtitle <key>`, `--es subtitlePosition Top`, `--es subtitleSize Large` and `--ez blackBars true` set the
     * subtitles as the menu would, without saving them.
     */
    private fun signInFromLaunch(intent: Intent) {
        if (!BuildConfig.DEBUG) return
        val model = appModel
        if (intent.hasExtra("noHistory")) model.recordsHistory = !intent.getBooleanExtra("noHistory", false)
        val server = intent.getStringExtra("server")
        val key = intent.getStringExtra("apiKey")
        if (server != null && key != null) model.signInForThisLaunch(server, key)
        val profile = intent.getStringExtra("profile")
        val play = intent.getStringExtra("play")
        if (profile == null && play == null) return
        model.scope.launch {
            if (profile != null) {
                model.loadProfiles()
                model.profiles.firstOrNull { it.id == profile }?.let { runCatching { model.chooseProfile(it) } }
            }
            if (play != null) {
                val player = PlayerActivity.intent(this@MainActivity, play, fromBeginning = false, startAt = intent.getIntExtra("at", -1).takeIf { it >= 0 })
                listOf("subtitle", "subtitlePosition", "subtitleSize").forEach { key -> intent.getStringExtra(key)?.let { player.putExtra(key, it) } }
                if (intent.hasExtra("blackBars")) player.putExtra("blackBars", intent.getBooleanExtra("blackBars", false))
                startActivity(player)
            }
        }
    }
}
