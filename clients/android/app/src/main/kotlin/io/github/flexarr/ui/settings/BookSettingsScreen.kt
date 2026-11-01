package io.github.flexarr.ui.settings

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import io.github.flexarr.AppModel
import io.github.flexarr.core.BookListening
import io.github.flexarr.core.BookListeningUpdate
import io.github.flexarr.core.BookSpeed
import io.github.flexarr.core.FlexarrException
import io.github.flexarr.core.bookListening
import io.github.flexarr.core.setBookListening
import io.github.flexarr.ui.components.LocalNavigator
import io.github.flexarr.ui.components.PageTitle
import io.github.flexarr.ui.components.StatusView
import io.github.flexarr.ui.theme.RoomBackground
import kotlinx.coroutines.launch

/**
 * How the profile listens to audiobooks, in every app: the speed for books without their own, the skip steps, and smart
 * rewind. As the web's Settings › Audiobooks (the server's listening settings, not its online details).
 */
@Composable
fun BookSettingsScreen(model: AppModel) {
    val client = model.client ?: return
    val navigator = LocalNavigator.current
    var listening by remember { mutableStateOf<BookListening?>(null) }
    var failed by remember { mutableStateOf<String?>(null) }
    var attempt by remember { mutableStateOf(0) }

    LaunchedEffect(model.credentials, attempt) {
        try {
            listening = client.bookListening()
            failed = null
        } catch (e: FlexarrException) {
            failed = e.message
        }
    }

    /** Shown at once; the server's answer follows, and a book playing takes the new steps. */
    fun update(change: BookListeningUpdate) {
        listening?.let { known ->
            listening = known.copy(
                speed = change.speed ?: known.speed,
                skipBackSeconds = change.skipBackSeconds ?: known.skipBackSeconds,
                skipForwardSeconds = change.skipForwardSeconds ?: known.skipForwardSeconds,
                smartRewind = change.smartRewind ?: known.smartRewind,
            )
        }
        model.scope.launch {
            val saved = try {
                client.setBookListening(change)
            } catch (_: FlexarrException) {
                return@launch
            }
            listening = saved
            if (model.books.current != null) model.books.listening = saved
        }
    }

    RoomBackground(ambient = null) {
        LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 32.dp), verticalArrangement = Arrangement.spacedBy(28.dp)) {
            item { PageTitle("Audiobooks", onBack = { navigator.back() }) }
            val known = listening
            when {
                known != null -> item {
                    Group(header = "Your profile, in every app", footer = "Speed is for books without their own; the player sets a book’s. Smart rewind starts a little earlier after a pause.") {
                        // A speed saved elsewhere that isn't one of the choices is offered too.
                        val speeds = (BookSpeed.presets + BookSpeed.snapped(known.speed)).distinct().sorted()
                        Picker("Speed", speeds, BookSpeed.snapped(known.speed), BookSpeed::label) { update(BookListeningUpdate(speed = it)) }
                        Divider()
                        Picker("Skip back", BookListening.skipChoices, known.skipBackSeconds, ::seconds) { update(BookListeningUpdate(skipBackSeconds = it)) }
                        Divider()
                        Picker("Skip forward", BookListening.skipChoices, known.skipForwardSeconds, ::seconds) { update(BookListeningUpdate(skipForwardSeconds = it)) }
                        Divider()
                        Toggle("Smart rewind", known.smartRewind, { update(BookListeningUpdate(smartRewind = it)) })
                    }
                }
                failed != null -> item { StatusView(failed, retry = { attempt++ }) }
                else -> item { StatusView() }
            }
        }
    }
}

private fun seconds(value: Int) = "$value seconds"
