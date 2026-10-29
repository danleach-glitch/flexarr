package io.github.flexarr.ui.profiles

import androidx.compose.animation.AnimatedContent
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
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
import io.github.flexarr.core.Profile
import io.github.flexarr.ui.components.ActionButton
import io.github.flexarr.ui.components.pressable
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.RoomBackground
import io.github.flexarr.ui.theme.ambientColor
import io.github.flexarr.ui.theme.marquee
import kotlinx.coroutines.launch

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
 * again).
 */
@Composable
fun ProfilePicker(model: AppModel, marksCurrent: Boolean = false, onChosen: () -> Unit = {}) {
    val scope = rememberCoroutineScope()
    var asking by remember { mutableStateOf<Profile?>(null) }
    var switching by remember { mutableStateOf<String?>(null) }
    var error by remember { mutableStateOf<String?>(null) }

    suspend fun choose(profile: Profile, pin: String?): Boolean {
        switching = profile.id
        try {
            model.chooseProfile(profile, pin)
            onChosen()
            return true
        } catch (e: Exception) {
            error = e.message
            return false
        } finally {
            switching = null
        }
    }

    AnimatedContent(asking, label = "profiles") { pinFor ->
        if (pinFor != null) {
            PinEntry(
                profile = pinFor,
                model = model,
                error = error,
                onEdit = { error = null },
                onCancel = {
                    asking = null
                    error = null
                },
                onSubmit = { pin -> choose(pinFor, pin) },
            )
        } else {
            Column(
                Modifier.fillMaxWidth(),
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
                        Column(
                            Modifier.pressable(
                                enabled = switching == null && !current,
                                onClick = {
                                    error = null
                                    if (profile.hasPin) asking = profile else scope.launch { choose(profile, null) }
                                },
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
                            }
                            Column(horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(2.dp)) {
                                Text(profile.name, fontSize = 17.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen, maxLines = 1)
                                Text(
                                    if (current) "Watching now" else if (profile.hasPin) "PIN" else " ",
                                    fontSize = 12.sp,
                                    fontWeight = FontWeight.Medium,
                                    color = Flexarr.usher,
                                )
                            }
                        }
                    }
                }
                error?.let { Text(it, fontSize = 15.sp, fontWeight = FontWeight.Medium, color = Flexarr.danger, textAlign = TextAlign.Center, modifier = Modifier.padding(horizontal = 32.dp)) }
            }
        }
    }
}

/** A 4 to 8 digit PIN, on the number pad. */
@Composable
private fun PinEntry(
    profile: Profile,
    model: AppModel,
    error: String?,
    onEdit: () -> Unit,
    onCancel: () -> Unit,
    onSubmit: suspend (String) -> Boolean,
) {
    val scope = rememberCoroutineScope()
    var pin by remember { mutableStateOf("") }
    var checking by remember { mutableStateOf(false) }
    val focus = remember { FocusRequester() }
    LaunchedEffect(Unit) { focus.requestFocus() }

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
        ProfileAvatar(model, profile, size = 84)
        Text("Enter ${profile.name}’s PIN", style = marquee(32), textAlign = TextAlign.Center)
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
            modifier = Modifier.width(220.dp).clip(RoundedCornerShape(12.dp)).background(Flexarr.seat).padding(vertical = 12.dp).focusRequester(focus),
        )
        Text(
            error ?: if (checking) "Checking" else " ",
            fontSize = 15.sp,
            fontWeight = FontWeight.Medium,
            color = if (error == null) Flexarr.usher else Flexarr.danger,
            textAlign = TextAlign.Center,
        )
        Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            ActionButton("Cancel", onClick = onCancel)
            ActionButton("Continue", onClick = ::submit, prominent = true, enabled = pin.length >= 4 && !checking)
        }
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
