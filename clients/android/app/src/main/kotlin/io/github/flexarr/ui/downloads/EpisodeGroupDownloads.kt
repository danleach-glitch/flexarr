package io.github.flexarr.ui.downloads

import io.github.flexarr.core.i18n.t
import io.github.flexarr.core.i18n.Words

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ErrorOutline
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.Stable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.github.flexarr.AppModel
import io.github.flexarr.core.EpisodeGroupRules
import io.github.flexarr.core.EpisodeGroupState
import io.github.flexarr.core.ItemSummary
import io.github.flexarr.core.LocalDownload
import io.github.flexarr.core.MediaKind
import io.github.flexarr.ui.components.DownloadedIcon
import io.github.flexarr.ui.components.pressable
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.FlexarrIcons
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch

// A show's or a season's episodes downloaded together, as on iPhone: Download on a show's page (every episode of every
// season) and the "Download Season 2" link beside a show's season pills. Each episode is a download of its own; the
// server gets them ready one at a time, so asking for many at once is fine.

/**
 * Episodes being asked of the server a batch at a time (a show's, a season's), on the main thread. Stop or Remove on any
 * group holding one of a batch's episodes halts it: nothing more is asked for, and the one being asked as it was
 * stopped is deleted when it comes, so Stop needs pressing only once.
 */
internal object EpisodeBatches {
    class Batch(val ids: Set<String>) {
        var halted = false
    }

    private val running = mutableListOf<Batch>()

    fun start(ids: Set<String>): Batch = Batch(ids).also { running += it }

    fun end(batch: Batch) {
        running -= batch
    }

    fun halt(ids: Set<String>) {
        running.filter { batch -> batch.ids.any { it in ids } }.forEach { it.halted = true }
    }
}

/** What Stop or Remove acts on: the downloads (worked out when it's answered), and every episode of the group. */
internal class GroupTarget(val downloads: () -> List<LocalDownload>, val episodes: () -> List<ItemSummary>)

/** The one question a group's control can be asking. */
internal sealed interface GroupQuestion {
    /** "Download 12 episodes of Severance?", with the episodes it would ask for. */
    data class Confirm(val episodes: List<ItemSummary>) : GroupQuestion
    data class Stop(val target: GroupTarget) : GroupQuestion
    data class Remove(val target: GroupTarget) : GroupQuestion
    data class Problem(val message: String) : GroupQuestion
}

/**
 * What a group's taps do, and the questions they ask: download what's missing, stop what's coming, remove what's here.
 * [groupId] is the show's or season's id, remembered as started ([io.github.flexarr.downloads.DownloadStore.markStarted])
 * once its Download is pressed. One question shows at a time, from one place ([Dialogs]).
 */
@Stable
internal class GroupDownloads(private val model: AppModel, private val groupId: String, private val showTitle: String? = null) {
    /** Episodes are still being asked of the server. */
    var queueing by mutableStateOf(false)
        private set
    var question by mutableStateOf<GroupQuestion?>(null)

    val started: Boolean get() = model.downloads.isStarted(groupId)

    /**
     * Downloads every one of [episodes] not here or on its way (one that failed starts again), one after another, at the
     * size chosen in Settings. Carries on past a failure and says what the first was at the end. Stop or Remove on a
     * group holding these episodes ends it ([EpisodeBatches]).
     */
    fun download(episodes: List<ItemSummary>) {
        val store = model.downloads
        val wanted = EpisodeGroupRules.toQueue(episodes, store::entry)
        if (wanted.isEmpty()) return
        store.markStarted(groupId)
        queueing = true
        val batch = EpisodeBatches.start(wanted.map { it.id }.toSet())
        model.scope.launch {
            var first: String? = null
            try {
                for (episode in wanted) {
                    if (batch.halted) break
                    try {
                        store.download(episode)
                    } catch (e: CancellationException) {
                        throw e
                    } catch (e: Exception) {
                        first = first ?: (e.message ?: t("Couldn’t download this"))
                    }
                    // Stopped while this one was being asked for: it goes too.
                    if (batch.halted) store.entry(episode.id)?.let { store.delete(it.id) }
                }
            } finally {
                EpisodeBatches.end(batch)
                queueing = false
            }
            if (batch.halted) return@launch
            first?.let { question = GroupQuestion.Problem(it) }
        }
    }

    /** Downloading [episodes] once asked: "Download 12 episodes of Severance?", counting only those it would fetch. */
    fun confirmDownload(episodes: List<ItemSummary>) {
        val wanted = EpisodeGroupRules.toQueue(episodes, model.downloads::entry)
        if (wanted.isNotEmpty()) question = GroupQuestion.Confirm(wanted)
    }

    /**
     * Stop (only what isn't here yet) or Remove (everything): ends any batch still asking for these episodes, forgets
     * every group holding them as started, and deletes. Run after the question has gone, so it closes on the first tap.
     */
    private fun end(target: GroupTarget, onlyUnfinished: Boolean) {
        val store = model.downloads
        // Posted, not run at once (the app's scope is immediate), so the dialog has gone first.
        model.scope.launch(Dispatchers.Main) {
            val downloads = target.downloads()
            val episodes = (target.episodes() + downloads.map { it.item }).distinctBy { it.id }
            EpisodeBatches.halt(episodes.map { it.id }.toSet())
            store.forgetStarted(EpisodeGroupRules.groupsHolding(episodes) + groupId)
            (if (onlyUnfinished) EpisodeGroupRules.toStop(downloads) else downloads).forEach { store.delete(it.id) }
        }
    }

    @Composable
    fun Dialogs() {
        val dismiss = { question = null }
        when (val asking = question) {
            null -> Unit
            is GroupQuestion.Confirm -> AlertDialog(
                onDismissRequest = dismiss,
                title = { Text(t("Download {count, plural, one {# episode} other {# episodes}} of {show}?", "count" to asking.episodes.size, "show" to showTitle.orEmpty())) },
                confirmButton = { TextButton(onClick = { dismiss(); download(asking.episodes) }) { Text(t(Words.download), color = Flexarr.screen) } },
                dismissButton = { TextButton(onClick = dismiss) { Text(t(Words.cancel), color = Flexarr.screen) } },
                containerColor = Flexarr.seat,
            )
            is GroupQuestion.Stop -> StopDownloadsDialog(onStop = { end(asking.target, onlyUnfinished = true) }, onDismiss = dismiss)
            is GroupQuestion.Remove -> RemoveDownloadsDialog(onRemove = { end(asking.target, onlyUnfinished = false) }, onDismiss = dismiss)
            is GroupQuestion.Problem -> AlertDialog(
                onDismissRequest = dismiss,
                title = { Text(t("Couldn’t download this")) },
                text = { Text(asking.message) },
                confirmButton = { TextButton(onClick = dismiss) { Text(t("OK"), color = Flexarr.screen) } },
                containerColor = Flexarr.seat,
            )
        }
    }
}

/** The episodes of a season (or a list of them) that are episodes: no extras. */
private fun List<ItemSummary>.episodesOnly() = filter { it.kind == MediaKind.Episode }

/**
 * Download on a show's page: every episode of every season, watched or not. It finds the episodes when tapped (with
 * [loadEpisodes], the page's own seasons), or straight away when some of the show is here already, so it can say how
 * far it's got: "Download show" (asking first, with how many), "3 of 10" with a ring (tap to stop), "Downloaded" (tap to
 * remove), or "Couldn’t download" (tap to try again).
 */
@Composable
fun ShowDownloadAction(model: AppModel, show: ItemSummary, loadEpisodes: suspend () -> List<ItemSummary>, modifier: Modifier = Modifier) {
    val store = model.downloads
    val group = remember(show.id) { GroupDownloads(model, show.id, showTitle = show.title) }
    val scope = rememberCoroutineScope()
    var episodes by remember(show.id) { mutableStateOf<List<ItemSummary>?>(null) }
    var loading by remember(show.id) { mutableStateOf(false) }
    // Pressed, and finding the episodes before asking: further presses are ignored, so a second load can't ask again
    // after the first question was answered. Set at once on the press, before anything is launched.
    var busy by remember(show.id) { mutableStateOf(false) }
    val held = store.episodesOfShow(show.id)

    suspend fun load(): List<ItemSummary>? {
        episodes?.let { return it }
        if (loading) return null
        loading = true
        return try {
            loadEpisodes().episodesOnly().also { episodes = it }
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            group.question = GroupQuestion.Problem(e.message ?: t("Couldn’t download this"))
            null
        } finally {
            loading = false
        }
    }

    // Some of the show is here already: its episodes, so the button can say how far it's got.
    val hasAny = held.isNotEmpty()
    LaunchedEffect(show.id, hasAny) { if (hasAny && episodes == null) load() }

    val known = episodes
    val entries: List<LocalDownload?> = known?.map { store.entry(it.id) } ?: held
    val state = EpisodeGroupRules.state(entries, started = group.started, queueing = group.queueing || loading, known = known != null, progress = store::progress)
    // Everything of the show here: its episodes' downloads, and any the list no longer has.
    val all = { (known.orEmpty().mapNotNull { store.entry(it.id) } + store.episodesOfShow(show.id)).distinctBy { it.id } }
    val target = GroupTarget(all) { known.orEmpty() + store.episodesOfShow(show.id).map { it.item } }

    val label = when (state) {
        // Not the plain word: beside "Play S2E3" it would read as downloading that episode.
        is EpisodeGroupState.NotDownloaded -> t("Download show")
        is EpisodeGroupState.Downloading -> if (state.total == 0) t("Getting ready") else t("{done} of {total}", "done" to state.done, "total" to state.total)
        EpisodeGroupState.Downloaded -> t("Downloaded")
        is EpisodeGroupState.Failed -> t("Couldn’t download")
    }
    DownloadActionFace(
        label,
        groupDescription(state),
        onClick = {
            if (!busy && group.question == null) when (state) {
                is EpisodeGroupState.NotDownloaded, is EpisodeGroupState.Failed -> {
                    busy = true
                    scope.launch {
                        try {
                            val found = load()
                            // Asked only if nothing else is being asked meanwhile.
                            if (found != null && group.question == null) {
                                if (state is EpisodeGroupState.Failed) group.download(found) else group.confirmDownload(found)
                            }
                        } finally {
                            busy = false
                        }
                    }
                }
                is EpisodeGroupState.Downloading -> group.question = GroupQuestion.Stop(target)
                EpisodeGroupState.Downloaded -> group.question = GroupQuestion.Remove(target)
            }
        },
        modifier,
    ) {
        GroupStateIcon(state, size = 22.dp)
    }
    group.Dialogs()
}

/**
 * "Download Season 2", beside a show's season pills (or its one season's heading): every episode of the season, at
 * once. Then "Downloading Season 2" with a ring (tap to stop), "Season 2 downloaded" (tap to remove), or
 * "Couldn’t download" (tap to try again).
 */
@Composable
fun SeasonDownloadLink(model: AppModel, season: ItemSummary, episodes: List<ItemSummary>, modifier: Modifier = Modifier) {
    val store = model.downloads
    val group = remember(season.id) { GroupDownloads(model, season.id) }
    val list = episodes.episodesOnly()
    if (list.isEmpty()) return
    val state = EpisodeGroupRules.state(list.map { store.entry(it.id) }, started = group.started, queueing = group.queueing, progress = store::progress)
    val here = GroupTarget({ list.mapNotNull { store.entry(it.id) } }) { list }
    val label = when (state) {
        is EpisodeGroupState.NotDownloaded -> t("Download {season}", "season" to season.title)
        is EpisodeGroupState.Downloading -> t("Downloading {season}", "season" to season.title)
        EpisodeGroupState.Downloaded -> t("{season} downloaded", "season" to season.title)
        is EpisodeGroupState.Failed -> t("Couldn’t download")
    }
    Row(
        modifier
            .pressable({
                when (state) {
                    is EpisodeGroupState.NotDownloaded, is EpisodeGroupState.Failed -> group.download(list)
                    is EpisodeGroupState.Downloading -> group.question = GroupQuestion.Stop(here)
                    EpisodeGroupState.Downloaded -> group.question = GroupQuestion.Remove(here)
                }
            })
            .semantics { contentDescription = label }
            .padding(vertical = 12.dp, horizontal = 4.dp),
        horizontalArrangement = Arrangement.spacedBy(6.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        if (state !is EpisodeGroupState.NotDownloaded) GroupStateIcon(state, size = 16.dp)
        Text(label, fontSize = 15.sp, fontWeight = FontWeight.SemiBold, color = if (state is EpisodeGroupState.Failed) Flexarr.danger else Flexarr.usher, maxLines = 1, overflow = TextOverflow.Ellipsis)
    }
    group.Dialogs()
}

/** A group's state at a glance, like a single download's: an arrow, a ring, the filled green arrow, an exclamation. */
@Composable
private fun GroupStateIcon(state: EpisodeGroupState, size: androidx.compose.ui.unit.Dp) {
    Box(Modifier.size(size), contentAlignment = Alignment.Center) {
        when (state) {
            is EpisodeGroupState.NotDownloaded -> Icon(FlexarrIcons.download, contentDescription = null, tint = Flexarr.screen)
            is EpisodeGroupState.Downloading -> DownloadRing(state.fraction.toFloat())
            EpisodeGroupState.Downloaded -> Icon(DownloadedIcon, contentDescription = null, tint = Color.Unspecified)
            is EpisodeGroupState.Failed -> Icon(Icons.Filled.ErrorOutline, contentDescription = null, tint = Flexarr.danger)
        }
    }
}

/** What a screen reader says for a show's Download. */
private fun groupDescription(state: EpisodeGroupState): String = when (state) {
    is EpisodeGroupState.NotDownloaded -> t("Download show")
    is EpisodeGroupState.Downloading -> if (state.total == 0) t("Getting ready") else t("{done} of {total}", "done" to state.done, "total" to state.total)
    EpisodeGroupState.Downloaded -> t("Downloaded")
    is EpisodeGroupState.Failed -> t("Couldn’t download")
}
