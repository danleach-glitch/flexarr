package io.github.flexarr.ui.connect

import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import io.github.flexarr.AppModel
import io.github.flexarr.core.GuestTvCode
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.withContext

/** The code this device shows for a family invite, and what's gone wrong getting one. */
class GuestCodeState {
    var code by mutableStateOf<GuestTvCode?>(null)
    var problem by mutableStateOf<String?>(null)
}

/**
 * "Invited by someone?": this device shows a code to read (or send) to whoever invited it, who types it into their
 * Flexarr. The code meets their server's invite at Flexarr's relay, sealed so only this device can open it, and the
 * device then joins as with a link (the tunnel, then a sign-in of its own). A code lasts ten minutes; a new one
 * replaces it. A new [attempt] starts again with a new code. Shared by the phone's and the TV's code screens.
 */
@Composable
fun rememberGuestCode(model: AppModel, attempt: Int): GuestCodeState {
    val state = remember { GuestCodeState() }
    LaunchedEffect(attempt) {
        state.code = null
        state.problem = null
        while (true) {
            val tv = withContext(Dispatchers.Default) { GuestTvCode.create() }
            try {
                tv.register()
            } catch (e: CancellationException) {
                throw e
            } catch (_: GuestTvCode.Failure.CodeTaken) {
                continue
            } catch (e: Exception) {
                state.problem = "Can’t reach Flexarr. Check this device’s internet."
                delay(10_000)
                continue
            }
            state.code = tv
            state.problem = null
            // Until it runs out (then a new one), or the invite arrives.
            while (true) {
                delay(3_000)
                val answer = try {
                    tv.poll()
                } catch (e: CancellationException) {
                    throw e
                } catch (_: GuestTvCode.Failure.BadInvite) {
                    state.problem = "That invite couldn’t be opened. Ask for another."
                    break
                } catch (_: Exception) {
                    // The network dropped for a moment: the code stands, so keep asking.
                    continue
                }
                when (answer) {
                    GuestTvCode.Answer.Waiting -> continue
                    GuestTvCode.Answer.Expired -> break
                    is GuestTvCode.Answer.Invite -> {
                        model.offerInvite(answer.invite)
                        model.confirmInvite()
                        return@LaunchedEffect
                    }
                }
            }
        }
    }
    return state
}
