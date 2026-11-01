package io.github.flexarr.ui.settings

import android.os.Build
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.github.flexarr.AppModel
import io.github.flexarr.BuildConfig
import io.github.flexarr.core.FlexarrClient
import io.github.flexarr.core.FlexarrException
import io.github.flexarr.logging.LogHub
import io.github.flexarr.ui.components.Edge
import io.github.flexarr.ui.components.LocalNavigator
import io.github.flexarr.ui.components.PageTitle
import io.github.flexarr.ui.components.pressable
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.RoomBackground
import kotlinx.coroutines.launch
import java.time.Instant
import io.github.flexarr.ui.theme.Tokens

/**
 * Send diagnostics: this phone's recent log, to the server only, for looking into a problem, with a note saying what
 * happened. As the iPhone's DiagnosticsSettingsView.
 */
@Composable
fun DiagnosticsSettingsScreen(model: AppModel) {
    val navigator = LocalNavigator.current
    val scope = rememberCoroutineScope()
    var note by remember { mutableStateOf("") }
    var sending by remember { mutableStateOf(false) }
    var outcome by remember { mutableStateOf<String?>(null) }

    fun send() {
        val client = model.client ?: return
        if (sending) return
        sending = true
        outcome = null
        scope.launch {
            outcome = try {
                val id = LogHub.sendDiagnostics(client, diagnosticsHeader(model), note)
                note = ""
                "Sent to your server ($id). Mention that if you ask for help."
            } catch (e: FlexarrException) {
                when {
                    e is FlexarrException.UnexpectedResponse || (e is FlexarrException.Http && e.status in setOf(404, 405)) ->
                        "This server doesn’t take diagnostics yet. Update Flexarr on it, then try again."
                    else -> e.message
                }
            } finally {
                sending = false
            }
        }
    }

    RoomBackground(ambient = null) {
        LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 32.dp), verticalArrangement = Arrangement.spacedBy(28.dp)) {
            item { PageTitle("Send diagnostics", onBack = { navigator.back() }) }
            item {
                Notes(
                    footer = "Sends what this phone has logged lately to your server, and only there, so a problem can be looked into. Sign-ins and stream addresses are taken out first. (Error reports, when your server has them on, are separate: see Server.)",
                ) {
                    BasicTextField(
                        value = note,
                        onValueChange = { note = it },
                        enabled = !sending,
                        textStyle = TextStyle(fontSize = 17.sp, color = Flexarr.screen),
                        cursorBrush = SolidColor(Flexarr.screen),
                        modifier = Modifier.fillMaxWidth().heightIn(min = 72.dp).padding(horizontal = 16.dp, vertical = 12.dp),
                        decorationBox = { field ->
                            if (note.isEmpty()) Text("What happened, and roughly when? (optional)", fontSize = 17.sp, color = Flexarr.usher.copy(alpha = 0.6f))
                            field()
                        },
                    )
                }
            }
            item {
                Notes(footer = outcome) {
                    val enabled = !sending && model.client != null
                    Row(
                        Modifier.fillMaxWidth().pressable({ send() }, enabled = enabled).padding(horizontal = 16.dp, vertical = 14.dp),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        Text(if (sending) "Sending…" else "Send diagnostics", fontSize = 17.sp, color = if (enabled) Flexarr.silver else Flexarr.usher, modifier = Modifier.weight(1f))
                        if (sending) CircularProgressIndicator(Modifier.size(18.dp), color = Flexarr.usher, strokeWidth = 2.dp)
                    }
                }
            }
        }
    }
}

@Composable
private fun Notes(footer: String?, content: @Composable () -> Unit) {
    Column(Modifier.padding(horizontal = Edge), verticalArrangement = Arrangement.spacedBy(8.dp)) {
        Column(Modifier.fillMaxWidth().clip(RoundedCornerShape(Tokens.radiusPanel)).background(Flexarr.seat)) { content() }
        footer?.let { Text(it, fontSize = 13.sp, color = Flexarr.usher, modifier = Modifier.padding(horizontal = 16.dp)) }
    }
}

/** The lines on top of a diagnostics upload: which app, which device, which server (its host only), who's watching. */
internal fun diagnosticsHeader(model: AppModel): String = buildList {
    add("Flexarr for Android ${BuildConfig.VERSION_NAME} (${BuildConfig.VERSION_CODE})")
    add("Android ${Build.VERSION.RELEASE} (API ${Build.VERSION.SDK_INT}) on ${Build.MANUFACTURER} ${Build.MODEL}")
    add("Sent ${Instant.now()}")
    model.credentials?.address?.let { FlexarrClient.normalizeAddress(it)?.host }?.let { add("Server: $it") }
    if (model.credentials?.remoteAddress != null) {
        add("Connected: ${if (model.client?.addresses?.isAway == true) "away, over Tailscale" else "at home"}")
    }
    model.currentProfile?.let { add("Profile: ${it.name}") }
}.joinToString("\n")
