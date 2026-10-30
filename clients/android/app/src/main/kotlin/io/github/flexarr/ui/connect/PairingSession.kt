package io.github.flexarr.ui.connect

import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import io.github.flexarr.AppModel
import io.github.flexarr.core.FlexarrClient
import io.github.flexarr.data.DiscoveredServer
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.delay

/**
 * Connecting by code: the server hands out a short code, someone approves it (a phone signed in as the main profile is
 * asked; on a computer, Settings › Access › Connect a device), and this device gets a sign-in of its own. Codes that run
 * out are replaced. One per attempt: Try again after "Don't allow" starts a new one. The phone's and the TV's connect
 * screens show it.
 */
class PairingSession {
    /** The code to show ("ABC-DEF"); null until the server gives one, and after it's turned away. */
    var code: String? by mutableStateOf(null)
        private set

    /** The code on screen replaced one that ran out, so anyone part-way through typing the old one notices. */
    var renewed by mutableStateOf(false)
        private set

    /** The main profile chose "Don't allow": the screen says so, and Try again starts over with a code they're asked about. */
    var declined by mutableStateOf(false)
        private set

    /** A new server, with nobody to approve the code yet: setting it up in a browser comes first. */
    var needsSetup by mutableStateOf(false)
        private set

    /** Setting it up asks for the setup code from the server's log. */
    var setupNeedsCode by mutableStateOf(false)
        private set

    var error: String? by mutableStateOf(null)
        private set

    /** Gets a code, then asks until it's approved (and signs in) or turned away; runs until then or until cancelled. */
    suspend fun run(model: AppModel, server: DiscoveredServer) {
        val client = FlexarrClient(FlexarrClient.normalizeAddress(server.address)!!)
        while (true) {
            try {
                val started = client.startPairing(model.deviceName)
                val setup = client.setupStatus()
                needsSetup = setup?.open ?: false
                setupNeedsCode = setup?.codeRequired ?: false
                renewed = code != null && code != started.code
                code = started.code
                error = null
                var since = 0
                // Asked straight away: a new server lets its first device in without anyone approving it.
                while (true) {
                    // A failed check (the device asleep, or the Wi-Fi blinking) asks again about the same code rather
                    // than starting another: the one on screen may already be approved, and the server keeps only three waiting.
                    val status = try {
                        client.pairingStatus(started.id).also { error = null }
                    } catch (e: CancellationException) {
                        throw e
                    } catch (e: Exception) {
                        error = e.message
                        delay(2_000)
                        continue
                    }
                    val key = status.apiKey
                    if (status.status == "approved" && key != null) {
                        model.connect(server.address, key)
                        return
                    }
                    if (status.status == "expired") {
                        // Turned away: say so, rather than quietly showing another code nobody's asked about.
                        if (status.declined) {
                            declined = true
                            code = null
                            renewed = false
                            return
                        }
                        break // ran out: a fresh code next time round
                    }
                    delay(2_000)
                    if (renewed && ++since > 5) renewed = false
                    // Set up in the browser meanwhile: switch to the usual instructions.
                    if (needsSetup) needsSetup = client.setupStatus()?.open ?: false
                }
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                error = e.message
                delay(5_000)
            }
        }
    }

    /** Where a phone that's signed in approves the code: the QR code on a TV, "Approve in the browser" on a phone. */
    fun link(server: DiscoveredServer): String? = code?.let { "${server.address.trimEnd('/')}/link?code=$it" }
}
