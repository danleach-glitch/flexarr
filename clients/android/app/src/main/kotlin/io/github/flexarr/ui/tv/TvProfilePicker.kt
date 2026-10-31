package io.github.flexarr.ui.tv

import android.view.KeyEvent
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsFocusedAsState
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.Backspace
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.key.KeyEventType
import androidx.compose.ui.input.key.onKeyEvent
import androidx.compose.ui.input.key.type
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.tv.material3.Border
import androidx.tv.material3.ClickableSurfaceDefaults
import androidx.tv.material3.Surface
import androidx.tv.material3.Text
import io.github.flexarr.AppModel
import io.github.flexarr.core.Profile
import io.github.flexarr.ui.profiles.ProfileAvatar
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.RoomBackground
import io.github.flexarr.ui.theme.marquee
import kotlinx.coroutines.launch

/**
 * "Who's watching?": a row of avatars, then a PIN pad for a protected profile. Full screen on first launch when the
 * household has several profiles, and from the sidebar afterwards ([marksCurrent]: the one watching now is marked, and
 * focus starts on someone else, so with two profiles switching is a single press). As the Apple TV's ProfilePickerView.
 */
@Composable
fun TvProfilePicker(model: AppModel, marksCurrent: Boolean = false, onDone: () -> Unit = {}) {
    val scope = rememberCoroutineScope()
    var asking by remember { mutableStateOf<Profile?>(null) }
    var switching by remember { mutableStateOf<String?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    val requesters = remember(model.profiles.map { it.id }) { model.profiles.associate { it.id to FocusRequester() } }
    val startOn = (if (marksCurrent) model.profiles.firstOrNull { !it.isCurrent } else null) ?: model.profiles.firstOrNull()

    fun choose(profile: Profile, pin: String?) {
        switching = profile.id
        scope.launch {
            try {
                model.chooseProfile(profile, pin)
                asking = null
                onDone()
            } catch (e: Exception) {
                error = e.message
            } finally {
                switching = null
            }
        }
    }

    BackHandler(enabled = marksCurrent && asking == null, onBack = onDone)
    RoomBackground(ambient = null) {
        val pinFor = asking
        if (pinFor != null) {
            PinPad(model, pinFor, error, checking = switching != null, onEdit = { error = null }, onCancel = {
                asking = null
                error = null
            }) { pin -> choose(pinFor, pin) }
        } else {
            Column(Modifier.fillMaxSize(), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(35.dp, Alignment.CenterVertically)) {
                Text("Who’s watching?", style = marquee(38).copy(fontWeight = FontWeight.ExtraBold))
                Row(horizontalArrangement = Arrangement.spacedBy(40.dp)) {
                    model.profiles.forEach { profile ->
                        val caption = when {
                            marksCurrent && profile.isCurrent -> "Watching now"
                            profile.takesPin -> "PIN"
                            else -> null
                        }
                        ProfileChoice(model, profile, caption, busy = switching == profile.id, modifier = Modifier.focusRequester(requesters.getValue(profile.id))) {
                            error = null
                            when {
                                switching != null -> Unit
                                // Already watching: nothing to switch, so this is the same as backing out.
                                marksCurrent && profile.isCurrent -> onDone()
                                profile.takesPin -> asking = profile
                                else -> choose(profile, null)
                            }
                        }
                    }
                }
                Text(error ?: " ", fontSize = 14.sp, fontWeight = FontWeight.Medium)
            }
            LaunchedEffect(startOn?.id) { startOn?.let { runCatching { requesters[it.id]?.requestFocus() } } }
        }
    }
}

/** An avatar that lifts and gains a ring in projected white on focus, with the name beneath. */
@Composable
private fun ProfileChoice(model: AppModel, profile: Profile, caption: String?, busy: Boolean, modifier: Modifier, onClick: () -> Unit) {
    val source = remember { MutableInteractionSource() }
    val focused by source.collectIsFocusedAsState()
    Column(Modifier.width(140.dp), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(14.dp)) {
        Surface(
            onClick = onClick,
            // The name is beneath, outside what takes focus: TalkBack is told it here.
            modifier = modifier.semantics { contentDescription = listOfNotNull(profile.name, caption).joinToString(", ") },
            interactionSource = source,
            shape = ClickableSurfaceDefaults.shape(CircleShape),
            colors = ClickableSurfaceDefaults.colors(containerColor = Color.Transparent, focusedContainerColor = Color.Transparent, pressedContainerColor = Color.Transparent),
            border = ClickableSurfaceDefaults.border(focusedBorder = Border(BorderStroke(4.dp, Flexarr.screen), shape = CircleShape)),
            scale = ClickableSurfaceDefaults.scale(focusedScale = 1.1f, pressedScale = 1.02f),
        ) {
            // The ring goes round the avatar, clear of it.
            Box(Modifier.padding(7.dp), contentAlignment = Alignment.Center) {
                ProfileAvatar(model, profile, size = 110)
                if (busy) {
                    Box(Modifier.size(110.dp).background(Color.Black.copy(alpha = 0.45f), CircleShape))
                    CircularProgressIndicator(color = Flexarr.screen)
                }
            }
        }
        Column(horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(4.dp)) {
            Text(profile.name, fontSize = 17.sp, fontWeight = FontWeight.SemiBold, color = if (focused) Flexarr.screen else Flexarr.usher, maxLines = 1)
            Text(caption ?: " ", fontSize = 12.sp, fontWeight = FontWeight.Medium, color = Flexarr.usher, maxLines = 1)
        }
    }
}

/** A 4 to 8 digit PIN entered with the remote: a keypad, for remotes without number keys. Number keys work too. */
@Composable
private fun PinPad(model: AppModel, profile: Profile, error: String?, checking: Boolean, onEdit: () -> Unit, onCancel: () -> Unit, onSubmit: (String) -> Unit) {
    var pin by remember { mutableStateOf("") }
    var shortPin by remember { mutableStateOf(false) }
    val first = remember { FocusRequester() }
    LaunchedEffect(Unit) { runCatching { first.requestFocus() } }
    // A wrong PIN starts over.
    LaunchedEffect(error) { if (error != null) pin = "" }
    BackHandler(onBack = onCancel)

    fun press(key: String) {
        if (checking) return
        onEdit()
        shortPin = false
        when (key) {
            "delete" -> if (pin.isEmpty()) onCancel() else pin = pin.dropLast(1)
            "ok" -> if (pin.length < 4) shortPin = true else onSubmit(pin)
            else -> if (pin.length < 8) pin += key
        }
    }

    Row(
        Modifier.fillMaxSize().onKeyEvent { event ->
            val code = event.nativeKeyEvent.keyCode
            (event.type == KeyEventType.KeyDown && code in KeyEvent.KEYCODE_0..KeyEvent.KEYCODE_9).also { if (it) press("${code - KeyEvent.KEYCODE_0}") }
        },
        horizontalArrangement = Arrangement.spacedBy(60.dp, Alignment.CenterHorizontally),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Column(Modifier.width(320.dp), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(15.dp)) {
            ProfileAvatar(model, profile, size = 90)
            Text("Enter ${profile.name}’s PIN", style = marquee(28).copy(fontWeight = FontWeight.ExtraBold))
            Row(Modifier.height(20.dp), horizontalArrangement = Arrangement.spacedBy(11.dp), verticalAlignment = Alignment.CenterVertically) {
                repeat(maxOf(4, pin.length)) { index ->
                    Box(Modifier.size(15.dp).background(if (index < pin.length) Flexarr.screen else Color.Transparent, CircleShape).border(1.5.dp, Flexarr.usher, CircleShape))
                }
            }
            val message = when {
                error != null -> error
                shortPin -> "A PIN is 4 to 8 digits."
                checking -> "Checking"
                else -> " "
            }
            Text(message, fontSize = 14.sp, fontWeight = FontWeight.Medium, color = if (error != null || shortPin) Flexarr.danger else Flexarr.screen)
        }
        Column(verticalArrangement = Arrangement.spacedBy(13.dp)) {
            listOf(listOf("1", "2", "3"), listOf("4", "5", "6"), listOf("7", "8", "9"), listOf("delete", "0", "ok")).forEach { row ->
                Row(horizontalArrangement = Arrangement.spacedBy(13.dp)) {
                    row.forEach { key ->
                        val modifier = Modifier.width(65.dp).then(if (key == "1") Modifier.focusRequester(first) else Modifier)
                        when (key) {
                            "delete" -> TvActionButton(null, onClick = { press(key) }, icon = Icons.AutoMirrored.Filled.Backspace, contentDescription = "Delete", modifier = modifier)
                            "ok" -> TvActionButton("OK", onClick = { press(key) }, prominent = true, modifier = modifier)
                            else -> TvActionButton(key, onClick = { press(key) }, modifier = modifier)
                        }
                    }
                }
            }
        }
    }
}
