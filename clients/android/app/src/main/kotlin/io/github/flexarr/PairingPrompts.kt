package io.github.flexarr

import android.content.Context
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import io.github.flexarr.core.FlexarrClient
import io.github.flexarr.core.FlexarrException
import io.github.flexarr.core.WaitingDevice
import io.github.flexarr.core.allowDevice
import io.github.flexarr.core.declineDevice
import io.github.flexarr.core.updating
import io.github.flexarr.core.waitingDevices
import io.github.flexarr.logging.FlexarrLog
import io.github.flexarr.notifications.FlexarrMessagingService
import kotlinx.coroutines.CancellationException
import java.time.Instant

private val flexarrLog = FlexarrLog("pairing")

/**
 * Devices showing a code and asking to connect, for the main profile to allow from this phone rather than on a computer.
 * They arrive on the command stream ([DeviceCommands]) and, when the app comes to the front, Settings' list is opened or a
 * notification about one is opened, from the server's list. One is asked about at a time, the rest waiting their turn;
 * closing the prompt leaves the device waiting (it can still be let in from Settings or the web app) and doesn't ask
 * about it again. Settings lists them all from here too, so an answer in one place shows in the other.
 */
class PairingPrompts(private val context: Context) {
    /** Waiting to be let in, oldest first. */
    var waiting: List<WaitingDevice> by mutableStateOf(emptyList())
        private set

    /** Closed without an answer here: not asked about again, though still listed in Settings. */
    private var dismissed by mutableStateOf(emptySet<String>())

    /** Being allowed here, kept on screen through "Connected" though the server says at once it's done. */
    private val allowing = mutableSetOf<String>()

    /** The one to ask about now. */
    val current: WaitingDevice?
        get() = waiting.firstOrNull { it.code !in dismissed && (it.expiresAt.isAfter(Instant.now()) || it.code in allowing) }

    /** Being asked about here (or about to be), so a notification about it would only repeat the prompt. */
    fun isWaiting(code: String): Boolean = waiting.any { it.code == code } && code !in dismissed

    /** A device started waiting, or one still showing its code has a later [WaitingDevice.expiresAt]: updated in place. */
    fun arrived(device: WaitingDevice) {
        waiting = waiting.updating(device)
    }

    /** Let in (here or anywhere else), turned away, or run out: no longer asked about, and its notification goes. */
    fun ended(code: String) {
        FlexarrMessagingService.cancelPairing(context, code)
        if (code in allowing) return
        waiting = waiting.filterNot { it.code == code }
        dismissed -= code
    }

    /** Closed without an answer: the device keeps waiting. */
    fun dismiss(code: String) {
        dismissed += code
    }

    /** Lets it in. Throws as [allowDevice] does; it stays listed until [finished]. */
    suspend fun allow(client: FlexarrClient, code: String) {
        allowing += code
        try {
            client.allowDevice(code)
            flexarrLog.info("Allowed a device to connect")
        } catch (e: Exception) {
            allowing -= code
            throw e
        }
        FlexarrMessagingService.cancelPairing(context, code)
    }

    suspend fun decline(client: FlexarrClient, code: String) {
        client.declineDevice(code)
        ended(code)
    }

    /** What was said about [code] (connected, or gone) has been seen: it's taken off. */
    fun finished(code: String) {
        allowing -= code
        waiting = waiting.filterNot { it.code == code }
        dismissed -= code
    }

    /**
     * The server's list in place of what's here (ones that ended while the app was away go). Throws as
     * [waitingDevices] does: 403 when this profile isn't the main one.
     */
    suspend fun reload(client: FlexarrClient) {
        val list = client.waitingDevices()
        val kept = waiting.filter { it.code in allowing }
        waiting = kept + list.filter { it.code !in allowing }
        dismissed = dismissed.filterTo(mutableSetOf()) { code -> list.any { it.code == code } }
    }

    /** As [reload], quietly if it can't be had. */
    suspend fun refresh(client: FlexarrClient) {
        try {
            reload(client)
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            flexarrLog.info("Couldn't ask which devices are waiting: ${e.message}")
        }
    }

    enum class Opened { Asking, Gone, NotMainProfile }

    /** A notification about [code] was opened: asked about first, even if it was closed before. */
    suspend fun open(client: FlexarrClient, code: String): Opened {
        try {
            reload(client)
        } catch (e: FlexarrException.Http) {
            if (e.status == 403) return Opened.NotMainProfile
            flexarrLog.info("Couldn't ask which devices are waiting: ${e.message}")
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            flexarrLog.info("Couldn't ask which devices are waiting: ${e.message}")
        }
        val device = waiting.firstOrNull { it.code == code } ?: return Opened.Gone
        dismissed -= code
        waiting = listOf(device) + waiting.filterNot { it.code == code }
        return Opened.Asking
    }

    /** Signed out, or someone else watching: nothing to ask about. */
    fun clear() {
        waiting = emptyList()
        dismissed = emptySet()
        allowing.clear()
    }
}
