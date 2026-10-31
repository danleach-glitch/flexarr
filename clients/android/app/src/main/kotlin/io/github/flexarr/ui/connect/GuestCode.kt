package io.github.flexarr.ui.connect

import android.os.SystemClock
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.runtime.snapshotFlow
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LocalLifecycleOwner
import io.github.flexarr.AppModel
import io.github.flexarr.core.GuestTvCode
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.withContext

/** The code this device shows for a family invite, and what's gone wrong getting one. */
class GuestCodeState {
    var code by mutableStateOf<GuestTvCode?>(null)
    var problem by mutableStateOf<String?>(null)

    /** Codes ran out [MAX_CODES] times with nothing arriving: no more until "Show a new code" (a new attempt). */
    var stopped by mutableStateOf(false)
}

/** Codes that run out with nothing arriving before the screen stops making new ones (half an hour). */
private const val MAX_CODES = 3

/** How long after a code is shown it's asked about every [FAST_POLL_MS] (most invites come quickly), then every [SLOW_POLL_MS]. */
private const val FAST_FOR_MS = 120_000L
private const val FAST_POLL_MS = 3_000L
private const val SLOW_POLL_MS = 10_000L

/**
 * "Join someone’s Flexarr": this device shows a code to read (or send) to whoever invited it, who types it into their
 * Flexarr. The code meets their server's invite at Flexarr's relay, sealed so only this device can open it, and the
 * screen asks "Join Dan’s Flexarr?" ([AppModel.inviteFromCode]); joining is then as with a link (the tunnel, then a
 * sign-in of its own). A code lasts ten minutes; a new one replaces it, and one follows an invite let go without
 * joining. The relay is asked only while the app is in front: every 3 seconds for the first 2 minutes, then every 10;
 * after [MAX_CODES] codes with nothing arriving it stops ([GuestCodeState.stopped]). A new [attempt] starts again with
 * a new code. Shared by the phone's and the TV's code screens.
 */
@Composable
fun rememberGuestCode(model: AppModel, attempt: Int): GuestCodeState {
    val state = remember { GuestCodeState() }
    val lifecycle = LocalLifecycleOwner.current.lifecycle
    LaunchedEffect(attempt) {
        state.code = null
        state.problem = null
        state.stopped = false
        // Waits while the app is in the background: nobody's there to read the code.
        suspend fun inFront() = lifecycle.currentStateFlow.first { it.isAtLeast(Lifecycle.State.STARTED) }
        var shownAt = SystemClock.elapsedRealtime()
        var ranOut = 0
        while (true) {
            if (ranOut >= MAX_CODES) {
                state.code = null
                state.stopped = true
                return@LaunchedEffect
            }
            inFront()
            val tv = withContext(Dispatchers.Default) { GuestTvCode.create() }
            try {
                tv.register(relay = model.pairRelay)
            } catch (e: CancellationException) {
                throw e
            } catch (_: GuestTvCode.Failure.CodeTaken) {
                continue
            } catch (e: Exception) {
                state.problem = "Can’t get a code just now."
                delay(10_000)
                continue
            }
            state.code = tv
            state.problem = null
            // Until it runs out (then a new one), or the invite arrives.
            while (true) {
                delay(if (SystemClock.elapsedRealtime() - shownAt < FAST_FOR_MS) FAST_POLL_MS else SLOW_POLL_MS)
                inFront()
                val answer = try {
                    tv.poll(relay = model.pairRelay)
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
                    GuestTvCode.Answer.Expired -> {
                        ranOut++
                        break
                    }
                    is GuestTvCode.Answer.Invite -> {
                        // Asked about on the code screen ("Join Dan’s Flexarr?"), not joined straight away: anyone who
                        // heard the code could have sent theirs.
                        model.offerInvite(answer.invite, fromCode = true)
                        snapshotFlow { model.offeredInvite }.first { it == null }
                        // Joined; or let go without joining ("Not now", "New code"), when this code is used up: a new
                        // one, asked about quickly again, as someone's there.
                        if (model.phase is AppModel.Phase.SignedIn) return@LaunchedEffect
                        ranOut = 0
                        shownAt = SystemClock.elapsedRealtime()
                        break
                    }
                }
            }
        }
    }
    return state
}
