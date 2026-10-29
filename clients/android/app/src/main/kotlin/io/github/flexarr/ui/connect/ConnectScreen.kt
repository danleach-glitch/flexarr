package io.github.flexarr.ui.connect

import androidx.activity.compose.BackHandler
import androidx.compose.animation.AnimatedContent
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowForward
import androidx.compose.material.icons.automirrored.filled.KeyboardArrowRight
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.Dns
import androidx.compose.material.icons.filled.Schedule
import androidx.compose.material.icons.filled.Warning
import androidx.compose.material.icons.automirrored.filled.OpenInNew
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.material3.minimumInteractiveComponentSize
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalUriHandler
import androidx.compose.ui.semantics.LiveRegionMode
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.liveRegion
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.withStyle
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.github.flexarr.AppModel
import io.github.flexarr.core.FlexarrClient
import io.github.flexarr.data.DiscoveredServer
import io.github.flexarr.data.ServerBrowser
import io.github.flexarr.ui.components.ActionButton
import io.github.flexarr.ui.components.BrandMark
import io.github.flexarr.ui.components.pressable
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.RoomBackground
import io.github.flexarr.ui.theme.marquee
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

private sealed interface Step {
    data object Choose : Step
    data class Pair(val server: DiscoveredServer) : Step
    data object Manual : Step
}

internal const val INSTALL_URL = "https://flexarr.io/#install"

/**
 * First run. Servers on the network are listed (they announce themselves): pick one and approve the code it shows, in
 * a browser on any device signed in to Flexarr. Typing an address (and API key) is still there for servers that can't
 * be found, such as over Tailscale.
 */
@Composable
fun ConnectScreen(model: AppModel) {
    val context = LocalContext.current
    val browser = remember { ServerBrowser(context) }
    DisposableEffect(browser) {
        browser.start()
        onDispose { browser.stop() }
    }
    // Signed out on the server: show a code for the same server straight away.
    var step by remember {
        val last = model.store.lastAddress
        mutableStateOf(if (model.signedOutNotice != null && last != null) Step.Pair(server(last)) else Step.Choose)
    }
    // Back from a code or an address goes back to the servers, as the screen's own Back does, rather than out of the app.
    BackHandler(enabled = step != Step.Choose) { step = Step.Choose }

    RoomBackground(ambient = Flexarr.silver, intensity = 0.22f) {
        Column(
            Modifier.align(Alignment.TopCenter).widthIn(max = 520.dp).fillMaxWidth().verticalScroll(rememberScrollState())
                .safeDrawingPadding().padding(24.dp).padding(top = 40.dp),
            verticalArrangement = Arrangement.spacedBy(28.dp),
        ) {
            Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                Row(horizontalArrangement = Arrangement.spacedBy(12.dp), verticalAlignment = Alignment.CenterVertically) {
                    BrandMark(56.dp)
                    Text("flexarr", style = marquee(64))
                }
                Text("Your entertainment, on every screen.", fontSize = 20.sp, fontWeight = FontWeight.Medium, color = Flexarr.usher)
                model.signedOutNotice?.let {
                    Text(it, fontSize = 17.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen, modifier = Modifier.padding(top = 8.dp))
                }
            }
            AnimatedContent(step, label = "connect") { current ->
                when (current) {
                    Step.Choose -> ChooseServer(browser, onPick = { step = Step.Pair(it) }, onManual = { step = Step.Manual })
                    is Step.Pair -> PairWithServer(model, current.server, onBack = { step = Step.Choose })
                    Step.Manual -> ManualConnect(model, onPair = { step = Step.Pair(it) }, onBack = { step = Step.Choose })
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
private fun ChooseServer(browser: ServerBrowser, onPick: (DiscoveredServer) -> Unit, onManual: () -> Unit) {
    // Nothing has turned up for a while: say what to check.
    var lookingAWhile by remember { mutableStateOf(false) }
    LaunchedEffect(Unit) {
        delay(6_000)
        lookingAWhile = true
    }
    Column(verticalArrangement = Arrangement.spacedBy(14.dp)) {
        Text("Servers on your network", fontSize = 15.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.usher)
        if (browser.servers.isEmpty()) {
            Row(Modifier.padding(vertical = 8.dp), horizontalArrangement = Arrangement.spacedBy(12.dp), verticalAlignment = Alignment.CenterVertically) {
                CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp, color = Flexarr.usher)
                Text("Looking for Flexarr…", color = Flexarr.usher)
            }
            if (lookingAWhile) {
                Text(
                    "Can’t see your server? Check it’s running and this device is on the same Wi-Fi, or enter its address.",
                    fontSize = 15.sp,
                    fontWeight = FontWeight.Medium,
                    color = Flexarr.screen,
                )
            }
            NewToFlexarr(Modifier.padding(top = 8.dp))
        } else {
            browser.servers.forEach { server ->
                Row(
                    Modifier.fillMaxWidth().clip(RoundedCornerShape(14.dp)).background(Flexarr.seat).pressable({ onPick(server) }).padding(16.dp),
                    horizontalArrangement = Arrangement.spacedBy(12.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Icon(Icons.Filled.Dns, contentDescription = null, tint = Flexarr.silver)
                    Text(server.name, fontSize = 17.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen, modifier = Modifier.weight(1f))
                    Icon(Icons.AutoMirrored.Filled.KeyboardArrowRight, contentDescription = null, tint = Flexarr.usher)
                }
            }
        }
        Text("Enter an address instead", fontSize = 17.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.silver, modifier = Modifier.padding(top = 6.dp).pressable(onManual).minimumInteractiveComponentSize())
    }
}

/** Shown while nothing is found: the app is nothing without the server, and store downloads often don't know there is one. */
@Composable
private fun NewToFlexarr(modifier: Modifier = Modifier) {
    val uri = LocalUriHandler.current
    Column(
        modifier.fillMaxWidth().clip(RoundedCornerShape(18.dp)).background(Flexarr.seat)
            .border(1.dp, Flexarr.silver.copy(alpha = 0.22f), RoundedCornerShape(18.dp)).padding(18.dp),
        verticalArrangement = Arrangement.spacedBy(16.dp),
    ) {
        Row(horizontalArrangement = Arrangement.spacedBy(14.dp)) {
            Box(Modifier.size(44.dp).clip(RoundedCornerShape(12.dp)).background(Flexarr.silver.copy(alpha = 0.16f)), contentAlignment = Alignment.Center) {
                Icon(Icons.Filled.Dns, contentDescription = null, tint = Flexarr.silver)
            }
            Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
                Text("New to Flexarr?", fontSize = 17.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen)
                Text("You’ll need the Flexarr server on a computer at home: a Mac, a NAS or anything with Docker.", fontSize = 15.sp, color = Flexarr.usher)
            }
        }
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            Badge("Completely free", Icons.Filled.Check)
            Badge("Under a minute", Icons.Filled.Schedule)
        }
        ActionButton("Set up a server", onClick = { uri.openUri(INSTALL_URL) }, icon = Icons.AutoMirrored.Filled.OpenInNew, prominent = true, modifier = Modifier.fillMaxWidth())
    }
}

@Composable
private fun Badge(title: String, icon: androidx.compose.ui.graphics.vector.ImageVector) {
    Row(
        Modifier.clip(CircleShape).background(Flexarr.screen.copy(alpha = 0.1f)).padding(horizontal = 10.dp, vertical = 6.dp),
        horizontalArrangement = Arrangement.spacedBy(5.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Icon(icon, contentDescription = null, tint = Flexarr.screen, modifier = Modifier.size(14.dp))
        Text(title, fontSize = 13.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen)
    }
}

/**
 * Connecting by code: the server hands out a short code, someone signed in to Flexarr approves it (Settings › Access ›
 * Connect a device), and this device gets a sign-in of its own. Codes that run out are replaced.
 */
@Composable
private fun PairWithServer(model: AppModel, server: DiscoveredServer, onBack: () -> Unit) {
    val uri = LocalUriHandler.current
    var code by remember { mutableStateOf<String?>(null) }
    var renewed by remember { mutableStateOf(false) }
    var needsSetup by remember { mutableStateOf(false) }
    var setupNeedsCode by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }

    LaunchedEffect(server) {
        val client = FlexarrClient(FlexarrClient.normalizeAddress(server.address)!!)
        while (true) {
            try {
                val started = client.startPairing(model.deviceName)
                val setup = client.setupStatus()
                needsSetup = setup?.open ?: false
                setupNeedsCode = setup?.codeRequired ?: false
                // The code on screen replaced one that ran out, so anyone part-way through typing the old one notices.
                renewed = code != null && code != started.code
                code = started.code
                error = null
                var since = 0
                // Asked straight away: a new server lets its first device in without anyone approving it.
                while (true) {
                    // A failed check (the phone asleep, or the Wi-Fi blinking) asks again about the same code rather than
                    // starting another: the one on screen may already be approved, and the server keeps only three waiting.
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
                        return@LaunchedEffect
                    }
                    if (status.status == "expired") break // a fresh code next time round
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

    Column(verticalArrangement = Arrangement.spacedBy(22.dp)) {
        val shown = code
        if (shown != null) {
            Text("Connect this device", fontSize = 22.sp, fontWeight = FontWeight.Bold, color = Flexarr.screen)
            // Short steps for someone who's never done this. A new server has nobody to approve the code yet, so setting
            // it up comes first.
            Column(verticalArrangement = Arrangement.spacedBy(14.dp)) {
                NumberedStep(1) {
                    Text(buildAnnotatedString {
                        append("On your computer, open ")
                        withStyle(SpanStyle(fontWeight = FontWeight.Bold)) { append(server.address) }
                        append(" in a web browser.")
                    })
                }
                if (needsSetup) {
                    NumberedStep(2) {
                        Text(buildAnnotatedString {
                            if (setupNeedsCode) append("Type in the setup code from the server’s log, then click ")
                            else append("Click ")
                            withStyle(SpanStyle(fontWeight = FontWeight.Bold)) { append("Set up Flexarr") }
                            append(".")
                        })
                    }
                }
                NumberedStep(if (needsSetup) 3 else 2) {
                    Text(buildAnnotatedString {
                        append("Go to ")
                        withStyle(SpanStyle(fontWeight = FontWeight.Bold)) { append("Settings") }
                        append(", then ")
                        withStyle(SpanStyle(fontWeight = FontWeight.Bold)) { append("Access") }
                        append(", then ")
                        withStyle(SpanStyle(fontWeight = FontWeight.Bold)) { append("Connect a device") }
                        append(".")
                    })
                }
                NumberedStep(if (needsSetup) 4 else 3) { Text("Type in this code:") }
            }
            Row(horizontalArrangement = Arrangement.spacedBy(12.dp), verticalAlignment = Alignment.CenterVertically) {
                Text(shown, fontSize = 52.sp, fontWeight = FontWeight.Black, letterSpacing = 2.sp, color = Flexarr.screen)
                if (renewed) Text("New code", fontSize = 15.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.silver)
            }
            Row(horizontalArrangement = Arrangement.spacedBy(10.dp), verticalAlignment = Alignment.CenterVertically) {
                CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp, color = Flexarr.usher)
                Text("This screen moves on by itself once you have.", fontSize = 13.sp, color = Flexarr.usher)
            }
        } else if (error == null) {
            CircularProgressIndicator(color = Flexarr.screen)
        }
        error?.let { ErrorNote(it) }
        Row(horizontalArrangement = Arrangement.spacedBy(20.dp), verticalAlignment = Alignment.CenterVertically) {
            Text("Back", fontSize = 15.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.silver, modifier = Modifier.pressable(onBack).minimumInteractiveComponentSize())
            // Only works if the browser on this phone is signed in already, so it stays out of the way.
            if (shown != null && !needsSetup) {
                Text(
                    "Approve in the browser instead",
                    fontSize = 15.sp,
                    fontWeight = FontWeight.SemiBold,
                    color = Flexarr.silver,
                    modifier = Modifier.pressable({ uri.openUri("${server.address.trimEnd('/')}/link?code=$shown") }).minimumInteractiveComponentSize(),
                )
            }
        }
    }
}

/** One step of the connect instructions: a number in a circle, then what to do. */
@Composable
private fun NumberedStep(number: Int, content: @Composable () -> Unit) {
    Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
        Box(Modifier.size(26.dp).clip(CircleShape).background(Flexarr.silver), contentAlignment = Alignment.Center) {
            Text("$number", fontSize = 15.sp, fontWeight = FontWeight.Bold, color = Flexarr.room)
        }
        Box(Modifier.padding(top = 2.dp)) {
            androidx.compose.material3.ProvideTextStyle(TextStyle(fontSize = 17.sp, color = Flexarr.screen)) { content() }
        }
    }
}

@Composable
private fun ErrorNote(message: String) {
    Row(
        // Read out when it appears.
        Modifier.fillMaxWidth().clip(RoundedCornerShape(12.dp)).background(Color(0x996B3347)).padding(14.dp).semantics(mergeDescendants = true) { liveRegion = LiveRegionMode.Polite },
        horizontalArrangement = Arrangement.spacedBy(10.dp),
    ) {
        Icon(Icons.Filled.Warning, contentDescription = null, tint = Flexarr.screen, modifier = Modifier.size(18.dp))
        Text(message, fontSize = 15.sp, fontWeight = FontWeight.Medium, color = Flexarr.screen)
    }
}

/**
 * A server that can't be found (over Tailscale, say): type its address and connect with a code like any other. The API
 * key is only for connecting from outside the home network and tailnet.
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
    LaunchedEffect(Unit) { addressFocus.requestFocus() }

    fun connect() {
        if (connecting) return
        if (!usesKey) {
            val url = FlexarrClient.normalizeAddress(address)
            if (url == null) {
                error = "That doesn’t look like a server address. Try one like http://192.168.1.20:8383."
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

    Column(verticalArrangement = Arrangement.spacedBy(18.dp)) {
        Field("Server address") {
            Input(
                address,
                { address = it },
                "http://192.168.1.20:8383",
                KeyboardOptions(keyboardType = KeyboardType.Uri, autoCorrectEnabled = false, imeAction = if (usesKey) ImeAction.Next else ImeAction.Go),
                KeyboardActions(onGo = { connect() }, onNext = { keyFocus.requestFocus() }),
                Modifier.focusRequester(addressFocus),
                name = "Server address",
            )
        }
        if (usesKey) {
            Field("API key") {
                Input(
                    apiKey,
                    { apiKey = it },
                    "Paste your API key",
                    KeyboardOptions(keyboardType = KeyboardType.Password, imeAction = ImeAction.Go),
                    KeyboardActions(onGo = { connect() }),
                    Modifier.focusRequester(keyFocus),
                    name = "API key",
                    secret = true,
                )
            }
        } else {
            Text("You’ll get a code to approve, like a server found on your Wi-Fi.", fontSize = 13.sp, color = Flexarr.usher)
        }
        Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            ActionButton(
                if (connecting) "Connecting" else "Connect",
                onClick = ::connect,
                icon = Icons.AutoMirrored.Filled.ArrowForward,
                prominent = true,
                enabled = !connecting && address.isNotBlank(),
            )
            ActionButton("Back", onClick = onBack)
        }
        error?.let { ErrorNote(it) }
        if (!usesKey) {
            Text(
                "Connecting from outside your home? Use an API key",
                fontSize = 13.sp,
                fontWeight = FontWeight.SemiBold,
                color = Flexarr.silver,
                modifier = Modifier.pressable({ usesKey = true }).minimumInteractiveComponentSize(),
            )
        }
    }
}

@Composable
private fun Field(title: String, content: @Composable () -> Unit) {
    Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
        Text(title, fontSize = 15.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.usher)
        Box(Modifier.fillMaxWidth().clip(RoundedCornerShape(12.dp)).background(Flexarr.seat).padding(12.dp)) { content() }
    }
}

@Composable
private fun Input(
    value: String,
    onValue: (String) -> Unit,
    placeholder: String,
    keyboard: KeyboardOptions,
    actions: KeyboardActions,
    modifier: Modifier,
    /** What TalkBack calls the field (the title above it isn't part of it). */
    name: String,
    secret: Boolean = false,
) {
    BasicTextField(
        value = value,
        onValueChange = onValue,
        singleLine = true,
        textStyle = TextStyle(fontSize = 17.sp, color = Flexarr.screen),
        cursorBrush = SolidColor(Flexarr.screen),
        keyboardOptions = keyboard,
        keyboardActions = actions,
        visualTransformation = if (secret) PasswordVisualTransformation() else androidx.compose.ui.text.input.VisualTransformation.None,
        modifier = modifier.fillMaxWidth().semantics { contentDescription = name },
        decorationBox = { field ->
            if (value.isEmpty()) Text(placeholder, fontSize = 17.sp, color = Flexarr.usher)
            field()
        },
    )
}
