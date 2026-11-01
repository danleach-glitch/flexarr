package io.github.flexarr.ui.pairing

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.filled.PhoneAndroid
import androidx.compose.material.icons.filled.PhoneIphone
import androidx.compose.material.icons.filled.TabletAndroid
import androidx.compose.material.icons.filled.Tv
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Text
import androidx.compose.material3.rememberModalBottomSheetState
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
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.semantics.LiveRegionMode
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.liveRegion
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.github.flexarr.AppModel
import io.github.flexarr.core.FlexarrClient
import io.github.flexarr.core.FlexarrException
import io.github.flexarr.core.WaitingDevice
import io.github.flexarr.core.waitingDevices
import io.github.flexarr.ui.components.ActionButton
import io.github.flexarr.ui.theme.Flexarr
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import java.time.Duration
import java.time.Instant
import io.github.flexarr.ui.theme.Tokens

/**
 * A device asking to connect, for the main profile to let in from this phone: its name, where it's asking from, and
 * the code it shows to check against. One at a time; the next comes up once this one's answered or closed. Closing it
 * leaves the device waiting. As the iPhone's pairing prompt.
 */
@Composable
fun PairingPrompt(model: AppModel, client: FlexarrClient) {
    val device = model.pairing.current ?: return
    key(device.code) { PairingSheet(model, client, device) }
}

/** A TV, phone or tablet, as its [WaitingDevice.deviceType] or [io.github.flexarr.core.Device.deviceType] says; a TV when it doesn't. */
fun deviceIcon(deviceType: String?): ImageVector = when (deviceType) {
    "IPhone" -> Icons.Filled.PhoneIphone
    "AndroidPhone" -> Icons.Filled.PhoneAndroid
    "IPad", "AndroidTablet" -> Icons.Filled.TabletAndroid
    else -> Icons.Filled.Tv
}

/** Why allowing (or turning away) a device didn't work, in a sentence; [outside] the app, from a notification's button. */
fun pairingProblem(e: Exception, outside: Boolean = false): String = if (e is PinProblem) e.message.orEmpty() else when ((e as? FlexarrException.Http)?.status) {
    404 -> if (outside) "That code has run out. The device shows a new one; open Flexarr to allow it." else "It may have expired; the device shows a new code."
    403 -> "Switch to the main profile to allow it."
    else -> e.message ?: "Something went wrong."
}

/** Said while the main profile has no PIN yet, so allowing can't be unlocked from here. */
const val PIN_MISSING = "Choose an admin PIN in Flexarr in a browser first."

/** The admin PIN didn't unlock: wrong, too many tries (the server says how long to wait), or none set yet. */
class PinProblem(message: String) : Exception(message)

/**
 * Lets the device showing [code] in as the main profile: while managing the server is locked here, [pin] unlocks it
 * first. Throws [PinProblem] when the PIN doesn't, or is needed again (the unlock ran out since), and otherwise as
 * [io.github.flexarr.PairingPrompts.allow] does.
 */
suspend fun allowAsMainProfile(model: AppModel, client: FlexarrClient, code: String, pin: String) {
    if (!model.isAdmin) {
        try {
            model.unlock(pin)
        } catch (e: FlexarrException.Http) {
            throw PinProblem(if (e.status == 409) PIN_MISSING else e.message ?: "That PIN isn’t right.")
        }
    }
    try {
        model.pairing.allow(client, code)
    } catch (e: FlexarrException.Http) {
        // Unlocked when last asked, but its 15 minutes have run out since: the PIN again.
        if (e.status == 403 && model.isAdmin) {
            model.locked()
            if (model.canUnlock) throw PinProblem("Enter the admin PIN to allow it.")
        }
        throw e
    }
}

/**
 * The admin PIN, asked for on Allow while managing the server is locked here: 4 to 8 digits on the number pad,
 * hidden. Without a PIN yet, says to give it one in a browser.
 */
@Composable
fun MainPinField(
    model: AppModel,
    pin: String,
    onPin: (String) -> Unit,
    onDone: () -> Unit,
    modifier: Modifier = Modifier,
    fieldColor: Color = Flexarr.seat,
) {
    if (model.adminPinMissing) {
        Text(PIN_MISSING, fontSize = 15.sp, color = Flexarr.usher, modifier = modifier)
        return
    }
    Column(modifier, verticalArrangement = Arrangement.spacedBy(8.dp)) {
        BasicTextField(
            value = pin,
            onValueChange = { onPin(it.filter(Char::isDigit).take(8)) },
            singleLine = true,
            visualTransformation = PasswordVisualTransformation(),
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.NumberPassword, imeAction = ImeAction.Done),
            keyboardActions = KeyboardActions(onDone = { onDone() }),
            textStyle = TextStyle(fontSize = 20.sp, color = Flexarr.screen),
            cursorBrush = SolidColor(Flexarr.screen),
            decorationBox = { field ->
                Box(Modifier.fillMaxWidth().clip(RoundedCornerShape(Tokens.radiusControl)).background(fieldColor).padding(horizontal = 16.dp, vertical = 12.dp)) {
                    if (pin.isEmpty()) Text("Admin PIN", fontSize = 17.sp, color = Flexarr.usher)
                    field()
                }
            },
            modifier = Modifier.fillMaxWidth().semantics { contentDescription = "Admin PIN" },
        )
        Text("Forgotten it? Reset it from Flexarr in a browser.", fontSize = 13.sp, color = Flexarr.usher)
    }
}

/** Whether Allow can be pressed: unlocked, or with a PIN long enough to try. */
fun mayAllow(model: AppModel, pin: String): Boolean = model.isAdmin || !model.adminPinMissing && pin.length >= 4

private sealed interface Answer {
    data object Asking : Answer
    data object Sending : Answer
    data object Connected : Answer
    /** [gone]: no longer waiting, so there's nothing left to answer. */
    data class Failed(val message: String, val gone: Boolean) : Answer
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun PairingSheet(model: AppModel, client: FlexarrClient, device: WaitingDevice) {
    val scope = rememberCoroutineScope()
    var answer by remember { mutableStateOf<Answer>(Answer.Asking) }
    var pin by remember { mutableStateOf("") }
    // Locked here: Allow asks for the admin PIN, then allows with it (as on iPhone).
    var asksPin by remember { mutableStateOf(false) }

    // Allowed from the notification while the app came up (or anywhere else): checked with the server as it shows, so
    // a device that's already in doesn't ask again.
    LaunchedEffect(device.code) {
        delay(1_500)
        if (answer != Answer.Asking) return@LaunchedEffect
        val still = runCatching { client.waitingDevices().any { it.code == device.code } }.getOrNull() ?: return@LaunchedEffect
        if (!still && answer == Answer.Asking) model.pairing.finished(device.code)
    }
    // Its code runs out (the device shows a new one, which asks again).
    LaunchedEffect(device.expiresAt) {
        delay(Duration.between(Instant.now(), device.expiresAt).toMillis().coerceAtLeast(0))
        model.pairing.ended(device.code)
    }

    fun failed(e: Exception): Answer.Failed {
        val gone = e is FlexarrException.Http && e.status == 404
        return Answer.Failed(pairingProblem(e), gone)
    }

    fun allow() {
        if (answer == Answer.Sending || answer == Answer.Connected) return
        if (!model.isAdmin && !asksPin) {
            asksPin = true
            return
        }
        if (!mayAllow(model, pin)) return
        answer = Answer.Sending
        scope.launch {
            try {
                allowAsMainProfile(model, client, device.code, pin)
                answer = Answer.Connected
                delay(1_800)
                model.pairing.finished(device.code)
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                if (e is PinProblem) pin = ""
                answer = failed(e)
            }
        }
    }

    fun decline() {
        if (answer == Answer.Sending || answer == Answer.Connected) return
        answer = Answer.Sending
        scope.launch {
            try {
                model.pairing.decline(client, device.code)
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                answer = failed(e)
            }
        }
    }

    fun close() {
        val now = answer
        when {
            now == Answer.Connected || (now is Answer.Failed && now.gone) -> model.pairing.finished(device.code)
            else -> model.pairing.dismiss(device.code)
        }
    }

    ModalBottomSheet(onDismissRequest = ::close, sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true), containerColor = Flexarr.room) {
        Column(
            // Scrolls rather than squeezing the buttons when the PIN's keyboard takes the space.
            Modifier.fillMaxWidth().verticalScroll(rememberScrollState()).imePadding().navigationBarsPadding().padding(start = 24.dp, end = 24.dp, bottom = 24.dp),
            verticalArrangement = Arrangement.spacedBy(18.dp),
        ) {
            Row(horizontalArrangement = Arrangement.spacedBy(14.dp), verticalAlignment = Alignment.CenterVertically) {
                Icon(deviceIcon(device.deviceType), contentDescription = null, tint = Flexarr.silver, modifier = Modifier.size(34.dp))
                Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
                    Text("Connect “${device.deviceName}”?", fontSize = 22.sp, fontWeight = FontWeight.Bold, color = Flexarr.screen)
                    Text(device.summary, fontSize = 15.sp, color = Flexarr.usher)
                }
            }
            Column(verticalArrangement = Arrangement.spacedBy(2.dp)) {
                Text("Check its screen shows", fontSize = 17.sp, color = Flexarr.screen)
                Text(
                    device.code,
                    fontSize = 44.sp,
                    fontWeight = FontWeight.Black,
                    letterSpacing = 2.sp,
                    color = Flexarr.screen,
                    // Read a character at a time, as it's checked against the screen.
                    modifier = Modifier.semantics { contentDescription = device.code.toList().joinToString(" ") },
                )
            }
            when (val now = answer) {
                Answer.Connected -> Row(
                    Modifier.semantics(mergeDescendants = true) { liveRegion = LiveRegionMode.Polite },
                    horizontalArrangement = Arrangement.spacedBy(10.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Icon(Icons.Filled.CheckCircle, contentDescription = null, tint = Flexarr.screen, modifier = Modifier.size(22.dp))
                    Text("Connected. It’ll be ready in a moment.", fontSize = 17.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen)
                }
                is Answer.Failed -> Text(
                    now.message,
                    fontSize = 15.sp,
                    color = Flexarr.danger,
                    modifier = Modifier.semantics { liveRegion = LiveRegionMode.Polite },
                )
                else -> Unit
            }
            when {
                answer == Answer.Connected -> Unit
                answer is Answer.Failed && (answer as Answer.Failed).gone -> ActionButton("OK", ::close, Modifier.fillMaxWidth())
                else -> Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                    val sending = answer == Answer.Sending
                    // Locked here: allowing takes the admin PIN.
                    if (asksPin && !model.isAdmin) MainPinField(model, pin, onPin = { pin = it }, onDone = ::allow, Modifier.padding(bottom = 6.dp))
                    if (sending) {
                        Row(Modifier.fillMaxWidth().padding(vertical = 13.dp), horizontalArrangement = Arrangement.Center) {
                            CircularProgressIndicator(Modifier.size(22.dp), strokeWidth = 2.dp, color = Flexarr.screen)
                        }
                    } else {
                        ActionButton("Allow", ::allow, Modifier.fillMaxWidth(), prominent = true, enabled = !asksPin || mayAllow(model, pin))
                    }
                    ActionButton("Don’t allow", ::decline, Modifier.fillMaxWidth(), enabled = !sending)
                }
            }
        }
    }
}
