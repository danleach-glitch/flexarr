package io.github.flexarr.audio

import android.os.Bundle
import android.provider.MediaStore
import io.github.flexarr.AppModel
import io.github.flexarr.core.FlexarrClient
import io.github.flexarr.core.ItemSummary
import io.github.flexarr.core.MediaKind
import io.github.flexarr.core.Podcast
import io.github.flexarr.core.PodcastEpisode
import io.github.flexarr.core.PodcastEpisodeSort
import io.github.flexarr.core.SpokenRequest
import io.github.flexarr.core.continueListening
import io.github.flexarr.core.matchingSpoken
import io.github.flexarr.core.newPodcastEpisodes
import io.github.flexarr.core.playlist
import io.github.flexarr.core.playlists
import io.github.flexarr.core.podcast
import io.github.flexarr.core.podcastEpisodes
import io.github.flexarr.core.podcasts
import io.github.flexarr.core.tracks
import io.github.flexarr.core.upNext

/**
 * What a spoken request means in this library ("Hey Google, play The Rest Is History on Flexarr"), as the iPhone's App
 * Intents work it out (FlexarrIntents.swift): a followed podcast plays the episode to hear next (or its newest, asked for
 * "the latest"), Up Next its first, music an artist, album, playlist or track, and a film or show (on the phone only)
 * its next episode. Android Auto ([CarLibrary]) and the Assistant on the phone ([io.github.flexarr.VoiceActivity]) both
 * ask here.
 */
class VoiceSearch(private val model: AppModel) {
    /** What to play for a request. */
    sealed interface Choice {
        /** Carry on with what's already loaded. */
        data object Resume : Choice
        data class Episode(val episode: PodcastEpisode) : Choice
        /** [tracks] from [start], known by [list] (`playlist-<id>`, `artist-<id>`, `album-<id>`, `track-<id>`). */
        data class Tracks(val list: String, val tracks: List<ItemSummary>, val start: Int) : Choice
        /** A film, show or episode, for the player (a show plays its next episode). */
        data class Video(val item: ItemSummary) : Choice
    }

    /**
     * The best match for [said], with Android's hints about what kind of thing was asked for ([extras]: `EXTRA_MEDIA_FOCUS`
     * and the artist, album, title and playlist it names), or null when nothing matches. [video]: films and shows count
     * too (not in the car).
     */
    suspend fun choose(client: FlexarrClient, said: String?, extras: Bundle?, video: Boolean): Choice? =
        when (val request = SpokenRequest.parse(said)) {
            SpokenRequest.Anything -> anything(client)
            SpokenRequest.UpNext -> client.upNext().firstOrNull()?.let(Choice::Episode)
            SpokenRequest.Continue -> if (video) continueWatching(client) else continueListening(client)
            is SpokenRequest.Latest -> podcast(client, request.term)?.let { latest(client, it) }?.let(Choice::Episode)
            is SpokenRequest.Title -> title(client, request.term, Focus.of(extras), video)
        }

    /** "Play Flexarr", "play music": what was playing; else Up Next, else the episode part-heard, else what's new. */
    private suspend fun anything(client: FlexarrClient): Choice? {
        if (model.music.current != null || model.podcasts.current != null) return Choice.Resume
        val episode = client.upNext().firstOrNull()
            ?: client.continueListening(take = 1).firstOrNull()
            ?: client.newPodcastEpisodes(unplayed = true, take = 1).firstOrNull()
        return episode?.let(Choice::Episode)
    }

    private suspend fun continueListening(client: FlexarrClient): Choice? =
        client.continueListening(take = 1).firstOrNull()?.let(Choice::Episode) ?: anything(client)

    private suspend fun continueWatching(client: FlexarrClient): Choice? =
        client.continueWatching(take = 1).firstOrNull()?.let(Choice::Video) ?: continueListening(client)

    /** What the words name, the closest name of any kind winning, and podcasts, then video, then music on a tie. */
    private suspend fun title(client: FlexarrClient, said: String, focus: Focus, video: Boolean): Choice? {
        val term = focus.term ?: said
        val candidates = mutableListOf<Candidate>()
        if (focus == Focus.Anything) podcast(client, term)?.let { candidates += Candidate(it.title) { nextToHear(client, it)?.let(Choice::Episode) } }
        if (focus == Focus.Anything || focus.isPlaylist) {
            matchingSpoken(term, client.playlists()) { it.name }.firstOrNull()?.let { playlist ->
                candidates += Candidate(playlist.name) { client.playlist(playlist.id).tracks.map { it.track }.takeIf { it.isNotEmpty() }?.let { Choice.Tracks("playlist-${playlist.id}", it, 0) } }
            }
        }
        val found = client.search(term)
        if (video && focus == Focus.Anything) {
            (found.titles.filter { it.kind == MediaKind.Movie || it.kind == MediaKind.Series } + found.episodes).forEach { item ->
                candidates += Candidate(item.title) { Choice.Video(item) }
            }
        }
        if (focus == Focus.Anything || focus.isArtist) {
            found.titles.filter { it.kind == MediaKind.Artist }.forEach { artist ->
                candidates += Candidate(artist.title) { client.tracks(artist.id).takeIf { it.isNotEmpty() }?.let { Choice.Tracks("artist-${artist.id}", it, 0) } }
            }
        }
        if (focus == Focus.Anything || focus.isAlbum) {
            found.albums.forEach { album ->
                candidates += Candidate(album.title) { client.tracks(album.id).takeIf { it.isNotEmpty() }?.let { Choice.Tracks("album-${album.id}", it, 0) } }
            }
        }
        if (focus == Focus.Anything || focus.isSong) {
            found.tracks.forEach { match ->
                candidates += Candidate(match.item.title) { track(client, match.item) }
            }
        }
        // The closest that has something to play.
        for (candidate in matchingSpoken(term, candidates) { it.name }) {
            candidate.choice()?.let { return it }
        }
        return null
    }

    /** A track plays on through the rest of its album, as choosing it there would. */
    suspend fun track(client: FlexarrClient, track: ItemSummary): Choice {
        val album = track.parentId?.let { id -> runCatching { client.tracks(id) }.getOrNull()?.takeIf { tracks -> tracks.any { it.id == track.id } } }
        return if (album != null) Choice.Tracks("album-${track.parentId}", album, album.indexOfFirst { it.id == track.id }) else Choice.Tracks("track-${track.id}", listOf(track), 0)
    }

    /** The followed podcast whose name is closest to what was said. */
    private suspend fun podcast(client: FlexarrClient, said: String): Podcast? = podcasts(client, said).firstOrNull()

    /** The followed podcasts whose names have the words said in them, closest first (none, on a server without podcasts). */
    suspend fun podcasts(client: FlexarrClient, said: String): List<Podcast> =
        matchingSpoken(said, runCatching { client.podcasts() }.getOrDefault(emptyList())) { it.title }

    /** The episode to hear next: one part-heard, else the first unplayed in the show's order. */
    private suspend fun nextToHear(client: FlexarrClient, podcast: Podcast): PodcastEpisode? {
        client.continueListening(take = 50).firstOrNull { it.podcastId == podcast.id }?.let { return it }
        val oldest = (podcast.subscription ?: client.podcast(podcast.id).subscription)?.oldestFirst == true
        return client.podcastEpisodes(podcast.id, if (oldest) PodcastEpisodeSort.Oldest else PodcastEpisodeSort.Newest, unplayed = true, take = 1).items.firstOrNull()
    }

    /** The newest episode not heard yet. */
    private suspend fun latest(client: FlexarrClient, podcast: Podcast): PodcastEpisode? =
        client.podcastEpisodes(podcast.id, PodcastEpisodeSort.Newest, unplayed = true, take = 1).items.firstOrNull()

    /** Something named that might be played, worked out only once it's the best match. */
    private class Candidate(val name: String, val choice: suspend () -> Choice?)

    /** What kind of thing Android heard asked for ("play the album Jazz"), and the name it heard for it. */
    private data class Focus(val type: String?, val term: String?) {
        val isArtist get() = type == MediaStore.Audio.Artists.ENTRY_CONTENT_TYPE
        val isAlbum get() = type == MediaStore.Audio.Albums.ENTRY_CONTENT_TYPE
        val isSong get() = type == MediaStore.Audio.Media.ENTRY_CONTENT_TYPE
        val isPlaylist get() = type == ENTRY_PLAYLIST

        companion object {
            val Anything = Focus(null, null)
            private const val ENTRY_PLAYLIST = "vnd.android.cursor.item/playlist"
            private val known = setOf(
                MediaStore.Audio.Artists.ENTRY_CONTENT_TYPE,
                MediaStore.Audio.Albums.ENTRY_CONTENT_TYPE,
                MediaStore.Audio.Media.ENTRY_CONTENT_TYPE,
                ENTRY_PLAYLIST,
            )

            fun of(extras: Bundle?): Focus {
                val type = extras?.getString(MediaStore.EXTRA_MEDIA_FOCUS)?.takeIf { it in known } ?: return Anything
                val term = when (type) {
                    MediaStore.Audio.Artists.ENTRY_CONTENT_TYPE -> extras.getString(MediaStore.EXTRA_MEDIA_ARTIST)
                    MediaStore.Audio.Albums.ENTRY_CONTENT_TYPE -> extras.getString(MediaStore.EXTRA_MEDIA_ALBUM)
                    MediaStore.Audio.Media.ENTRY_CONTENT_TYPE -> extras.getString(MediaStore.EXTRA_MEDIA_TITLE)
                    else -> extras.getString(MediaStore.EXTRA_MEDIA_PLAYLIST)
                }
                return Focus(type, term?.takeIf { it.isNotBlank() })
            }
        }
    }
}
