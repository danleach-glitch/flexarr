package io.github.flexarr.ui.settings

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Notifications
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.semantics.LiveRegionMode
import androidx.compose.ui.semantics.liveRegion
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.github.flexarr.AppModel
import io.github.flexarr.core.BookListening
import io.github.flexarr.core.BookListeningUpdate
import io.github.flexarr.core.BookSpeed
import io.github.flexarr.core.FlexarrException
import io.github.flexarr.core.PlaybackPreferences
import io.github.flexarr.core.PodcastPreferencesUpdate
import io.github.flexarr.core.bookListening
import io.github.flexarr.core.forSaving
import io.github.flexarr.core.playbackPreferences
import io.github.flexarr.core.podcastPreferences
import io.github.flexarr.core.setBookListening
import io.github.flexarr.core.setPodcastPreferences
import io.github.flexarr.ui.components.Edge
import io.github.flexarr.ui.components.LocalNavigator
import io.github.flexarr.ui.components.PageTitle
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.RoomBackground
import kotlinx.coroutines.launch

/**
 * Settings › Podcasts and audiobooks: one speed, skip steps and smart rewind for both (a book with a speed of its own
 * keeps it), whether they join Continue watching, and what this phone downloads by itself. As the iPhone's
 * ListeningSettingsView and the web's page of the same name. From a server before 0.58 podcasts have only their speed.
 */
@Composable
fun ListeningSettingsScreen(model: AppModel, onNotifications: () -> Unit) {
    val client = model.client ?: return
    val navigator = LocalNavigator.current
    // Books' own settings, for a server whose podcast settings don't carry them (or without podcasts).
    var books by remember { mutableStateOf<BookListening?>(null) }
    var playback by remember { mutableStateOf<PlaybackPreferences?>(null) }
    var saveFailed by remember { mutableStateOf(false) }

    LaunchedEffect(model.credentials) {
        if (model.podcastPreferences == null) runCatching { client.podcastPreferences() }.onSuccess { model.podcastPreferences = it }
        if (model.podcastPreferences?.listening == null && model.hasAudiobooks) runCatching { client.bookListening() }.onSuccess { books = it }
        runCatching { client.playbackPreferences() }.onSuccess { playback = it }
    }

    val podcasts = model.podcastPreferences
    // The shared settings: the podcast settings' when the server shares them (0.58 on), or the books'.
    val listening = podcasts?.listening ?: books

    /** A book playing takes the new speed, steps and smart rewind. */
    fun booksFollow(saved: BookListening) {
        if (model.books.current != null) model.books.listening = saved
    }

    /** Shown at once; the server's answer follows (it keeps books in step). Downloads catch up when [reconciles]. */
    fun updatePodcasts(change: PodcastPreferencesUpdate, reconciles: Boolean = false) {
        val before = model.podcastPreferences ?: return
        saveFailed = false
        model.podcastPreferences = before.copy(
            autoDownload = change.autoDownload ?: before.autoDownload,
            keepEpisodes = change.keepEpisodes ?: before.keepEpisodes,
            speed = change.speed ?: before.speed,
            skipBackSeconds = change.skipBackSeconds ?: before.skipBackSeconds,
            skipForwardSeconds = change.skipForwardSeconds ?: before.skipForwardSeconds,
            smartRewind = change.smartRewind ?: before.smartRewind,
        )
        model.scope.launch {
            val saved = try {
                client.setPodcastPreferences(change)
            } catch (_: FlexarrException) {
                model.podcastPreferences = before
                saveFailed = true
                return@launch
            }
            model.podcastPreferences = saved
            saved.listening?.let(::booksFollow)
            if (reconciles) model.downloads.reconcilePodcasts()
        }
    }

    fun updateBooks(change: BookListeningUpdate) {
        val before = books ?: return
        saveFailed = false
        books = before.copy(
            speed = change.speed ?: before.speed,
            skipBackSeconds = change.skipBackSeconds ?: before.skipBackSeconds,
            skipForwardSeconds = change.skipForwardSeconds ?: before.skipForwardSeconds,
            smartRewind = change.smartRewind ?: before.smartRewind,
        )
        model.scope.launch {
            val saved = try {
                client.setBookListening(change)
            } catch (_: FlexarrException) {
                books = before
                saveFailed = true
                return@launch
            }
            books = saved
            booksFollow(saved)
        }
    }

    /** Saved as podcast settings where there are any (the server keeps books in step), otherwise as book settings. */
    fun update(podcastChange: PodcastPreferencesUpdate, bookChange: BookListeningUpdate) {
        if (model.podcastPreferences != null) updatePodcasts(podcastChange) else updateBooks(bookChange)
    }

    fun savePlayback(changed: PlaybackPreferences, over: PlaybackPreferences) {
        playback = changed
        model.scope.launch {
            try {
                client.setPlaybackPreferences(changed.forSaving())
                model.watchStateChanged() // Home's rows follow it.
            } catch (_: FlexarrException) {
                playback = over
            }
        }
    }

    RoomBackground(ambient = null) {
        LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 32.dp), verticalArrangement = Arrangement.spacedBy(28.dp)) {
            item { PageTitle("Podcasts and audiobooks", onBack = { navigator.back() }) }
            val speed = podcasts?.speed ?: books?.speed
            if (speed != null) item {
                Group(
                    header = "Your profile, on every device",
                    footer = "A book with a speed of its own keeps it. Smart rewind starts a little earlier after a pause.",
                ) {
                    Picker("Speed", BookSpeed.choices(speed), BookSpeed.snapped(speed), BookSpeed::label) {
                        update(PodcastPreferencesUpdate(speed = it), BookListeningUpdate(speed = it))
                    }
                    listening?.let { known ->
                        Divider()
                        Picker("Skip back", steps(known.skipBackSeconds), known.skipBackSeconds, ::seconds) {
                            update(PodcastPreferencesUpdate(skipBackSeconds = it), BookListeningUpdate(skipBackSeconds = it))
                        }
                        Divider()
                        Picker("Skip forward", steps(known.skipForwardSeconds), known.skipForwardSeconds, ::seconds) {
                            update(PodcastPreferencesUpdate(skipForwardSeconds = it), BookListeningUpdate(skipForwardSeconds = it))
                        }
                        Divider()
                        Toggle("Smart rewind", known.smartRewind, {
                            update(PodcastPreferencesUpdate(smartRewind = it), BookListeningUpdate(smartRewind = it))
                        })
                    }
                    // Only from a server that has the switch (0.58 and later).
                    playback?.let { current ->
                        current.listeningInContinue?.let { on ->
                            Divider()
                            Toggle("Books and podcasts in Continue watching", on, { savePlayback(current.copy(listeningInContinue = it), current) })
                        }
                    }
                }
            }
            if (saveFailed) item {
                Text(
                    "That didn’t save. Try again.",
                    fontSize = 15.sp,
                    color = Flexarr.danger,
                    modifier = Modifier.padding(horizontal = Edge).semantics { liveRegion = LiveRegionMode.Polite },
                )
            }
            podcasts?.let { known ->
                item {
                    Group(header = "On this phone", footer = "On Wi-Fi, the newest unplayed episodes of every show you follow. Played ones are deleted.") {
                        Toggle("Download new episodes", known.autoDownload, { updatePodcasts(PodcastPreferencesUpdate(autoDownload = it), reconciles = true) })
                        Divider()
                        Stepper("Keep the newest ${known.keepEpisodes}", known.keepEpisodes, 1..50, enabled = known.autoDownload) {
                            updatePodcasts(PodcastPreferencesUpdate(keepEpisodes = it), reconciles = true)
                        }
                    }
                }
                item {
                    Group(footer = "New episodes can come as notifications.") {
                        SettingsLink("Notifications", Icons.Filled.Notifications, value = null, onClick = onNotifications)
                    }
                }
            }
        }
    }
}

private fun steps(current: Int) = (BookListening.skipChoices + current).distinct().sorted()

private fun seconds(value: Int) = "$value seconds"
