package io.github.flexarr.ui.pairing

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.CheckCircle
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
import androidx.compose.ui.semantics.LiveRegionMode
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.liveRegion
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.github.flexarr.AppModel
import io.github.flexarr.core.FlexarrClient
import io.github.flexarr.core.FlexarrException
import io.github.flexarr.core.WaitingDevice
import io.github.flexarr.ui.components.ActionButton
import io.github.flexarr.ui.theme.Flexarr
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import java.time.Duration
import java.time.Instant

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

    // Its code runs out (the device shows a new one, which asks again).
    LaunchedEffect(device.expiresAt) {
        delay(Duration.between(Instant.now(), device.expiresAt).toMillis().coerceAtLeast(0))
        model.pairing.ended(device.code)
    }

    fun failed(e: Exception): Answer.Failed {
        val gone = e is FlexarrException.Http && e.status == 404
        return Answer.Failed(if (gone) "It may have expired; the device shows a new code." else e.message ?: "Something went wrong.", gone)
    }

    fun allow() {
        if (answer == Answer.Sending || answer == Answer.Connected) return
        answer = Answer.Sending
        scope.launch {
            try {
                model.pairing.allow(client, device.code)
                answer = Answer.Connected
                delay(1_800)
                model.pairing.finished(device.code)
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
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
            Modifier.fillMaxWidth().navigationBarsPadding().padding(start = 24.dp, end = 24.dp, bottom = 24.dp),
            verticalArrangement = Arrangement.spacedBy(18.dp),
        ) {
            Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
                Text("Connect “${device.deviceName}”?", fontSize = 22.sp, fontWeight = FontWeight.Bold, color = Flexarr.screen)
                Text(device.whereFrom, fontSize = 15.sp, color = Flexarr.usher)
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
                    if (sending) {
                        Row(Modifier.fillMaxWidth().padding(vertical = 13.dp), horizontalArrangement = Arrangement.Center) {
                            CircularProgressIndicator(Modifier.size(22.dp), strokeWidth = 2.dp, color = Flexarr.screen)
                        }
                    } else {
                        ActionButton("Allow", ::allow, Modifier.fillMaxWidth(), prominent = true)
                    }
                    ActionButton("Don’t allow", ::decline, Modifier.fillMaxWidth(), enabled = !sending)
                }
            }
        }
    }
}
