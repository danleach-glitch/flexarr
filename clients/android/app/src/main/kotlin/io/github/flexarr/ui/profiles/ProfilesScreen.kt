package io.github.flexarr.ui.profiles

import androidx.activity.compose.BackHandler
import androidx.compose.animation.AnimatedContent
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.material3.Icon
import androidx.compose.material3.TextButton
import androidx.compose.material.icons.filled.LockOpen
import androidx.compose.material.icons.filled.Lock
import androidx.compose.material.icons.Icons
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
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
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.semantics.LiveRegionMode
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.liveRegion
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import coil3.compose.AsyncImage
import io.github.flexarr.AppModel
import io.github.flexarr.core.FlexarrException
import io.github.flexarr.core.Profile
import io.github.flexarr.ui.components.ActionButton
import io.github.flexarr.ui.components.pressable
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.RoomBackground
import io.github.flexarr.ui.theme.ambientColor
import io.github.flexarr.ui.theme.marquee
import kotlinx.coroutines.launch
import io.github.flexarr.ui.theme.Tokens

/** First launch, when the household has several profiles: choose who's watching. */
@Composable
fun ProfilesScreen(model: AppModel) {
    RoomBackground(ambient = null) {
        Box(Modifier.safeDrawingPadding()) { ProfilePicker(model) }
    }
}

/**
 * "Who's watching?": avatars to tap, then a PIN for a protected profile. Full screen on first launch, and a sheet from
 * the avatar on Home or from Settings afterwards ([marksCurrent]: the one watching now is marked, and can't be chosen
 * again). With [asksLockPin], it opens on the admin PIN to lock this device to kids profiles (Settings' row,
 * when the server asked for it), and cancelling that is [onCancelled].
 */
@Composable
fun ProfilePicker(
    model: AppModel,
    marksCurrent: Boolean = false,
    asksLockPin: Boolean = false,
    onCancelled: () -> Unit = {},
    onChosen: () -> Unit = {},
) {
    val scope = rememberCoroutineScope()
    var asking by remember { mutableStateOf<Profile?>(null) }
    // Leaving a kids profile: the admin PIN is asked for, keeping the chosen profile's own if it had one.
    var parent by remember { mutableStateOf<ParentPin?>(null) }
    // Locking this device to kids profiles, or unlocking it, with the admin PIN.
    var locking by remember { mutableStateOf<KidsLockStep?>(if (asksLockPin) KidsLockStep.Lock else null) }
    var lockingBusy by remember { mutableStateOf(false) }
    // The kids profile whose padlock was held: child lock goes on with it.
    var lockTarget by remember { mutableStateOf<Profile?>(null) }
    // Unlocked here, the admin PIN just given goes with the next choice rather than being asked for again.
    var unlockedWith by remember { mutableStateOf<String?>(null) }
    var switching by remember { mutableStateOf<String?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    // Its PIN is forgotten: the admin PIN is asked for, which takes it off.
    var forgetting by remember { mutableStateOf<Profile?>(null) }
    // Child lock can be turned off from the web while the app's open: what it says now.
    LaunchedEffect(Unit) { model.refreshKidsLock() }

    suspend fun choose(profile: Profile, pin: String?, parentPin: String? = null): Boolean {
        switching = profile.id
        try {
            model.chooseProfile(profile, pin, parentPin ?: unlockedWith)
            onChosen()
            return true
        } catch (e: FlexarrException.ParentPinNeeded) {
            if (parentPin == null) {
                unlockedWith = null
                asking = profile
                parent = ParentPin(pin)
            } else {
                error = e.message
            }
            return false
        } catch (e: FlexarrException.OwnPinWrong) {
            // The admin PIN was right: back to the chosen profile's own.
            asking = profile
            parent = null
            error = "That isn’t ${profile.name}’s PIN."
            return false
        } catch (e: Exception) {
            error = e.message
            return false
        } finally {
            switching = null
        }
    }

    /**
     * Without a PIN first when locking: the main profile, unlocked here, needs none; otherwise the server asks. Locked
     * with several kids profiles, the picker stays open for the parent to choose which one this device stays on.
     */
    suspend fun kidsLock(step: KidsLockStep, pin: String?): Boolean {
        try {
            if (step == KidsLockStep.Lock) {
                val choosing = model.lockToKids(pin)
                locking = null
                // The server chose a kids profile: the one whose padlock was held, if it's another. Without one (Settings'
                // row), several kids profiles keep the picker open to choose which this device stays on.
                val target = lockTarget
                lockTarget = null
                if (target != null) {
                    if (model.currentProfile?.id != target.id) runCatching { model.chooseProfile(target) }
                    onChosen()
                } else if (!choosing) {
                    onChosen()
                }
            } else {
                model.releaseKidsLock(pin ?: return false)
                unlockedWith = pin
                locking = null
            }
            return true
        } catch (e: FlexarrException.ParentPinNeeded) {
            if (pin == null) locking = step else error = e.message
            return false
        } catch (e: Exception) {
            error = e.message
            return false
        }
    }

    val cancel = {
        // Only cancelling the lock itself closes the sheet; once locked, Cancel is the kids profiles again.
        if (asksLockPin && locking != null) onCancelled()
        asking = null
        parent = null
        locking = null
        forgetting = null
        error = null
    }

    /** The admin PIN takes a forgotten PIN off, and the profile is chosen. */
    suspend fun forget(profile: Profile, adminPin: String): Boolean {
        val client = model.client ?: return false
        return try {
            val cleared = client.forgetPin(profile.id, adminPin)
            forgetting = null
            choose(cleared, null)
        } catch (e: FlexarrException.ParentPinNeeded) {
            error = e.message ?: "That isn’t the admin PIN."
            false
        } catch (e: FlexarrException.Http) {
            error = if (e.status == 404) "Take the PIN off in Settings › Profiles on the web." else e.message
            false
        } catch (e: Exception) {
            error = e.message
            false
        }
    }

    AnimatedContent(PickerStep(asking, parent, locking, forgetting), label = "profiles") { (pinFor, parentFor, lockStep, forgot) ->
        if (forgot != null) {
            PinEntry(
                profile = null,
                title = "Enter the admin PIN",
                note = "It takes ${forgot.name}’s PIN off.",
                model = model,
                error = error,
                onEdit = { error = null },
                onCancel = cancel,
                onSubmit = { pin -> forget(forgot, pin) },
            )
        } else if (lockStep != null) {
            PinEntry(
                profile = null,
                title = "Enter the admin PIN",
                model = model,
                error = error,
                onEdit = { error = null },
                onCancel = cancel,
                onSubmit = { pin -> kidsLock(lockStep, pin) },
            )
        } else if (pinFor != null) {
            // The admin PIN is the server's, not the main profile's own: a padlock rather than its picture. Where the
            // server says choosing the main profile takes it (a guest), the same.
            val adminPin = parentFor != null || pinFor.takesAdminPin
            PinEntry(
                profile = if (adminPin) null else pinFor,
                title = if (adminPin) "Enter the admin PIN" else null,
                model = model,
                error = error,
                onEdit = { error = null },
                onForgot = if (adminPin || pinFor.isMain) null else ({
                    forgetting = pinFor
                    asking = null
                    error = null
                }),
                onCancel = cancel,
                onSubmit = { pin -> if (parentFor != null) choose(pinFor, parentFor.ownPin, pin) else choose(pinFor, pin) },
            )
        } else {
            // Clear of the gesture bar, where Lock and Unlock sit last.
            Column(
                Modifier.fillMaxWidth().navigationBarsPadding().padding(bottom = 16.dp),
                horizontalAlignment = Alignment.CenterHorizontally,
                verticalArrangement = Arrangement.spacedBy(32.dp),
            ) {
                Text("Who’s watching?", style = marquee(40), modifier = Modifier.padding(top = if (marksCurrent) 28.dp else 80.dp))
                LazyVerticalGrid(
                    columns = GridCells.Adaptive(110.dp),
                    horizontalArrangement = Arrangement.spacedBy(20.dp),
                    verticalArrangement = Arrangement.spacedBy(28.dp),
                    modifier = Modifier.padding(horizontal = 32.dp),
                ) {
                    items(model.profiles, key = { it.id }) { profile ->
                        val current = marksCurrent && profile.isCurrent
                        // The padlock over each kids profile's picture: open while child lock can be turned on (holding
                        // it locks to that profile), closed while it's on (holding it turns it off, with the PIN).
                        val padlock = profile.isKids && model.showsKidsPadlock // at launch too, not only when switching
                        val toggleLock = {
                            error = null
                            if (model.kidsLock) {
                                locking = KidsLockStep.Release
                            } else if (!lockingBusy) {
                                lockingBusy = true
                                lockTarget = profile
                                scope.launch {
                                    try {
                                        kidsLock(KidsLockStep.Lock, null)
                                    } finally {
                                        lockingBusy = false
                                    }
                                }
                            }
                        }
                        Column(
                            Modifier.pressable(
                                enabled = switching == null && (!current || padlock),
                                onClick = {
                                    error = null
                                    // The admin PIN just given here (child lock off) goes with this choice, rather
                                    // than the keypad again; a profile with its own PIN still asks for that.
                                    val held = unlockedWith ?: model.recentParentPin
                                    when {
                                        current -> Unit
                                        profile.takesPin && held != null && (profile.isMain || !profile.hasPin) ->
                                            scope.launch { choose(profile, if (profile.isMain) held else null, held) }
                                        profile.takesPin -> asking = profile
                                        else -> scope.launch { choose(profile, null) }
                                    }
                                },
                                onLongClick = if (padlock) toggleLock else null,
                                onLongClickLabel = if (model.kidsLock) "Turn off child lock" else "Child lock",
                            ),
                            horizontalAlignment = Alignment.CenterHorizontally,
                            verticalArrangement = Arrangement.spacedBy(10.dp),
                        ) {
                            Box(contentAlignment = Alignment.Center) {
                                ProfileAvatar(model, profile, size = 96)
                                if (switching == profile.id) {
                                    Box(Modifier.size(96.dp).clip(CircleShape).background(Color.Black.copy(alpha = 0.45f)))
                                    CircularProgressIndicator(color = Flexarr.screen)
                                }
                                if (current) Box(Modifier.size(108.dp).border(3.dp, Flexarr.screen, CircleShape))
                                if (padlock) {
                                    Box(Modifier.size(96.dp).clip(CircleShape).background(Color.Black.copy(alpha = 0.35f)), contentAlignment = Alignment.Center) {
                                        if (lockingBusy && lockTarget?.id == profile.id) {
                                            CircularProgressIndicator(Modifier.size(28.dp), strokeWidth = 3.dp, color = Flexarr.screen)
                                        } else {
                                            Icon(if (model.kidsLock) Icons.Filled.Lock else Icons.Filled.LockOpen, contentDescription = null, tint = Flexarr.screen, modifier = Modifier.size(36.dp))
                                        }
                                    }
                                }
                            }
                            Column(horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(2.dp)) {
                                Text(profile.name, fontSize = 17.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen, maxLines = 1)
                                Text(
                                    if (current) "Watching now" else if (profile.takesPin) "PIN" else " ",
                                    fontSize = 12.sp,
                                    fontWeight = FontWeight.Medium,
                                    color = Flexarr.usher,
                                )
                            }
                        }
                    }
                }
                error?.let {
                    Text(
                        it,
                        fontSize = 15.sp,
                        fontWeight = FontWeight.Medium,
                        color = Flexarr.danger,
                        textAlign = TextAlign.Center,
                        modifier = Modifier.padding(horizontal = 32.dp).semantics { liveRegion = LiveRegionMode.Polite },
                    )
                }
            }
        }
    }
}

private data class ParentPin(val ownPin: String?)

/** What the picker shows: the profiles, or a PIN for one of them, child lock, or a forgotten PIN. */
private data class PickerStep(val asking: Profile?, val parent: ParentPin?, val locking: KidsLockStep?, val forgetting: Profile?)

private enum class KidsLockStep { Lock, Release }

/**
 * A 4 to 8 digit PIN, on the number pad: [profile]'s by default; with a [title], whatever that says (the admin PIN, for
 * a parent), under [profile]'s picture, or a padlock without one.
 */
@Composable
private fun PinEntry(
    profile: Profile?,
    model: AppModel,
    title: String? = null,
    note: String? = null,
    error: String?,
    onEdit: () -> Unit,
    onForgot: (() -> Unit)? = null,
    onCancel: () -> Unit,
    onSubmit: suspend (String) -> Boolean,
) {
    val scope = rememberCoroutineScope()
    var pin by remember { mutableStateOf("") }
    var checking by remember { mutableStateOf(false) }
    val focus = remember { FocusRequester() }
    LaunchedEffect(Unit) { focus.requestFocus() }
    // Back is Cancel: the profiles again, rather than out of the app.
    BackHandler(onBack = onCancel)

    fun submit() {
        if (pin.length < 4 || checking) return
        checking = true
        scope.launch {
            // Only comes back false on a wrong PIN; a right one replaces this screen.
            if (!onSubmit(pin)) pin = ""
            checking = false
        }
    }

    Column(Modifier.fillMaxWidth().padding(32.dp), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(22.dp)) {
        if (profile != null) {
            ProfileAvatar(model, profile, size = 84)
        } else {
            Icon(Icons.Filled.Lock, contentDescription = null, tint = Flexarr.usher, modifier = Modifier.size(72.dp))
        }
        Text(title ?: "Enter ${profile?.name}’s PIN", style = marquee(32), textAlign = TextAlign.Center)
        note?.let { Text(it, fontSize = 15.sp, color = Flexarr.usher, textAlign = TextAlign.Center) }
        BasicTextField(
            value = pin,
            onValueChange = {
                pin = it.filter(Char::isDigit).take(8)
                onEdit()
            },
            singleLine = true,
            visualTransformation = PasswordVisualTransformation(),
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.NumberPassword, imeAction = ImeAction.Done),
            keyboardActions = KeyboardActions(onDone = { submit() }),
            textStyle = TextStyle(fontSize = 22.sp, color = Flexarr.screen, textAlign = TextAlign.Center),
            cursorBrush = SolidColor(Flexarr.screen),
            modifier = Modifier.width(220.dp).clip(RoundedCornerShape(Tokens.radiusControl)).background(Flexarr.seat).padding(vertical = 12.dp).focusRequester(focus)
                .semantics { contentDescription = "PIN" },
        )
        Text(
            error ?: if (checking) "Checking" else " ",
            fontSize = 15.sp,
            fontWeight = FontWeight.Medium,
            color = if (error == null) Flexarr.usher else Flexarr.danger,
            textAlign = TextAlign.Center,
            // A wrong PIN is read out.
            modifier = Modifier.semantics { liveRegion = LiveRegionMode.Polite },
        )
        Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            ActionButton("Cancel", onClick = onCancel)
            ActionButton("Continue", onClick = ::submit, prominent = true, enabled = pin.length >= 4 && !checking)
        }
        onForgot?.let { TextButton(onClick = it) { Text("Forgot PIN?", fontSize = 15.sp, color = Flexarr.usher) } }
    }
}

/** The profile's picture, or its initial on its colour. */
@Composable
fun ProfileAvatar(model: AppModel, profile: Profile, size: Int) {
    Box(Modifier.size(size.dp).clip(CircleShape).background(ambientColor(profile.color)), contentAlignment = Alignment.Center) {
        Text(profile.initial, style = marquee((size * 0.46).toInt(), Flexarr.room).copy(fontWeight = FontWeight.ExtraBold))
        model.client?.imageUrl(profile.image, size * 3)?.let { url ->
            AsyncImage(model = url, contentDescription = null, contentScale = ContentScale.Crop, modifier = Modifier.fillMaxSize())
        }
    }
}
