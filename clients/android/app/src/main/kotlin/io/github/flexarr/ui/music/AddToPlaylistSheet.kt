package io.github.flexarr.ui.music

import io.github.flexarr.core.i18n.t
import io.github.flexarr.core.i18n.Words

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Close
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardCapitalization
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.github.flexarr.core.FlexarrClient
import io.github.flexarr.core.Format
import io.github.flexarr.core.PlaylistSummary
import io.github.flexarr.core.addToPlaylist
import io.github.flexarr.core.createPlaylist
import io.github.flexarr.core.playlists
import io.github.flexarr.ui.components.ActionButton
import io.github.flexarr.ui.components.pressable
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.Tokens
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.launch

/** What's being added to a playlist: a track, an album or an artist (the server turns albums and artists into their tracks). */
data class PlaylistAddition(val id: String, val title: String)

/** "12 tracks, 46m", as the web's describePlaylist. */
private fun describe(p: PlaylistSummary) = listOfNotNull(p.trackCountLabel, Format.runtime(p.durationSeconds)).joinToString(", ")

/** A new playlist's name is at most this long, as on the web. */
private const val NAME_LIMIT = 120

/**
 * Pick a playlist for the track (or album, or artist), or start a new one with it; tracks already in a playlist aren't
 * added twice. With no playlists yet, naming the first is all there is to do. The web's AddToPlaylistDialog. Phones
 * only: the TV leaves Add to playlist out.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun AddToPlaylistSheet(client: FlexarrClient, item: PlaylistAddition, onClose: () -> Unit) {
    var version by remember { mutableIntStateOf(0) }
    var playlists by remember { mutableStateOf<List<PlaylistSummary>?>(null) }
    var loadError by remember { mutableStateOf<String?>(null) }
    var name by remember { mutableStateOf("") }
    // What's being added to: a playlist's id, or NEW.
    var busy by remember { mutableStateOf<String?>(null) }
    var message by remember { mutableStateOf<String?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    val scope = rememberCoroutineScope()
    val nameFocus = remember { FocusRequester() }

    LaunchedEffect(version) {
        try {
            playlists = client.playlists()
            loadError = null
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            loadError = e.message ?: t("Couldn’t load your playlists.")
        }
    }
    val none = playlists?.isEmpty() == true
    LaunchedEffect(none) { if (none) runCatching { nameFocus.requestFocus() } }

    fun run(target: String, work: suspend () -> String) {
        if (busy != null) return
        busy = target
        error = null
        scope.launch {
            try {
                message = work()
                version++
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                error = e.message ?: t("Something went wrong.")
            } finally {
                busy = null
            }
        }
    }

    fun add(p: PlaylistSummary) = run(p.id) {
        if (client.addToPlaylist(p.id, listOf(item.id)) == 0) t("Already in {playlist}.", "playlist" to p.name) else t("Added to {playlist}.", "playlist" to p.name)
    }

    fun create() {
        val trimmed = name.trim()
        if (trimmed.isEmpty()) return
        run(NEW) {
            val created = client.createPlaylist(trimmed, listOf(item.id))
            name = ""
            t("Made {playlist} with it.", "playlist" to created.name)
        }
    }

    ModalBottomSheet(onDismissRequest = onClose, sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true), containerColor = Flexarr.room) {
        Column(Modifier.navigationBarsPadding().imePadding()) {
            Row(Modifier.fillMaxWidth().padding(start = 20.dp, end = 8.dp), verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f)) {
                    Text(t(Words.addToPlaylist), fontSize = 17.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen)
                    Text(item.title, fontSize = 13.sp, color = Flexarr.usher, maxLines = 1, overflow = TextOverflow.Ellipsis)
                }
                IconButton(onClick = onClose) { Icon(Icons.Filled.Close, contentDescription = t("Close"), tint = Flexarr.screen) }
            }
            loadError?.let { Text(it, fontSize = 13.sp, color = Flexarr.danger, modifier = Modifier.padding(horizontal = 20.dp, vertical = 8.dp)) }
            val list = playlists
            if (list == null && loadError == null) {
                Box(Modifier.fillMaxWidth().padding(24.dp), contentAlignment = Alignment.Center) {
                    CircularProgressIndicator(Modifier.size(22.dp), color = Flexarr.usher, strokeWidth = 2.dp)
                }
            } else if (!list.isNullOrEmpty()) {
                LazyColumn(Modifier.heightIn(max = 420.dp), contentPadding = PaddingValues(horizontal = 8.dp, vertical = 8.dp)) {
                    items(list, key = { it.id }) { p -> PlaylistChoice(p, busy = busy == p.id, enabled = busy == null) { add(p) } }
                }
            }
            Row(
                Modifier.fillMaxWidth().padding(horizontal = 20.dp, vertical = 12.dp),
                horizontalArrangement = Arrangement.spacedBy(12.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                OutlinedTextField(
                    name, { if (it.length <= NAME_LIMIT) name = it },
                    placeholder = { Text(if (none) t("Name your first playlist") else t("New playlist")) },
                    singleLine = true,
                    keyboardOptions = KeyboardOptions(capitalization = KeyboardCapitalization.Sentences, imeAction = ImeAction.Done),
                    keyboardActions = KeyboardActions(onDone = { create() }),
                    modifier = Modifier.weight(1f).focusRequester(nameFocus),
                )
                if (busy == NEW) {
                    Box(Modifier.width(88.dp), contentAlignment = Alignment.Center) { CircularProgressIndicator(Modifier.size(22.dp), color = Flexarr.usher, strokeWidth = 2.dp) }
                } else {
                    ActionButton(t("Create"), onClick = ::create, enabled = busy == null && name.isNotBlank())
                }
            }
            message?.let { Text(it, fontSize = 14.sp, color = Flexarr.usher, modifier = Modifier.padding(start = 20.dp, end = 20.dp, bottom = 12.dp)) }
            error?.let { Text(it, fontSize = 14.sp, color = Flexarr.danger, modifier = Modifier.padding(start = 20.dp, end = 20.dp, bottom = 12.dp)) }
        }
    }
}

private const val NEW = "new"

/** One of the profile's playlists: its picture, name, and how many tracks and how long. Tapping adds to it. */
@Composable
private fun PlaylistChoice(playlist: PlaylistSummary, busy: Boolean, enabled: Boolean, onClick: () -> Unit) {
    Row(
        Modifier.fillMaxWidth().clip(RoundedCornerShape(Tokens.radiusControl)).pressable(onClick, enabled = enabled).padding(horizontal = 12.dp, vertical = 8.dp),
        horizontalArrangement = Arrangement.spacedBy(14.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        PlaylistArtwork(playlist, Modifier.width(56.dp))
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
            Text(playlist.name, fontSize = 16.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen, maxLines = 1, overflow = TextOverflow.Ellipsis)
            Text(describe(playlist), fontSize = 13.sp, color = Flexarr.usher, maxLines = 1)
        }
        if (busy) CircularProgressIndicator(Modifier.size(20.dp), color = Flexarr.usher, strokeWidth = 2.dp)
    }
}
