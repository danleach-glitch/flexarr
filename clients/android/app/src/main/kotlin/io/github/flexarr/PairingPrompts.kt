package io.github.flexarr

import android.content.Context
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import io.github.flexarr.core.FlexarrClient
import io.github.flexarr.core.WaitingDevice
import io.github.flexarr.core.allowDevice
import io.github.flexarr.core.declineDevice
import io.github.flexarr.core.waitingDevices
import io.github.flexarr.logging.FlexarrLog
import io.github.flexarr.notifications.FlexarrMessagingService
import java.time.Instant

private val flexarrLog = FlexarrLog("pairing")

/**
 * Devices showing a code and asking to connect, for the main profile to allow from this phone rather than on a computer.
 * They arrive on the command stream ([DeviceCommands]) and, when the app comes to the front or a notification about one
 * is opened, from the server's list. One is asked about at a time, the rest waiting their turn; closing the prompt
 * leaves the device waiting (it can still be let in from the web app) and doesn't ask about it again.
 */
class PairingPrompts(private val context: Context) {
    /** Waiting to be asked about, oldest first. */
    var waiting: List<WaitingDevice> by mutableStateOf(emptyList())
        private set

    /** Closed without an answer here: not asked about again. */
    private val dismissed = mutableSetOf<String>()

    /** The one being allowed here, kept on screen through "Connected" though the server says at once it's done. */
    private var allowing: String? = null

    /** The one to ask about now. */
    val current: WaitingDevice? get() = waiting.firstOrNull { it.expiresAt.isAfter(Instant.now()) || it.code == allowing }

    fun isWaiting(code: String): Boolean = waiting.any { it.code == code }

    fun arrived(device: WaitingDevice) {
        if (device.code in dismissed) return
        val index = waiting.indexOfFirst { it.code == device.code }
        waiting = if (index < 0) waiting + device else waiting.toMutableList().also { it[index] = device }
    }

    /** Let in (here or anywhere else), turned away, or run out: no longer asked about, and its notification goes. */
    fun ended(code: String) {
        FlexarrMessagingService.cancelPairing(context, code)
        if (code == allowing) return
        waiting = waiting.filterNot { it.code == code }
    }

    /** Closed without an answer: the device keeps waiting. */
    fun dismiss(code: String) {
        dismissed += code
        waiting = waiting.filterNot { it.code == code }
    }

    /** Lets it in. Throws as [allowDevice] does; the prompt stays until [finished]. */
    suspend fun allow(client: FlexarrClient, code: String) {
        allowing = code
        try {
            client.allowDevice(code)
            flexarrLog.info("Allowed a device to connect")
        } catch (e: Exception) {
            allowing = null
            throw e
        }
        FlexarrMessagingService.cancelPairing(context, code)
    }

    suspend fun decline(client: FlexarrClient, code: String) {
        client.declineDevice(code)
        ended(code)
    }

    /** The prompt for [code] has said what came of it and closed. */
    fun finished(code: String) {
        if (allowing == code) allowing = null
        waiting = waiting.filterNot { it.code == code }
    }

    /** The server's list in place of what's here (ones that ended while the app was away go). Quietly, if it can't be had. */
    suspend fun refresh(client: FlexarrClient) {
        val list = try {
            client.waitingDevices()
        } catch (e: Exception) {
            flexarrLog.info("Couldn't ask which devices are waiting: ${e.message}")
            return
        }
        val kept = waiting.filter { it.code == allowing }
        waiting = kept + list.filter { it.code !in dismissed && it.code != allowing }
    }

    /**
     * A notification about [code] was opened: asked about first, even if it was closed before. False when it's no
     * longer waiting.
     */
    suspend fun open(client: FlexarrClient, code: String): Boolean {
        dismissed -= code
        refresh(client)
        val device = waiting.firstOrNull { it.code == code } ?: return false
        waiting = listOf(device) + waiting.filterNot { it.code == code }
        return true
    }

    /** Signed out, or someone else watching: nothing to ask about. */
    fun clear() {
        waiting = emptyList()
        allowing = null
    }
}
