package io.github.flexarr.ui.tv

import io.github.flexarr.core.i18n.t
import io.github.flexarr.core.i18n.Words

import androidx.compose.material.icons.filled.Lock
import androidx.compose.material.icons.filled.LockOpen
import androidx.compose.material3.Icon
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
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
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
import io.github.flexarr.core.FlexarrException
import io.github.flexarr.core.Profile
import io.github.flexarr.ui.profiles.ProfileAvatar
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.RoomBackground
import io.github.flexarr.ui.theme.marquee
import kotlinx.coroutines.android.awaitFrame
import kotlinx.coroutines.launch

/** Turning child lock on (when the server asks for the admin PIN) or off: the pad asks for the admin PIN. */
private enum class TvKidsLockStep { Lock, Release }

/**
 * "Who's watching?": a row of avatars, then a PIN pad for a protected profile. Full screen on first launch when the
 * household has several profiles, and from the sidebar afterwards ([marksCurrent]: the one watching now is marked, and
 * focus starts on someone else, so with two profiles switching is a single press). As the Apple TV's ProfilePickerView:
 * holding OK on a kids profile turns child lock on with it, or off with the admin PIN. The row scrolls, so a household
 * of more than five can reach everyone.
 */
@Composable
fun TvProfilePicker(model: AppModel, marksCurrent: Boolean = false, onDone: () -> Unit = {}) {
    val scope = rememberCoroutineScope()
    var asking by remember { mutableStateOf<Profile?>(null) }
    var switching by remember { mutableStateOf<String?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    // Its PIN is forgotten: the pad asks for the admin PIN, which takes it off.
    var forgetting by remember { mutableStateOf<Profile?>(null) }
    // Child lock: the admin PIN asked for, the kids profile it goes on with, and a lock on its way to the server.
    var locking by remember { mutableStateOf<TvKidsLockStep?>(null) }
    var lockTarget by remember { mutableStateOf<Profile?>(null) }
    var lockingBusy by remember { mutableStateOf(false) }
    // Child lock can be turned off from the web while the app's open: what it says now.
    LaunchedEffect(Unit) { model.refreshKidsLock() }
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

    /** The admin PIN takes a forgotten PIN off, and the profile is chosen. */
    fun forget(profile: Profile, adminPin: String) {
        val client = model.client ?: return
        switching = profile.id
        scope.launch {
            try {
                val cleared = client.forgetPin(profile.id, adminPin)
                forgetting = null
                choose(cleared, null)
            } catch (e: FlexarrException.ParentPinNeeded) {
                error = e.message ?: t("That isn’t the admin PIN.")
            } catch (e: FlexarrException.Http) {
                error = if (e.status == 404) t("Take the PIN off in Settings › Profiles on the web.") else e.message
            } catch (e: Exception) {
                error = e.message
            } finally {
                if (forgetting != null) switching = null
            }
        }
    }

    /**
     * Without a PIN first when locking: the main profile, unlocked here, needs none; otherwise the server asks and the
     * pad comes up. Locked, the kids profile held is the one this TV stays on.
     */
    fun kidsLock(step: TvKidsLockStep, pin: String?) {
        if (lockingBusy) return
        lockingBusy = true
        scope.launch {
            try {
                if (step == TvKidsLockStep.Lock) {
                    model.lockToKids(pin)
                    locking = null
                    val target = lockTarget
                    lockTarget = null
                    if (target != null && model.currentProfile?.id != target.id) runCatching { model.chooseProfile(target) }
                    onDone()
                } else {
                    model.releaseKidsLock(pin ?: return@launch)
                    locking = null
                }
            } catch (e: FlexarrException.ParentPinNeeded) {
                if (pin == null) locking = step else error = e.message ?: t("That isn’t the admin PIN.")
            } catch (e: Exception) {
                error = e.message ?: t("Something went wrong.")
            } finally {
                lockingBusy = false
            }
        }
    }

    fun toggleChildLock(profile: Profile) {
        if (switching != null || lockingBusy) return
        error = null
        if (model.kidsLock) {
            locking = TvKidsLockStep.Release
        } else {
            lockTarget = profile
            kidsLock(TvKidsLockStep.Lock, null)
        }
    }

    BackHandler(enabled = marksCurrent && asking == null && forgetting == null && locking == null, onBack = onDone)
    RoomBackground(ambient = null) {
        val pinFor = asking
        val forgot = forgetting
        val lockStep = locking
        if (lockStep != null) {
            PinPad(model, null, error, checking = lockingBusy, onEdit = { error = null }, onCancel = {
                locking = null
                lockTarget = null
                error = null
            }, adminPin = true) { pin -> kidsLock(lockStep, pin) }
        } else if (forgot != null) {
            PinPad(model, forgot, error, checking = switching != null, onEdit = { error = null }, onCancel = {
                forgetting = null
                error = null
            }, adminPin = true, note = t("It takes {name}’s PIN off.", "name" to forgot.name)) { pin -> forget(forgot, pin) }
        } else if (pinFor != null) {
            PinPad(model, pinFor, error, checking = switching != null, onEdit = { error = null }, onCancel = {
                asking = null
                error = null
            }, onForgot = if (pinFor.isMain || pinFor.takesAdminPin) null else ({
                forgetting = pinFor
                asking = null
                error = null
            })) { pin -> choose(pinFor, pin) }
        } else {
            val rowState = rememberLazyListState()
            Column(Modifier.fillMaxSize(), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(35.dp, Alignment.CenterVertically)) {
                Text(t("Who’s watching?"), style = marquee(38).copy(fontWeight = FontWeight.ExtraBold))
                // Scrolls sideways past five or so, keeping the focused avatar in view; centred while they all fit.
                LazyRow(
                    state = rowState,
                    horizontalArrangement = Arrangement.spacedBy(40.dp, Alignment.CenterHorizontally),
                    contentPadding = PaddingValues(horizontal = 60.dp, vertical = 16.dp),
                    modifier = Modifier.fillMaxWidth(),
                ) {
                    items(model.profiles, key = { it.id }) { profile ->
                        val caption = when {
                            marksCurrent && profile.isCurrent -> t("Watching now")
                            profile.takesPin -> t("PIN")
                            else -> null
                        }
                        // The padlock over each kids profile: open while child lock can be turned on (holding OK locks
                        // to that profile), closed while it's on (holding OK turns it off, with the admin PIN).
                        val padlock = if (profile.isKids && model.showsKidsPadlock) model.kidsLock else null
                        ProfileChoice(
                            model, profile, caption,
                            busy = switching == profile.id || (lockingBusy && lockTarget?.id == profile.id),
                            padlock = padlock,
                            modifier = Modifier.focusRequester(requesters.getValue(profile.id)),
                            onLongClick = if (padlock != null) ({ toggleChildLock(profile) }) else null,
                        ) {
                            error = null
                            // The admin PIN just given here (child lock off) goes with this choice, rather than the pad
                            // again; a profile with its own PIN still asks for that.
                            val held = model.recentParentPin
                            when {
                                switching != null || lockingBusy -> Unit
                                // Already watching: nothing to switch, so this is the same as backing out.
                                marksCurrent && profile.isCurrent -> onDone()
                                profile.takesPin && held != null && (profile.isMain || !profile.hasPin) -> choose(profile, if (profile.isMain) held else null)
                                profile.takesPin -> asking = profile
                                else -> choose(profile, null)
                            }
                        }
                    }
                }
                Text(error ?: " ", fontSize = 14.sp, fontWeight = FontWeight.Medium)
            }
            LaunchedEffect(startOn?.id) {
                val start = startOn ?: return@LaunchedEffect
                // Off the edge of a long row it isn't laid out yet: scrolled to first, so its focus can be asked for.
                val index = model.profiles.indexOfFirst { it.id == start.id }
                if (index >= 0) runCatching { rowState.scrollToItem(index) }
                awaitFrame()
                runCatching { requesters[start.id]?.requestFocus() }
            }
        }
    }
}

/** An avatar that lifts and gains a ring in projected white on focus, with the name beneath. */
@Composable
private fun ProfileChoice(
    model: AppModel,
    profile: Profile,
    caption: String?,
    busy: Boolean,
    modifier: Modifier,
    padlock: Boolean? = null,
    onLongClick: (() -> Unit)? = null,
    onClick: () -> Unit,
) {
    val source = remember { MutableInteractionSource() }
    val focused by source.collectIsFocusedAsState()
    Column(Modifier.width(140.dp), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(14.dp)) {
        Surface(
            onClick = onClick,
            onLongClick = onLongClick,
            // The name is beneath, outside what takes focus: TalkBack is told it here.
            modifier = modifier.semantics {
                contentDescription = listOfNotNull(profile.name, caption, if (padlock == true) t("Child lock") else null).joinToString(", ")
            },
            interactionSource = source,
            shape = ClickableSurfaceDefaults.shape(CircleShape),
            colors = ClickableSurfaceDefaults.colors(containerColor = Color.Transparent, focusedContainerColor = Color.Transparent, pressedContainerColor = Color.Transparent),
            border = ClickableSurfaceDefaults.border(focusedBorder = Border(BorderStroke(4.dp, Flexarr.screen), shape = CircleShape)),
            scale = ClickableSurfaceDefaults.scale(focusedScale = 1.1f, pressedScale = 1.02f),
        ) {
            // The ring goes round the avatar, clear of it.
            Box(Modifier.padding(7.dp), contentAlignment = Alignment.Center) {
                ProfileAvatar(model, profile, size = 110)
                if (padlock != null && !busy) {
                    Box(Modifier.size(110.dp).background(Color.Black.copy(alpha = 0.35f), CircleShape), contentAlignment = Alignment.Center) {
                        Icon(if (padlock) Icons.Filled.Lock else Icons.Filled.LockOpen, contentDescription = null, tint = Flexarr.screen, modifier = Modifier.size(40.dp))
                    }
                }
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
private fun PinPad(
    model: AppModel,
    profile: Profile?,
    error: String?,
    checking: Boolean,
    onEdit: () -> Unit,
    onCancel: () -> Unit,
    adminPin: Boolean = profile?.takesAdminPin ?: true,
    note: String? = null,
    onForgot: (() -> Unit)? = null,
    onSubmit: (String) -> Unit,
) {
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
            // The admin PIN is the server's, not the main profile's own: a padlock rather than its picture.
            if (adminPin || profile == null) {
                Icon(Icons.Filled.Lock, contentDescription = null, tint = Flexarr.usher, modifier = Modifier.size(80.dp))
            } else {
                ProfileAvatar(model, profile, size = 90)
            }
            Text(if (adminPin || profile == null) t("Enter the admin PIN") else t("Enter {name}’s PIN", "name" to profile.name),
                style = marquee(28).copy(fontWeight = FontWeight.ExtraBold))
            note?.let { Text(it, fontSize = 14.sp, color = Flexarr.usher) }
            Row(Modifier.height(20.dp), horizontalArrangement = Arrangement.spacedBy(11.dp), verticalAlignment = Alignment.CenterVertically) {
                repeat(maxOf(4, pin.length)) { index ->
                    Box(Modifier.size(15.dp).background(if (index < pin.length) Flexarr.screen else Color.Transparent, CircleShape).border(1.5.dp, Flexarr.usher, CircleShape))
                }
            }
            val message = when {
                error != null -> error
                shortPin -> t("A PIN is 4 to 8 digits.")
                checking -> t("Checking")
                else -> " "
            }
            Text(message, fontSize = 14.sp, fontWeight = FontWeight.Medium, color = if (error != null || shortPin) Flexarr.danger else Flexarr.screen)
            onForgot?.let { TvActionButton(t("Forgot PIN?"), onClick = it) }
        }
        Column(verticalArrangement = Arrangement.spacedBy(13.dp)) {
            listOf(listOf("1", "2", "3"), listOf("4", "5", "6"), listOf("7", "8", "9"), listOf("delete", "0", "ok")).forEach { row ->
                Row(horizontalArrangement = Arrangement.spacedBy(13.dp)) {
                    row.forEach { key ->
                        val modifier = Modifier.width(65.dp).then(if (key == "1") Modifier.focusRequester(first) else Modifier)
                        when (key) {
                            "delete" -> TvActionButton(null, onClick = { press(key) }, icon = Icons.AutoMirrored.Filled.Backspace, contentDescription = t(Words.delete), modifier = modifier)
                            "ok" -> TvActionButton(t("OK"), onClick = { press(key) }, prominent = true, modifier = modifier)
                            else -> TvActionButton(key, onClick = { press(key) }, modifier = modifier)
                        }
                    }
                }
            }
        }
    }
}
