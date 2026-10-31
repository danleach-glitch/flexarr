package io.github.flexarr.ui.settings

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.key
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.semantics.LiveRegionMode
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.liveRegion
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.text.withStyle
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.github.flexarr.AppModel
import io.github.flexarr.core.FlexarrException
import io.github.flexarr.core.WaitingDevice
import io.github.flexarr.ui.components.ActionButton
import io.github.flexarr.ui.components.LocalNavigator
import io.github.flexarr.ui.components.PageTitle
import io.github.flexarr.ui.pairing.MainPinField
import io.github.flexarr.ui.pairing.PinProblem
import io.github.flexarr.ui.pairing.allowAsMainProfile
import io.github.flexarr.ui.pairing.deviceIcon
import io.github.flexarr.ui.pairing.mayAllow
import io.github.flexarr.ui.pairing.pairingProblem
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.RoomBackground
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import java.time.Duration
import java.time.Instant

/** The waiting devices on a page of their own (kept for links to it; Settings shows them itself). */
@Composable
fun WaitingDevicesScreen(model: AppModel) {
    val navigator = LocalNavigator.current
    RoomBackground(ambient = null) {
        LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 32.dp), verticalArrangement = Arrangement.spacedBy(28.dp)) {
            item { PageTitle("Devices waiting to connect", onBack = { navigator.back() }) }
            item { WaitingDevicesSection(model, header = null) }
        }
    }
}

/**
 * Devices showing a code and waiting to connect, for the main profile to allow or turn away here: the same list the
 * prompt asks from ([io.github.flexarr.PairingPrompts]), so one closed there can still be let in. In Settings, as the
 * iPhone's.
 */
@Composable
fun WaitingDevicesSection(model: AppModel, header: String? = "Devices waiting to connect") {
    val client = model.client ?: return
    val scope = rememberCoroutineScope()
    val pairing = model.pairing
    var loading by remember { mutableStateOf(true) }
    var problem by remember { mutableStateOf<String?>(null) }
    /** The one being answered now. */
    var sending by remember { mutableStateOf<String?>(null) }
    /** What came of the last answer: "“Living room” is connected." */
    var outcome by remember { mutableStateOf<String?>(null) }
    /** The main profile's PIN, while allowing takes it here. */
    var pin by remember { mutableStateOf("") }
    /** Locked here, Allow asks for the PIN first (as on iPhone), once for any of them. */
    var asksPin by remember { mutableStateOf(false) }

    // The server's list on opening, as the stream only brings what changes after it's open.
    LaunchedEffect(model.credentials) {
        problem = try {
            pairing.reload(client)
            null
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            pairingProblem(e)
        }
        loading = false
    }
    // A code runs out (the device shows a new one, which asks again).
    LaunchedEffect(pairing.waiting) {
        val next = pairing.waiting.minByOrNull { it.expiresAt } ?: return@LaunchedEffect
        delay(Duration.between(Instant.now(), next.expiresAt).toMillis().coerceAtLeast(0))
        pairing.ended(next.code)
    }

    fun answer(device: WaitingDevice, allow: Boolean) {
        if (sending != null) return
        if (allow && !model.isAdmin && !asksPin) {
            asksPin = true
            return
        }
        if (allow && !mayAllow(model, pin)) return
        sending = device.code
        outcome = null
        scope.launch {
            try {
                if (allow) {
                    allowAsMainProfile(model, client, device.code, pin)
                    pairing.finished(device.code)
                    outcome = "“${device.deviceName}” is connected. It’ll be ready in a moment."
                } else {
                    pairing.decline(client, device.code)
                }
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                if (e is PinProblem) pin = ""
                outcome = pairingProblem(e)
                // Run out: it goes from the list (the device shows a new code, which asks again).
                if ((e as? FlexarrException.Http)?.status == 404) pairing.finished(device.code)
            } finally {
                sending = null
            }
        }
    }

    Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
        outcome?.let { message ->
            Text(
                message,
                fontSize = 15.sp,
                color = Flexarr.screen,
                modifier = Modifier.padding(horizontal = 32.dp).semantics { liveRegion = LiveRegionMode.Polite },
            )
        }
        val waiting = pairing.waiting
        Group(
            header = header,
            footer = problem ?: listOfNotNull(
                "Allow one only if its screen shows the same code.".takeIf { waiting.isNotEmpty() },
                // Without them, a device asking is only heard about while Flexarr is open here.
                "Turn on Notifications to be asked when Flexarr isn't open.".takeIf { !model.notifications.isOn },
            ).joinToString(" ").ifEmpty { null },
        ) {
            // Locked here: allowing takes the main profile's PIN, given once for any of them.
            if (asksPin && !model.isAdmin && waiting.isNotEmpty()) {
                MainPinField(model, pin, onPin = { pin = it }, onDone = {}, Modifier.padding(horizontal = 16.dp, vertical = 14.dp), fieldColor = Flexarr.aisle)
                Divider()
            }
            when {
                waiting.isEmpty() && loading -> Row(Modifier.fillMaxWidth().padding(16.dp)) {
                    CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp, color = Flexarr.usher)
                }
                waiting.isEmpty() -> Text(
                    "No devices are waiting. When one asks to connect, it shows here and your phone asks you.",
                    fontSize = 15.sp,
                    color = Flexarr.usher,
                    modifier = Modifier.padding(horizontal = 16.dp, vertical = 14.dp),
                )
                else -> waiting.forEachIndexed { index, device ->
                    key(device.code) {
                        if (index > 0) Divider()
                        WaitingDeviceRow(
                            device,
                            sending = sending == device.code,
                            enabled = sending == null,
                            mayAllow = !asksPin || mayAllow(model, pin),
                            onAnswer = { answer(device, it) },
                        )
                    }
                }
            }
        }
    }
}

@Composable
private fun WaitingDeviceRow(device: WaitingDevice, sending: Boolean, enabled: Boolean, mayAllow: Boolean, onAnswer: (Boolean) -> Unit) {
    Column(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 14.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        Row(horizontalArrangement = Arrangement.spacedBy(14.dp), verticalAlignment = Alignment.CenterVertically) {
            Icon(deviceIcon(device.deviceType), contentDescription = null, tint = Flexarr.silver, modifier = Modifier.size(26.dp))
            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(3.dp)) {
                Text(device.deviceName, fontSize = 17.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen, maxLines = 1, overflow = TextOverflow.Ellipsis)
                Text(device.shortSummary, fontSize = 13.sp, color = Flexarr.usher, maxLines = 1, overflow = TextOverflow.Ellipsis)
                if (!device.asks) Text("You turned this away. Allow it if it's yours.", fontSize = 13.sp, color = Flexarr.usher)
                Text(
                    buildAnnotatedString {
                        append("Code ")
                        withStyle(SpanStyle(fontWeight = FontWeight.Bold, color = Flexarr.screen)) { append(device.code) }
                    },
                    fontSize = 15.sp,
                    color = Flexarr.usher,
                    // Read a character at a time, as it's checked against the screen.
                    modifier = Modifier.semantics { contentDescription = "Code " + device.code.toList().joinToString(" ") },
                )
            }
            if (sending) CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp, color = Flexarr.screen)
        }
        Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
            ActionButton("Allow", { onAnswer(true) }, Modifier.weight(1f), prominent = true, enabled = enabled && mayAllow)
            ActionButton("Don’t allow", { onAnswer(false) }, Modifier.weight(1f), enabled = enabled)
        }
    }
}
