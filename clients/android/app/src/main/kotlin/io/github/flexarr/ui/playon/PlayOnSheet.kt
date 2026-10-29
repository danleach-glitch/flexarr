package io.github.flexarr.ui.playon

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.github.flexarr.core.Device
import io.github.flexarr.core.FlexarrClient
import io.github.flexarr.core.ItemSummary
import io.github.flexarr.core.devices
import io.github.flexarr.core.displayTitle
import io.github.flexarr.core.playOn
import io.github.flexarr.ui.components.pressable
import io.github.flexarr.ui.pairing.deviceIcon
import io.github.flexarr.ui.theme.Flexarr
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.launch

/**
 * The other screens signed in to the server, to play [item] on: those with Flexarr open first, each saying who's
 * watching there and what it's playing. Choosing one sends the title there (a show plays the episode that screen's
 * profile is up to), and says whether it took it.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun PlayOnSheet(client: FlexarrClient, item: ItemSummary, onClose: () -> Unit) {
    val scope = rememberCoroutineScope()
    var devices by remember { mutableStateOf<List<Device>?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    var sending by remember { mutableStateOf<String?>(null) }
    var outcome by remember { mutableStateOf<String?>(null) }

    LaunchedEffect(item.id) {
        try {
            devices = client.devices().filterNot { it.isThisOne }.sortedWith(compareByDescending<Device> { it.online }.thenByDescending { it.lastUsedAt })
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            error = e.message
        }
    }

    fun send(device: Device) {
        if (sending != null) return
        sending = device.id
        outcome = null
        scope.launch {
            outcome = try {
                val result = client.playOn(device.id, item.id)
                val what = result.item?.displayTitle ?: item.displayTitle
                if (result.delivered) {
                    "Playing $what on ${result.deviceName}."
                } else {
                    "Flexarr isn’t open on ${result.deviceName}. $what plays there if it’s opened in the next two minutes."
                }
            } catch (e: Exception) {
                e.message
            } finally {
                sending = null
            }
        }
    }

    ModalBottomSheet(onDismissRequest = onClose, sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true), containerColor = Flexarr.room) {
        Row(Modifier.fillMaxWidth().padding(start = 20.dp, end = 8.dp), verticalAlignment = Alignment.CenterVertically) {
            Text("Play on", fontSize = 17.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen, modifier = Modifier.weight(1f))
            TextButton(onClick = onClose) { Text("Done", color = Flexarr.screen, fontWeight = FontWeight.SemiBold) }
        }
        LazyColumn(Modifier.navigationBarsPadding(), contentPadding = PaddingValues(horizontal = 8.dp, vertical = 8.dp)) {
            outcome?.let { message ->
                item { Text(message, fontSize = 14.sp, color = Flexarr.screen, modifier = Modifier.padding(horizontal = 14.dp, vertical = 8.dp)) }
            }
            val listed = devices
            when {
                error != null -> item { Note(error!!, Flexarr.danger) }
                listed == null -> item { Row(Modifier.padding(16.dp)) { CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp, color = Flexarr.usher) } }
                listed.isEmpty() -> item { Note("No other screens are signed in. Connect Flexarr on a TV or another phone, then it’s listed here.") }
                else -> items(listed, key = { it.id }) { device -> DeviceRow(device, sending == device.id) { send(device) } }
            }
        }
    }
}

@Composable
private fun Note(text: String, color: androidx.compose.ui.graphics.Color = Flexarr.usher) {
    Text(text, fontSize = 13.sp, color = color, modifier = Modifier.padding(horizontal = 14.dp, vertical = 16.dp))
}

@Composable
private fun DeviceRow(device: Device, sending: Boolean, onClick: () -> Unit) {
    Row(
        Modifier.fillMaxWidth().pressable(onClick).padding(horizontal = 14.dp, vertical = 10.dp),
        horizontalArrangement = Arrangement.spacedBy(14.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Icon(deviceIcon(device.deviceType), contentDescription = null, tint = if (device.online) Flexarr.silver else Flexarr.usher, modifier = Modifier.size(26.dp))
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(3.dp)) {
            Text(device.name, fontSize = 16.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen, maxLines = 1, overflow = TextOverflow.Ellipsis)
            val detail = when {
                !device.online -> "Flexarr isn’t open"
                device.nowPlaying != null -> (if (device.nowPlaying!!.paused) "Paused: " else "Playing ") + device.nowPlaying!!.item.displayTitle
                else -> device.profileName?.let { "Open, as $it" } ?: "Open"
            }
            Text(detail, fontSize = 13.sp, color = Flexarr.usher, maxLines = 1, overflow = TextOverflow.Ellipsis)
        }
        if (sending) CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp, color = Flexarr.screen)
    }
}
