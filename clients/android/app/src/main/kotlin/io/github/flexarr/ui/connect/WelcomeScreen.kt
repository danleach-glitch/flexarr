package io.github.flexarr.ui.connect

import android.content.Context
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.ui.BiasAlignment
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.pager.HorizontalPager
import androidx.compose.foundation.pager.rememberPagerState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Dns
import androidx.compose.material.icons.filled.Language
import androidx.compose.material.icons.filled.Wifi
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.material3.minimumInteractiveComponentSize
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalUriHandler
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.core.content.edit
import io.github.flexarr.ui.components.ActionButton
import io.github.flexarr.ui.components.BrandMark
import io.github.flexarr.ui.components.pressable
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.RoomBackground
import io.github.flexarr.ui.theme.marquee
import kotlinx.coroutines.launch

/** Whether the welcome pages have been seen or skipped on this install. */
@Composable
fun rememberWelcomed(): Pair<Boolean, () -> Unit> {
    val context = LocalContext.current
    val prefs = remember { context.getSharedPreferences(WELCOME_PREFS, Context.MODE_PRIVATE) }
    var welcomed by remember { mutableStateOf(prefs.getBoolean(WELCOMED, false)) }
    return welcomed to {
        prefs.edit { putBoolean(WELCOMED, true) }
        welcomed = true
    }
}

private const val WELCOME_PREFS = "welcome"
private const val WELCOMED = "welcomed"
private const val PAGES = 3

/**
 * First launch on a fresh install: three short pages saying what Flexarr is, that it needs its server at home (with
 * the way to flexarr.io), and how the app finds it. People from the store often don't know there's a server at all.
 * Skippable; the connect screen repeats the link for anyone who skips. As the iPhone's WelcomeView.
 */
@Composable
fun WelcomeScreen(onDone: () -> Unit) {
    val pager = rememberPagerState { PAGES }
    val scope = rememberCoroutineScope()
    val uri = LocalUriHandler.current
    val last = pager.currentPage == PAGES - 1

    RoomBackground(ambient = Flexarr.silver, intensity = 0.22f) {
        Column(Modifier.fillMaxSize().safeDrawingPadding(), horizontalAlignment = Alignment.CenterHorizontally) {
            Row(Modifier.fillMaxWidth().height(48.dp).padding(horizontal = 24.dp), horizontalArrangement = Arrangement.End, verticalAlignment = Alignment.CenterVertically) {
                Text(
                    "Skip",
                    fontSize = 17.sp,
                    fontWeight = FontWeight.SemiBold,
                    color = Flexarr.usher,
                    modifier = Modifier.alpha(if (last) 0f else 1f).pressable(onDone, enabled = !last).minimumInteractiveComponentSize(),
                )
            }
            HorizontalPager(pager, Modifier.weight(1f)) { page ->
                when (page) {
                    0 -> WelcomePage(
                        art = { BrandMark(96.dp) },
                        title = "Your entertainment, on every screen.",
                        message = "Flexarr plays the movies, shows and music you keep at home, on this phone, wherever you are.",
                    )
                    1 -> WelcomePage(
                        art = { Symbol(Icons.Filled.Dns) },
                        title = "Set up your free server",
                        message = "The app plays from your own Flexarr server, on a computer in your home: a Mac, a NAS or anything with Docker. The server is completely free, and setting it up takes less than a minute.",
                    ) {
                        ActionButton("Set up at flexarr.io", onClick = { uri.openUri(INSTALL_URL) }, icon = Icons.Filled.Language)
                    }
                    else -> WelcomePage(
                        art = { Symbol(Icons.Filled.Wifi) },
                        title = "Then connect",
                        message = "On the same Wi-Fi, this phone finds your server by itself. Then approve the code it shows and you’re in.",
                    )
                }
            }
            // Which page, as iPhone's dots.
            Row(Modifier.padding(vertical = 16.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                repeat(PAGES) { index ->
                    Box(Modifier.size(7.dp).clip(CircleShape).background(Flexarr.screen.copy(alpha = if (index == pager.currentPage) 1f else 0.35f)))
                }
            }
            Box(Modifier.widthIn(max = 520.dp).fillMaxWidth().padding(horizontal = 24.dp).padding(bottom = 24.dp)) {
                ActionButton(
                    if (last) "Find my server" else "Next",
                    onClick = { if (last) onDone() else scope.launch { pager.animateScrollToPage(pager.currentPage + 1) } },
                    modifier = Modifier.fillMaxWidth(),
                    prominent = true,
                )
            }
        }
    }
}

@Composable
private fun WelcomePage(art: @Composable () -> Unit, title: String, message: String, extra: @Composable () -> Unit = {}) {
    BoxWithConstraints(Modifier.fillMaxSize(), contentAlignment = Alignment.TopCenter) {
        // A third of the way down, as on iPhone; scrolls rather than cutting the words off when the text is large.
        Column(
            Modifier.widthIn(max = 520.dp).fillMaxWidth().verticalScroll(rememberScrollState()).heightIn(min = maxHeight).padding(horizontal = 24.dp),
            verticalArrangement = Arrangement.spacedBy(18.dp, BiasAlignment.Vertical(-1f / 3)),
        ) {
            art()
            Text(title, style = marquee(40))
            Text(message, fontSize = 20.sp, fontWeight = FontWeight.Medium, color = Flexarr.usher)
            Box(Modifier.padding(top = 6.dp)) { extra() }
        }
    }
}

@Composable
private fun Symbol(icon: ImageVector) {
    Box(Modifier.height(96.dp), contentAlignment = Alignment.CenterStart) {
        Icon(icon, contentDescription = null, tint = Flexarr.silver, modifier = Modifier.size(56.dp))
    }
}
