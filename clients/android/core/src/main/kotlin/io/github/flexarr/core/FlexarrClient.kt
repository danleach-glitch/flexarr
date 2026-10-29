package io.github.flexarr.core

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withContext
import kotlinx.serialization.KSerializer
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.serializer
import okhttp3.Call
import okhttp3.Callback
import okhttp3.HttpUrl
import okhttp3.HttpUrl.Companion.toHttpUrlOrNull
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response
import java.io.IOException
import java.util.concurrent.TimeUnit
import kotlin.coroutines.resume
import kotlin.coroutines.resumeWithException

/** Why a request to the server didn't work, worded for the person using the app. */
sealed class FlexarrException(message: String) : Exception(message) {
    /** The server rejected the sign-in (or one is required and none was sent). */
    class Unauthorized : FlexarrException(
        "The server didn’t accept this device’s sign-in. Connect it again with a code, or if you typed an API key, check it against Settings › Access on the web.",
    )

    class Http(val status: Int, message: String?) : FlexarrException(message ?: "The server answered with an error (HTTP $status).")

    /** The response was not what a Flexarr server sends (for example an HTML page). */
    class UnexpectedResponse : FlexarrException("That address answered, but not like a Flexarr server. Check the host name and port.")

    class InvalidAddress : FlexarrException("That doesn't look like a server address. Try something like http://192.168.1.20:8383.")

    class Unreachable(cause: IOException) : FlexarrException("Couldn't reach the server. Make sure Flexarr is running and this device is on the same network.") {
        init {
            initCause(cause)
        }
    }
}

/**
 * Talks to a Flexarr server's `/api/v1` REST API, as FlexarrKit's FlexarrClient does for the Apple apps.
 * [apiKey] is this device's own token (or the server's key); [profileToken] who's watching (null acts as the main profile).
 */
class FlexarrClient(
    val baseUrl: HttpUrl,
    val apiKey: String? = null,
    val profileToken: String? = null,
    private val http: OkHttpClient = sharedHttp,
    /** Called whenever the server answers 401, so the app can notice its saved sign-in has stopped working. */
    private val onUnauthorized: (() -> Unit)? = null,
) {
    // System

    suspend fun systemStatus(): SystemStatus = get("system/status")

    // Libraries and items

    suspend fun libraries(): List<Library> = get("libraries")

    suspend fun libraryItems(
        libraryId: String,
        sort: LibrarySort = LibrarySort.Title,
        genre: String? = null,
        skip: Int = 0,
        take: Int = 60,
    ): Paged<ItemSummary> =
        get(
            "libraries/$libraryId/items",
            buildMap {
                if (sort != LibrarySort.Title) put("sort", sort.raw)
                if (!genre.isNullOrEmpty()) put("genre", genre)
                if (skip > 0) put("skip", skip.toString())
                put("take", take.toString())
            },
        )

    suspend fun libraryGenres(libraryId: String): List<GenreCount> = get("libraries/$libraryId/genres")

    suspend fun home(): Home = get("home")

    suspend fun item(id: String): ItemDetail = get("items/$id")

    suspend fun similar(id: String): List<ItemSummary> = get("items/$id/similar")

    /**
     * Takes a title out of Continue Watching without marking it watched; for an episode, its show stays out until
     * another episode is played.
     */
    suspend fun removeFromContinueWatching(id: String) {
        send("DELETE", "home/continue/$id")
    }

    /** The episode to play next for a series or season, or null when everything is watched. */
    suspend fun nextUp(id: String): ItemSummary? {
        val body = send("GET", "items/$id/next-up")
        return if (body.isEmpty()) null else decode(serializer<ItemSummary>(), body)
    }

    suspend fun setWatched(id: String, watched: Boolean) {
        send("POST", "items/$id/watched", body = json.encodeToString(WatchedRequest(watched)))
    }

    suspend fun person(id: String): Person = get("people/$id")

    suspend fun search(query: String): SearchResults = get("search", mapOf("q" to query))

    // Playback

    suspend fun startPlayback(request: PlaybackRequest): PlaybackInfo =
        decode(serializer(), send("POST", "playback", body = json.encodeToString(request)))

    suspend fun reportProgress(sessionId: String, positionSeconds: Double, paused: Boolean, ended: Boolean = false) {
        val report = ProgressReport(positionSeconds.coerceAtLeast(0.0), paused, ended)
        send("POST", "playback/$sessionId/progress", body = json.encodeToString(report))
    }

    suspend fun stopPlayback(sessionId: String) {
        send("DELETE", "playback/$sessionId")
    }

    /** The viewer picked subtitles in the player (a track's key, or null for off): remembered for next time. */
    suspend fun setSubtitle(sessionId: String, key: String?) {
        send("PUT", "playback/$sessionId/subtitle", body = json.encodeToString(SubtitleChoice(key ?: "off")))
    }

    suspend fun setPlaybackPreferences(preferences: PlaybackPreferences) {
        send("PUT", "me/playback-preferences", body = json.encodeToString(preferences))
    }

    /** Remembers how far this title's subtitles are moved (later when positive); used the next time they're fetched. */
    suspend fun setSubtitleDelay(itemId: String, seconds: Double) {
        send("PUT", "items/$itemId/subtitle-delay", body = json.encodeToString(SecondsBody(seconds)))
    }

    /** Plays a file's sound [seconds] later (earlier when negative, up to two seconds either way), for everyone. */
    suspend fun setAudioDelay(itemId: String, seconds: Double) {
        send("PUT", "items/$itemId/audio-delay", body = json.encodeToString(SecondsBody(seconds)))
    }

    /** Remembers [speed] for the show an episode belongs to, for this profile (1 forgets it). A film's isn't kept. */
    suspend fun setPlaybackSpeed(itemId: String, speed: Double) {
        send("PUT", "items/$itemId/playback-speed", body = json.encodeToString(SpeedBody(speed)))
    }

    // Profiles

    suspend fun profiles(): List<Profile> = get("profiles")

    /** Checks the PIN (if the profile has one) and returns the token for [profileToken]. A wrong PIN is a 403 saying so. */
    suspend fun selectProfile(id: String, pin: String? = null): ProfileSelection =
        decode(serializer(), send("POST", "profiles/$id/select", body = json.encodeToString(SelectProfileRequest(pin))))

    /**
     * Saves the order of [profileId]'s libraries in the menu (see [libraryOrder]); the profile watching may set its own.
     * Servers before 0.54 answer 404.
     */
    suspend fun setLibraryOrder(profileId: String, order: List<String>): Profile =
        decode(serializer(), send("PUT", "profiles/$profileId/library-order", body = json.encodeToString(LibraryOrderRequest(order))))

    // Pairing (connecting without typing the API key)

    /** Asks the server for a code to show; someone signed in approves it in the web app. */
    suspend fun startPairing(deviceName: String): PairingStart =
        decode(serializer(), send("POST", "pairing", body = json.encodeToString(mapOf("deviceName" to deviceName))))

    /** Polled while the code is on screen; carries this device's own token once approved. */
    suspend fun pairingStatus(id: String): PairingStatus = get("pairing/$id")

    /** Whether the server is new and nobody has set it up yet. Null when it doesn't say. */
    suspend fun setupStatus(): SetupStatus? = runCatching { get<SetupStatus>("setup") }.getOrNull()

    // Sign-in

    /** How this client signed in, which profile it acts for, and whether it may manage the server. */
    suspend fun me(): SignInInfo = get("me")

    /** Swaps the server's API key for a token of this device's own, which can be signed out from the web app. */
    suspend fun deviceToken(deviceName: String): String =
        decode(serializer<DeviceToken>(), send("POST", "session/device", body = json.encodeToString(mapOf("deviceName" to deviceName)))).token

    /** Signs this device out on the server (its token stops working). */
    suspend fun signOut() {
        send("DELETE", "session")
    }

    // URLs

    /** Resolves a server-relative URL such as a stream or artwork path. */
    fun url(relative: String): String =
        if (relative.startsWith("http://") || relative.startsWith("https://")) relative
        else baseUrl.resolve(relative)?.toString() ?: relative

    /** Artwork URL with a width hint for the server's image cache. Image endpoints need no sign-in. */
    fun imageUrl(relative: String?, width: Int): String? {
        if (relative.isNullOrEmpty()) return null
        val separator = if ('?' in relative) '&' else '?'
        return url("$relative${separator}w=$width")
    }

    // Transport

    internal suspend inline fun <reified T> get(path: String, query: Map<String, String> = emptyMap()): T {
        val text = send("GET", path, query)
        return withContext(Dispatchers.Default) { decode(serializer<T>(), text) }
    }

    /** The answer's text. Off the main thread throughout: Android refuses network reads on it, the body's included. */
    internal suspend fun send(method: String, path: String, query: Map<String, String> = emptyMap(), body: String? = null): String =
        withContext(Dispatchers.IO) { exchange(method, path, query, body) }

    private suspend fun exchange(method: String, path: String, query: Map<String, String>, body: String?): String {
        val url = baseUrl.newBuilder().addPathSegments("api/v1/$path").apply {
            query.forEach { (name, value) -> addQueryParameter(name, value) }
        }.build()
        val request = Request.Builder().url(url).header("Accept", "application/json").apply {
            apiKey?.takeIf { it.isNotEmpty() }?.let { header("X-Api-Key", it) }
            profileToken?.takeIf { it.isNotEmpty() }?.let { header("X-Flexarr-Profile", it) }
            deviceType?.let { header("X-Flexarr-Device", it) }
            val requestBody = body?.toRequestBody(jsonType) ?: if (method == "POST") ByteArray(0).toRequestBody(null) else null
            method(method, requestBody)
        }.build()

        val response = try {
            http.newCall(request).await()
        } catch (e: IOException) {
            throw FlexarrException.Unreachable(e)
        }
        return response.use {
            val text = it.body.string()
            when {
                it.code == 204 || it.code == 202 -> ""
                it.isSuccessful -> {
                    // Unknown paths fall through to the web app's index.html; don't try to decode that.
                    if (it.header("Content-Type")?.contains("text/html") == true) throw FlexarrException.UnexpectedResponse()
                    text
                }
                it.code == 401 -> {
                    onUnauthorized?.invoke()
                    throw FlexarrException.Unauthorized()
                }
                else -> throw FlexarrException.Http(it.code, problemMessage(text))
            }
        }
    }

    /** The answer's text, for callers in this module that read it themselves (an empty answer meaning none). */
    internal suspend fun sendRaw(method: String, path: String): String = send(method, path)

    internal fun <T> decodeText(serializer: KSerializer<T>, text: String): T = decode(serializer, text)

    /** Sends [body] as JSON and reads the answer as a [T]. */
    internal suspend inline fun <reified B, reified T> sendDecoding(method: String, path: String, body: B): T {
        val text = send(method, path, body = body?.let { json.encodeToString(serializer<B>(), it) })
        return decode(serializer<T>(), text)
    }

    internal fun <T> decode(serializer: KSerializer<T>, text: String): T =
        try {
            json.decodeFromString(serializer, text)
        } catch (e: IllegalArgumentException) {
            throw FlexarrException.UnexpectedResponse().apply { initCause(e) }
        }

    companion object {
        /**
         * What this device is ("AndroidPhone", "AndroidTablet" or "AndroidTv"), sent with every request as
         * `X-Flexarr-Device` so the server's lists of devices can name and draw it. Set once at launch.
         */
        @Volatile
        var deviceType: String? = null

        val json = Json {
            ignoreUnknownKeys = true
            explicitNulls = false
            coerceInputValues = true
        }

        private val jsonType = "application/json".toMediaType()

        /** One connection pool for the whole app; clients for different sign-ins share it. */
        val sharedHttp: OkHttpClient = OkHttpClient.Builder()
            .connectTimeout(10, TimeUnit.SECONDS)
            .readTimeout(20, TimeUnit.SECONDS)
            .build()

        /**
         * Turns what a person typed ("mini.local", "192.168.1.5:8383", "http://host:8383/") into a base URL.
         * Defaults to http and Flexarr's port 8383 when they're left out.
         */
        fun normalizeAddress(input: String): HttpUrl? {
            val text = input.trim()
            if (text.isEmpty() || ' ' in text) return null
            val hasScheme = "://" in text
            val url = (if (hasScheme) text else "http://$text").toHttpUrlOrNull() ?: return null
            if (url.host.isEmpty()) return null
            // HttpUrl fills in 80 for a bare http host: it only means "left out" when the text didn't say 80.
            val portTyped = Regex("^(?:[a-zA-Z]+://)?(?:\\[[^]]+]|[^/:]+):\\d+").containsMatchIn(text)
            return url.newBuilder()
                .apply { if (!hasScheme && !portTyped) port(8383) }
                .encodedPath(url.encodedPath.trimEnd('/') + "/")
                .query(null)
                .fragment(null)
                .build()
        }

        /** Reads ASP.NET problem details (`errors`, `detail` or `title`), or a plain `reason`, into one message. */
        internal fun problemMessage(text: String): String? {
            val problem = runCatching { json.parseToJsonElement(text).jsonObject }.getOrNull() ?: return null
            (problem["errors"] as? JsonObject)?.values?.firstNotNullOfOrNull { field ->
                runCatching { (field.jsonArray.firstOrNull() as? JsonPrimitive)?.content }.getOrNull()
            }?.let { return it }
            return listOf("detail", "title", "reason").firstNotNullOfOrNull { (problem[it] as? JsonPrimitive)?.content }
        }
    }
}

/** Runs the call without blocking, cancelling it if the coroutine is cancelled. */
private suspend fun Call.await(): Response = suspendCancellableCoroutine { continuation ->
    enqueue(object : Callback {
        override fun onResponse(call: Call, response: Response) = continuation.resume(response)
        override fun onFailure(call: Call, e: IOException) = continuation.resumeWithException(e)
    })
    continuation.invokeOnCancellation { runCatching { cancel() } }
}
