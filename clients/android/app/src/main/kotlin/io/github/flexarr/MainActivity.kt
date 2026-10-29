package io.github.flexarr

import android.content.Intent
import android.graphics.Color
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.SystemBarStyle
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
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
     */
    private fun signInFromLaunch(intent: Intent) {
        if (!BuildConfig.DEBUG) return
        val model = appModel
        if (intent.hasExtra("noHistory")) model.recordsHistory = !intent.getBooleanExtra("noHistory", false)
        val server = intent.getStringExtra("server")
        val key = intent.getStringExtra("apiKey")
        if (server != null && key != null) model.signInForThisLaunch(server, key)
    }
}
