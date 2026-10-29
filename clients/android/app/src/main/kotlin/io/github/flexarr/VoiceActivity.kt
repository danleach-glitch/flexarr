package io.github.flexarr

import android.app.SearchManager
import android.content.Intent
import android.os.Bundle
import android.provider.MediaStore
import androidx.activity.ComponentActivity
import androidx.lifecycle.lifecycleScope
import io.github.flexarr.audio.VoiceSearch
import io.github.flexarr.logging.FlexarrLog
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeoutOrNull

private val log = FlexarrLog("voice")

/**
 * The Assistant on the phone, as Siri and the App Intents are on iPhone (FlexarrIntents.swift). Nothing of its own shows:
 * it works out what was asked and opens the app there.
 *
 * - "Hey Google, play Heat on Flexarr" (Android's play-from-search): the best match plays, as [VoiceSearch] picks it. A
 *   film plays from where it was left, a show its next episode, a podcast the episode to hear next ("the latest …" its
 *   newest), music an artist, album, playlist or track, "my Up Next" Up Next and "continue watching" Continue Watching.
 * - "Search Flexarr for Fargo" (the Assistant's in-app search, and App Actions' GET_THING): Search, the words typed in.
 * - "Open Continue Watching in Flexarr" (App Actions' OPEN_APP_FEATURE, the app shortcuts' names): that screen.
 *
 * This is open to every app (the Assistant must reach it). Asked to play by the Assistant itself, it plays; asked by any
 * other app, it only opens the title's page (or Search), as [AppLink.received] treats a link from outside.
 */
class VoiceActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        if (savedInstanceState != null) return finish()
        val intent = intent
        when (intent.action) {
            MediaStore.INTENT_ACTION_MEDIA_PLAY_FROM_SEARCH -> play(intent.getStringExtra(SearchManager.QUERY), intent.extras)
            SEARCH_ACTION, Intent.ACTION_SEARCH -> search(intent.getStringExtra(SearchManager.QUERY))
            OPEN_FEATURE -> open(feature(intent.getStringExtra(FEATURE)))
            else -> finish()
        }
    }

    /**
     * Whether the Google app (the Assistant) sent this. The referrer is the app that started this one unless the intent
     * names one itself, which any app could: then it doesn't count.
     */
    private val fromAssistant: Boolean
        get() = !intent.hasExtra(Intent.EXTRA_REFERRER) && !intent.hasExtra(Intent.EXTRA_REFERRER_NAME) && referrer?.host in assistants

    private fun play(said: String?, extras: Bundle?) {
        val model = appModel
        val client = model.client ?: return open(AppLink.Home)
        val trusted = fromAssistant
        lifecycleScope.launch {
            val choice = withTimeoutOrNull(TIMEOUT_MS) {
                runCatching { VoiceSearch(model).choose(client, said, extras, video = true) }
                    .onFailure { log.warning("Couldn't work out what to play for the Assistant", it) }
                    .getOrNull()
            }
            log.info("Asked to play${if (trusted) "" else " (not by the Assistant)"}: ${choice?.let { it::class.simpleName } ?: "nothing found"}")
            when (choice) {
                null -> search(said)
                is VoiceSearch.Choice.Video -> open(if (trusted) AppLink.Play(choice.item.id) else AppLink.Item(choice.item.id))
                is VoiceSearch.Choice.Episode -> open(if (trusted) AppLink.PodcastEpisode(choice.episode.id) else AppLink.Podcast(choice.episode.podcastId))
                is VoiceSearch.Choice.Tracks -> if (trusted) {
                    model.music.play(choice.tracks, start = choice.start)
                    open(AppLink.Home)
                } else {
                    search(said)
                }
                VoiceSearch.Choice.Resume -> {
                    if (trusted) {
                        if (model.music.current != null) model.music.resume() else if (!model.podcasts.isPlaying) model.podcasts.togglePlayPause()
                    }
                    open(AppLink.Home)
                }
            }
        }
    }

    private fun search(said: String?) {
        said?.trim()?.takeIf { it.isNotEmpty() }?.let { appModel.pendingSearch = it }
        open(AppLink.Search)
    }

    /** Opens the app at [link] (marked as the app's own: the decision whether to play was made here) and goes. */
    private fun open(link: AppLink) {
        startActivity(link.intent(this))
        finish()
    }

    /** An app shortcut's id, as App Actions passes it, to the screen it opens. */
    private fun feature(id: String?): AppLink = when (id) {
        "search" -> AppLink.Search
        "downloads" -> AppLink.Downloads
        else -> AppLink.Home
    }

    companion object {
        /** The Assistant's in-app search, and what App Actions' GET_THING sends here (shortcuts.xml). */
        private const val SEARCH_ACTION = "com.google.android.gms.actions.SEARCH_ACTION"
        /** App Actions' OPEN_APP_FEATURE, as shortcuts.xml sends it, with the shortcut's id as [FEATURE]. */
        private const val OPEN_FEATURE = "io.github.flexarr.OPEN_FEATURE"
        private const val FEATURE = "feature"
        private const val TIMEOUT_MS = 10_000L

        /** The Google app (the Assistant on phones) and the Assistant in cars. */
        private val assistants = setOf("com.google.android.googlequicksearchbox", "com.google.android.carassistant")
    }
}
