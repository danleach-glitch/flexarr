package io.github.flexarr.core

import kotlinx.serialization.Serializable
import kotlinx.serialization.serializer
import java.util.concurrent.TimeUnit

// Finding subtitles through the server's Bazarr, and re-syncing the ones beside a file to its audio, as FlexarrKit's
// SubtitleSearch.swift.

/** Subtitles Bazarr found for a file, best first. */
@Serializable
data class SubtitleSearchResults(val results: List<SubtitleSearchResult> = emptyList())

/** One set of subtitles a provider has: pass its [id] to [FlexarrClient.downloadSubtitles] to fetch it. */
@Serializable
data class SubtitleSearchResult(
    val id: String,
    val provider: String,
    val language: String,
    /** "English", "English (SDH)". */
    val languageLabel: String,
    /** The release the subtitles were made for. */
    val release: String,
    val hearingImpaired: Boolean = false,
    val forced: Boolean = false,
    /** 0 to 100: how well they match the file. */
    val score: Int = 0,
    /** Made for this very release, so they should be in time. */
    val matchesFile: Boolean = false,
    val uploader: String? = null,
)

/** Subtitles saved beside the file: the track's key in the next playback session, and its label. */
@Serializable
data class DownloadedSubtitles(val key: String, val label: String)

@Serializable(with = SubtitleSyncState.Serializer::class)
enum class SubtitleSyncState(override val raw: String) : ForwardCompatibleEnum {
    Running("running"), Done("done"), Failed("failed"),
    /** Nothing has been re-synced for this file (since the server started). */
    Idle("none"),
    Unknown("");

    object Serializer : ForwardCompatibleEnumSerializer<SubtitleSyncState>("SubtitleSyncState", entries, Unknown)
}

/** How a re-sync of a file's subtitles to its audio is going. */
@Serializable
data class SubtitleSyncStatus(
    val state: SubtitleSyncState,
    /** The subtitle track being (or last) re-synced. */
    val key: String? = null,
    /** Why it failed, in words. */
    val error: String? = null,
)

@Serializable
internal data class SubtitleDownloadBody(val result: String?, val fileId: String)

@Serializable
internal data class SubtitleSyncBody(val key: String, val fileId: String)

/** Bazarr's providers are slow: a search often takes a minute, sometimes more. */
private val slowHttp by lazy { FlexarrClient.sharedHttp.newBuilder().readTimeout(180, TimeUnit.SECONDS).build() }

/** Asks Bazarr's providers for subtitles for a file. Slow: often a minute, sometimes more. */
suspend fun FlexarrClient.searchSubtitles(itemId: String, fileId: String): List<SubtitleSearchResult> =
    decodeText(
        serializer<SubtitleSearchResults>(),
        send("GET", "items/$itemId/subtitles/search", mapOf("fileId" to fileId), racesAlternate = false, http = slowHttp),
    ).results

/**
 * Saves a search result beside the file ([result] null: the best match Bazarr can find). The new track has the
 * returned key in the next playback session.
 */
suspend fun FlexarrClient.downloadSubtitles(itemId: String, fileId: String, result: String? = null): DownloadedSubtitles =
    decodeText(
        serializer(),
        send(
            "POST",
            "items/$itemId/subtitles/download",
            body = FlexarrClient.json.encodeToString(SubtitleDownloadBody(result, fileId)),
            http = slowHttp,
        ),
    )

/** Starts re-syncing a subtitle file to the video's audio; follow it with [subtitleSync]. A 409 means one is already running. */
suspend fun FlexarrClient.startSubtitleSync(itemId: String, fileId: String, key: String) {
    send("POST", "items/$itemId/subtitles/sync", body = FlexarrClient.json.encodeToString(SubtitleSyncBody(key, fileId)))
}

suspend fun FlexarrClient.subtitleSync(itemId: String, fileId: String): SubtitleSyncStatus =
    decodeText(serializer(), send("GET", "items/$itemId/subtitles/sync", mapOf("fileId" to fileId)))
