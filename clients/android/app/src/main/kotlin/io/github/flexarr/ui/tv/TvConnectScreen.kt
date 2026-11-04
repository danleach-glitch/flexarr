package io.github.flexarr.ui.tv

import io.github.flexarr.core.i18n.Words
import io.github.flexarr.core.i18n.t
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LocalLifecycleOwner
import kotlinx.coroutines.flow.first
import android.graphics.Bitmap
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowForward
import androidx.compose.material.icons.filled.Dns
import androidx.compose.material.icons.filled.Warning
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.getValue
import androidx.compose.runtime.setValue
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.FilterQuality
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.semantics.LiveRegionMode
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.heading
import androidx.compose.ui.semantics.liveRegion
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.withStyle
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.tv.material3.Icon
import androidx.tv.material3.Text
import com.google.zxing.BarcodeFormat
import com.google.zxing.EncodeHintType
import com.google.zxing.qrcode.QRCodeWriter
import com.google.zxing.qrcode.decoder.ErrorCorrectionLevel
import io.github.flexarr.AppModel
import io.github.flexarr.BuildConfig
import io.github.flexarr.core.FlexarrClient
import io.github.flexarr.core.GuestTvCode
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import io.github.flexarr.data.DiscoveredServer
import io.github.flexarr.data.ServerBrowser
import io.github.flexarr.ui.components.BrandMark
import io.github.flexarr.ui.connect.PairingSession
import io.github.flexarr.ui.connect.rememberGuestCode
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.RoomBackground
import io.github.flexarr.ui.theme.marquee
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import io.github.flexarr.ui.theme.Tokens
import androidx.compose.foundation.border

private sealed interface Step {
    data object Choose : Step
    data class Pair(val server: DiscoveredServer) : Step
    data object Manual : Step
    /** Someone else's Flexarr, from another home: a code to read to them. */
    data object Guest : Step
}

/**
 * First run, as the Apple TV's ConnectView. Servers on the network are listed (they announce themselves), so there's
 * nothing to type: pick one and the TV shows a code and QR code to approve from a phone or computer signed in to
 * Flexarr. Typing an address is still there for servers that can't be found (over Tailscale, say), and leads to the
 * same code.
 */
@Composable
fun TvConnectScreen(model: AppModel) {
    val context = LocalContext.current
    val browser = remember { ServerBrowser(context) }
    DisposableEffect(browser) {
        browser.start()
        onDispose { browser.stop() }
    }
    // Signed out on the server: show a code for the same server straight away.
    var step by remember {
        val last = model.store.lastAddress
        mutableStateOf(if (model.signedOutNotice != null && !model.signedOutAsGuest && last != null) Step.Pair(server(last)) else Step.Choose)
    }
    BackHandler(enabled = step != Step.Choose) { step = Step.Choose }

    RoomBackground(ambient = Flexarr.silver, intensity = 0.22f, reach = 900.dp) {
        Row(
            Modifier.fillMaxSize().padding(horizontal = 60.dp),
            horizontalArrangement = Arrangement.spacedBy(60.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(14.dp)) {
                Row(horizontalArrangement = Arrangement.spacedBy(14.dp), verticalAlignment = Alignment.CenterVertically) {
                    BrandMark(66.dp)
                    Text("flexarr", style = marquee(75))
                }
                Text(t("Your entertainment, on every screen."), fontSize = 18.sp, fontWeight = FontWeight.Medium, color = Flexarr.usher)
                model.signedOutNotice?.let {
                    Text(it, fontSize = 16.sp, fontWeight = FontWeight.SemiBold, modifier = Modifier.padding(top = 10.dp))
                }
            }
            Box(Modifier.width(410.dp)) {
                when (val current = step) {
                    Step.Choose -> ChooseServer(browser, onPick = { step = Step.Pair(it) }, onManual = { step = Step.Manual }, onGuest = { step = Step.Guest })
                    is Step.Pair -> PairWithServer(model, current.server, onBack = { step = Step.Choose })
                    Step.Manual -> ManualConnect(model, onPair = { step = Step.Pair(it) }, onBack = { step = Step.Choose })
                    Step.Guest -> GuestCode(model, onBack = { step = Step.Choose })
                }
            }
        }
    }
}

private fun server(address: String): DiscoveredServer {
    val url = FlexarrClient.normalizeAddress(address)
    return DiscoveredServer(url?.host ?: "Flexarr", url?.toString() ?: address)
}

@Composable
private fun ChooseServer(browser: ServerBrowser, onPick: (DiscoveredServer) -> Unit, onManual: () -> Unit, onGuest: () -> Unit) {
    // Nothing has turned up for a while: say what to check.
    var lookingAWhile by remember { mutableStateOf(false) }
    LaunchedEffect(Unit) {
        delay(10_000)
        lookingAWhile = true
    }
    val first = remember { FocusRequester() }
    val manual = remember { FocusRequester() }
    val guest = remember { FocusRequester() }
    // The first server found takes focus, so one click of the remote picks it; until then (and in a home with no
    // server, a guest's), joining someone's Flexarr has it.
    val firstServer = browser.servers.firstOrNull()?.address
    LaunchedEffect(firstServer) { runCatching { if (firstServer != null) first.requestFocus() else if (BuildConfig.GUEST_INVITES) guest.requestFocus() else manual.requestFocus() } }
    Column(verticalArrangement = Arrangement.spacedBy(15.dp)) {
        Text(t("Servers on your network"), fontSize = 15.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.usher)
        if (browser.servers.isEmpty()) {
            Row(Modifier.padding(vertical = 10.dp), horizontalArrangement = Arrangement.spacedBy(10.dp), verticalAlignment = Alignment.CenterVertically) {
                CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp, color = Flexarr.usher)
                Text(t("Looking for Flexarr…"), fontSize = 14.sp, color = Flexarr.usher)
            }
            if (lookingAWhile) {
                Text(t("Can’t see your server? Check this TV is on the same network as it, or enter an address."), fontSize = 14.sp, fontWeight = FontWeight.Medium)
            }
        } else {
            browser.servers.forEachIndexed { index, server ->
                TvActionButton(
                    server.name,
                    onClick = { onPick(server) },
                    icon = Icons.Filled.Dns,
                    prominent = true,
                    modifier = Modifier.fillMaxWidth().then(if (index == 0) Modifier.focusRequester(first) else Modifier),
                )
            }
        }
        // With no server found (a guest's home), joining someone's comes first, as on the Apple TV.
        val joinFirst = BuildConfig.GUEST_INVITES && browser.servers.isEmpty()
        val join = @Composable { modifier: Modifier ->
            if (BuildConfig.GUEST_INVITES) TvActionButton(t("Join someone’s Flexarr"), onClick = onGuest, modifier = modifier.focusRequester(guest))
        }
        if (joinFirst) join(Modifier.padding(top = 5.dp))
        TvActionButton(t("Enter an address instead"), onClick = onManual, modifier = Modifier.padding(top = if (joinFirst) 0.dp else 5.dp).focusRequester(manual))
        if (!joinFirst) join(Modifier)
    }
}

/**
 * A family invite for this TV: a code to read out to whoever invited it, who types it into their Flexarr. The code
 * meets their server's invite at Flexarr's relay, sealed so only this TV can open it; the TV asks "Join Dan’s Flexarr?"
 * and then joins as a phone does with a link (the tunnel, then a sign-in of its own). See [rememberGuestCode].
 */
@Composable
private fun GuestCode(model: AppModel, onBack: () -> Unit) {
    var attempt by remember { mutableIntStateOf(0) }
    val guest = rememberGuestCode(model, attempt)
    val code = guest.code
    val problem = guest.problem
    val back = remember { FocusRequester() }
    val retry = remember { FocusRequester() }
    val join = remember { FocusRequester() }
    val newCode = remember { FocusRequester() }
    val invite = model.offeredInvite?.takeIf { model.inviteFromCode }
    val joinProblem = model.joinProblem
    // On Join when an invite arrives (one click for whoever's holding the remote), on Try again (or New code, when
    // trying again can't work) after a problem.
    val asking = invite != null && joinProblem == null && !model.joining
    LaunchedEffect(asking, joinProblem != null, model.joinCanRetry, guest.stopped, code == null && problem != null) {
        runCatching {
            when {
                asking -> join.requestFocus()
                guest.stopped && invite == null -> newCode.requestFocus()
                invite != null && joinProblem != null -> if (model.joinCanRetry) retry.requestFocus() else newCode.requestFocus()
                invite == null && code == null && problem != null -> retry.requestFocus()
                else -> back.requestFocus()
            }
        }
    }
    BackHandler { if (!model.joining) { model.dismissInvite(); onBack() } }

    Column(verticalArrangement = Arrangement.spacedBy(15.dp)) {
        if (invite != null) {
            when {
                model.joining -> Row(horizontalArrangement = Arrangement.spacedBy(12.dp), verticalAlignment = Alignment.CenterVertically) {
                    CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp, color = Flexarr.usher)
                    Text(t("{kind, select, named {Joining {server}…} owner {Joining {owner}’s Flexarr…} other {Joining this Flexarr…}}", *invite.serverNaming.values), fontSize = 20.sp, fontWeight = FontWeight.Bold)
                }
                joinProblem != null -> {
                    ErrorNote(joinProblem)
                    // Try again only when it could work (the host couldn't be reached); otherwise a new code for a new invite.
                    Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                        if (model.joinCanRetry) TvActionButton(t("Try again"), onClick = model::confirmInvite, prominent = true, modifier = Modifier.focusRequester(retry))
                        TvActionButton(t("New code"), onClick = model::dismissInvite, prominent = !model.joinCanRetry,
                            modifier = if (model.joinCanRetry) Modifier else Modifier.focusRequester(newCode))
                    }
                }
                // Asked first: anyone who heard the code could have sent theirs.
                else -> {
                    // Said as it arrives: whoever's using TalkBack is looking at the code.
                    Text(t("{kind, select, named {Join {server}?} owner {Join {owner}’s Flexarr?} other {Join this Flexarr?}}", *invite.serverNaming.values), fontSize = 22.sp, fontWeight = FontWeight.Bold,
                        modifier = Modifier.semantics { liveRegion = LiveRegionMode.Polite; heading() })
                    Text(t("You’ll watch as {profile}.", "profile" to invite.profileName), fontSize = 16.sp, color = Flexarr.usher)
                    Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                        TvActionButton(t("Join"), onClick = model::confirmInvite, prominent = true, modifier = Modifier.focusRequester(join))
                        TvActionButton(t("Not now"), onClick = model::dismissInvite)
                    }
                }
            }
        } else if (code == null && problem != null && !guest.stopped) {
            // No code to be had (the relay can't be reached): just that, and a way to try again.
            ErrorNote(problem)
            TvActionButton(if (guest.badInvite) t("New code") else t("Try again"), onClick = { attempt++ }, prominent = true, modifier = Modifier.focusRequester(retry))
        } else {
            // Not once codes have stopped: there's none to read.
            if (!guest.stopped) Text(t("Read this code to the person who invited you"), fontSize = 20.sp, fontWeight = FontWeight.Bold)
            val shown = code
            if (shown != null) {
                Text(shown.display, fontSize = 56.sp, fontWeight = FontWeight.Black, letterSpacing = 4.sp, modifier = Modifier.semantics { contentDescription = shown.spoken })
                Row(horizontalArrangement = Arrangement.spacedBy(10.dp), verticalAlignment = Alignment.CenterVertically) {
                    CircularProgressIndicator(Modifier.size(16.dp), strokeWidth = 2.dp, color = Flexarr.usher)
                    Text(t("They type it into Settings › Remote access in Flexarr."), fontSize = 13.sp, color = Flexarr.usher)
                }
            } else if (guest.stopped) {
                TvActionButton(t("Show a new code"), onClick = { attempt++ }, prominent = true, modifier = Modifier.focusRequester(newCode))
            } else if (problem == null) {
                CircularProgressIndicator(Modifier.size(22.dp), strokeWidth = 2.dp, color = Flexarr.usher)
            }
            problem?.let { ErrorNote(it) }
        }
        if (!asking) {
            // Focusable while joining (focus has to rest somewhere), but does nothing until it's done.
            TvActionButton(t("Back"), onClick = { if (!model.joining) { model.dismissInvite(); onBack() } }, modifier = Modifier.focusRequester(back))
        }
    }
}

@Composable
private fun PairWithServer(model: AppModel, server: DiscoveredServer, onBack: () -> Unit) {
    var attempt by remember { mutableIntStateOf(0) }
    // Try again after "Don't allow" starts over with a code they're asked about.
    val session = remember(server, attempt) { PairingSession() }
    val lifecycle = LocalLifecycleOwner.current.lifecycle
    // Asks only while the app is in front: nobody's there to read the code otherwise.
    LaunchedEffect(session) { session.run(model, server) { lifecycle.currentStateFlow.first { it.isAtLeast(Lifecycle.State.STARTED) } } }
    val back = remember { FocusRequester() }
    val tryAgain = remember { FocusRequester() }
    LaunchedEffect(session.declined) { runCatching { if (session.declined) tryAgain.requestFocus() else back.requestFocus() } }

    Column(verticalArrangement = Arrangement.spacedBy(15.dp)) {
        val code = session.code
        if (session.declined) {
            Column(verticalArrangement = Arrangement.spacedBy(9.dp)) {
                Text(t("Not allowed"), fontSize = 20.sp, fontWeight = FontWeight.Bold)
                Text(t("The main profile turned this TV away. If that was a mistake, try again and allow it this time."), fontSize = 14.sp, color = Flexarr.usher)
            }
        } else if (code != null) {
            // The phone's steps, word for word: a phone signed in as the main profile is asked to allow it, and the web
            // app does it without one. A new server has nobody to allow it yet, so setting it up in a browser comes
            // first; the QR code only helps a phone that's signed in already, so it's small, underneath.
            Column(verticalArrangement = Arrangement.spacedBy(13.dp)) {
                Text(t("Connect this TV"), fontSize = 20.sp, fontWeight = FontWeight.Bold)
                if (session.needsSetup) {
                    Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                        NumberedStep(1) {
                            appendBolding(t("On your computer, open {address} in a web browser.", "address" to boldMark(0)), server.address)
                        }
                        NumberedStep(2) {
                            val button = t("Set up Flexarr")
                            if (session.setupNeedsCode) {
                                appendBolding(t("Type in the setup code from the server’s log, then click {button}.", "button" to boldMark(0)), button)
                            } else {
                                appendBolding(t("Click {button}.", "button" to boldMark(0)), button)
                            }
                        }
                        NumberedStep(3) {
                            appendBolding(
                                t("Go to {settings}, then {security}, then {connect}.", "settings" to boldMark(0), "security" to boldMark(1), "connect" to boldMark(2)),
                                t(Words.settings), t("Security"), t("Connect a device"),
                            )
                        }
                        NumberedStep(4) { append(t("Type in this code:")) }
                    }
                    Code(code, session.renewed)
                } else {
                    Text(
                        buildAnnotatedString {
                            appendBolding(t("On a phone signed in to Flexarr, tap {allow} when it asks. Check it shows this code:", "allow" to boldMark(0)), t("Allow"))
                        },
                        fontSize = 15.sp,
                    )
                    Code(code, session.renewed)
                    Text(
                        buildAnnotatedString {
                            // The page's own address works on servers before and after Connect a device moved to Security.
                            appendBolding(t("Or on a computer, open {address} and type in the code.", "address" to boldMark(0)), server.address.trimEnd('/') + "/settings/tv")
                        },
                        fontSize = 13.sp,
                        color = Flexarr.usher,
                    )
                }
                Row(horizontalArrangement = Arrangement.spacedBy(7.dp), verticalAlignment = Alignment.CenterVertically) {
                    CircularProgressIndicator(Modifier.size(14.dp), strokeWidth = 2.dp, color = Flexarr.usher)
                    Text(t("This screen moves on by itself once it’s allowed."), fontSize = 12.sp, color = Flexarr.usher)
                }
            }
            if (!session.needsSetup) {
                session.link(server)?.let { link ->
                    Row(horizontalArrangement = Arrangement.spacedBy(10.dp), verticalAlignment = Alignment.CenterVertically) {
                        QrCode(link, Modifier.size(70.dp))
                        Text(t("Or scan with a phone that’s signed in to Flexarr."), fontSize = 12.sp, color = Flexarr.usher)
                    }
                }
            }
        } else if (session.error == null) {
            CircularProgressIndicator(color = Flexarr.screen)
        }
        session.error?.let { ErrorNote(it) }
        Row(horizontalArrangement = Arrangement.spacedBy(15.dp)) {
            if (session.declined) TvActionButton(t("Try again"), onClick = { attempt++ }, prominent = true, modifier = Modifier.focusRequester(tryAgain))
            TvActionButton(t("Back"), onClick = onBack, modifier = Modifier.focusRequester(back))
        }
    }
}

private fun androidx.compose.ui.text.AnnotatedString.Builder.bold(text: String) = withStyle(SpanStyle(fontWeight = FontWeight.Bold)) { append(text) }

/** A stand-in for the [index]th bold value, passed to t() so the translated sentence says where it goes. */
private fun boldMark(index: Int) = "\u0001$index\u0001"

/** Appends [sentence] (through t(), its values given as [boldMark]s), with [values] in bold where the marks are. */
private fun androidx.compose.ui.text.AnnotatedString.Builder.appendBolding(sentence: String, vararg values: String) {
    sentence.split('\u0001').forEachIndexed { i, part ->
        if (i % 2 == 0) append(part) else bold(values.getOrElse(part.toIntOrNull() ?: -1) { "" })
    }
}

@Composable
private fun Code(code: String, renewed: Boolean) {
    Row(horizontalArrangement = Arrangement.spacedBy(10.dp), verticalAlignment = Alignment.CenterVertically) {
        Text(code, fontSize = 44.sp, fontWeight = FontWeight.Black, letterSpacing = 3.sp)
        if (renewed) Text(t("A new code"), fontSize = 15.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.silver)
    }
}

/** One step of the connect instructions: a number in a circle, then what to do. */
@Composable
private fun NumberedStep(number: Int, text: androidx.compose.ui.text.AnnotatedString.Builder.() -> Unit) {
    Row(horizontalArrangement = Arrangement.spacedBy(9.dp)) {
        Box(Modifier.size(21.dp).background(Flexarr.silver, CircleShape), contentAlignment = Alignment.Center) {
            Text("$number", fontSize = 12.sp, fontWeight = FontWeight.Bold, color = Flexarr.room)
        }
        Text(buildAnnotatedString(text), fontSize = 15.sp, modifier = Modifier.padding(top = 1.dp))
    }
}

/** A QR code, drawn crisp at any size on a white tile (here, and for Trakt's activation page). */
@Composable
internal fun QrCode(text: String, modifier: Modifier = Modifier) {
    val bitmap = remember(text) { qrBitmap(text) }
    Box(modifier.background(Color.White, RoundedCornerShape(Tokens.radiusPanel)).padding(9.dp)) {
        Image(bitmap, contentDescription = t("QR code"), filterQuality = FilterQuality.None, modifier = Modifier.fillMaxSize())
    }
}

private fun qrBitmap(text: String): ImageBitmap {
    val matrix = QRCodeWriter().encode(text, BarcodeFormat.QR_CODE, 0, 0, mapOf(EncodeHintType.ERROR_CORRECTION to ErrorCorrectionLevel.M, EncodeHintType.MARGIN to 0))
    val bitmap = Bitmap.createBitmap(matrix.width, matrix.height, Bitmap.Config.ARGB_8888)
    for (y in 0 until matrix.height) for (x in 0 until matrix.width) {
        bitmap.setPixel(x, y, if (matrix[x, y]) android.graphics.Color.BLACK else android.graphics.Color.WHITE)
    }
    return bitmap.asImageBitmap()
}

@Composable
private fun ErrorNote(message: String) {
    Row(
        Modifier.fillMaxWidth().background(Flexarr.seat, RoundedCornerShape(Tokens.radiusPanel))
            .border(1.dp, Flexarr.danger.copy(alpha = 0.5f), RoundedCornerShape(Tokens.radiusPanel)).padding(12.dp),
        horizontalArrangement = Arrangement.spacedBy(8.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Icon(Icons.Filled.Warning, contentDescription = null, tint = Flexarr.danger, modifier = Modifier.size(15.dp))
        Text(message, fontSize = 13.sp, fontWeight = FontWeight.Medium)
    }
}

/**
 * A server that can't be found (over Tailscale, say): type its address and connect with a code like any other. The
 * API key is only for connecting from outside the home network and tailnet, where codes aren't offered.
 */
@Composable
private fun ManualConnect(model: AppModel, onPair: (DiscoveredServer) -> Unit, onBack: () -> Unit) {
    val scope = rememberCoroutineScope()
    var address by remember { mutableStateOf(model.store.lastAddress.orEmpty()) }
    var apiKey by remember { mutableStateOf("") }
    var usesKey by remember { mutableStateOf(false) }
    var connecting by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }
    val addressFocus = remember { FocusRequester() }
    val keyFocus = remember { FocusRequester() }
    val connectFocus = remember { FocusRequester() }
    LaunchedEffect(Unit) { runCatching { if (address.isEmpty()) addressFocus.requestFocus() else connectFocus.requestFocus() } }
    LaunchedEffect(usesKey) { if (usesKey) runCatching { keyFocus.requestFocus() } }

    fun connect() {
        if (connecting) return
        if (!usesKey) {
            val url = FlexarrClient.normalizeAddress(address)
            if (url == null) {
                error = t("That doesn’t look like a server address. Try one like http://192.168.1.20:8383.")
                return
            }
            onPair(DiscoveredServer(url.host, url.toString()))
            return
        }
        connecting = true
        error = null
        scope.launch {
            try {
                model.connect(address, apiKey)
            } catch (e: Exception) {
                error = e.message
            } finally {
                connecting = false
            }
        }
    }

    Column(verticalArrangement = Arrangement.spacedBy(17.dp)) {
        Field(t("Server address")) {
            Input(
                address,
                { address = it },
                "http://192.168.1.20:8383",
                KeyboardOptions(keyboardType = KeyboardType.Uri, autoCorrectEnabled = false, imeAction = if (usesKey) ImeAction.Next else ImeAction.Go),
                KeyboardActions(onGo = { connect() }, onNext = { keyFocus.requestFocus() }),
                Modifier.focusRequester(addressFocus),
            )
        }
        if (usesKey) {
            Field(t("API key")) {
                Input(
                    apiKey,
                    { apiKey = it },
                    t("Paste or type your API key"),
                    KeyboardOptions(keyboardType = KeyboardType.Password, imeAction = ImeAction.Go),
                    KeyboardActions(onGo = { connect() }),
                    Modifier.focusRequester(keyFocus),
                    secret = true,
                )
            }
        } else {
            Text(t("You’ll get a code to approve, like a server found on your network."), fontSize = 12.sp, color = Flexarr.usher)
        }
        Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            TvActionButton(
                if (connecting) t("Connecting") else t("Connect"),
                onClick = ::connect,
                icon = Icons.AutoMirrored.Filled.ArrowForward,
                prominent = true,
                enabled = !connecting && address.isNotBlank(),
                modifier = Modifier.focusRequester(connectFocus),
            )
            TvActionButton(t("Back"), onClick = onBack)
        }
        error?.let { ErrorNote(it) }
        if (!usesKey) TvActionButton(t("Connecting from outside your home? Use an API key"), onClick = { usesKey = true })
    }
}

@Composable
private fun Field(title: String, content: @Composable () -> Unit) {
    Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
        Text(title, fontSize = 12.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.usher)
        content()
    }
}

/** A field of the connect form: the TV's shared text field, full width. */
@Composable
private fun Input(
    value: String,
    onValue: (String) -> Unit,
    placeholder: String,
    keyboard: KeyboardOptions,
    actions: KeyboardActions,
    modifier: Modifier,
    secret: Boolean = false,
) {
    TvTextField(value, onValue, placeholder, modifier.fillMaxWidth(), keyboardOptions = keyboard, keyboardActions = actions, secret = secret)
}
