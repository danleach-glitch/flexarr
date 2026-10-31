package io.github.flexarr.core

import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.launch
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull
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
import okhttp3.RequestBody
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response
import java.io.IOException
import java.time.DateTimeException
import java.net.ConnectException
import java.net.Inet4Address
import java.net.Inet6Address
import java.net.InetAddress
import java.net.NoRouteToHostException
import java.net.SocketTimeoutException
import java.net.UnknownHostException
import java.util.concurrent.TimeUnit
import javax.net.ssl.SSLException
import kotlin.coroutines.resume
import kotlin.coroutines.resumeWithException

/** Why a request to the server didn't work, worded for the person using the app. */
sealed class FlexarrException(message: String) : Exception(message) {
    /** The server rejected the sign-in (or one is required and none was sent). */
    class Unauthorized : FlexarrException(
        "The server didn’t accept this device’s sign-in. Connect it again with a code, or if you typed an API key, check it against Settings › Access on the web.",
    )

    /** [detail]: the server's own words for what went wrong (its problem details' `detail`), when it gave some. */
    class Http(val status: Int, message: String?, val detail: String? = null) : FlexarrException(message ?: "The server answered with an error (HTTP $status).")

    /**
     * This takes the admin PIN (leaving a kids profile, locking or releasing a device), which wasn't given or
     * wasn't right. The message is the server's.
     */
    class ParentPinNeeded(message: String?) : FlexarrException(message ?: "Enter the admin PIN.")

    /** The admin PIN was right but the chosen profile's own PIN wasn't. The message is the server's. */
    class OwnPinWrong(message: String?) : FlexarrException(message ?: "That PIN isn’t right.")

    /** This device is locked to kids profiles, so only they can be chosen. */
    class KidsLocked(message: String?) : FlexarrException(message ?: "Child lock is on for this device.")

    /** A live channel can't be watched: every stream is recording, [recording] among them. */
    class RecordingInTheWayException(val recording: RecordingInTheWay) : FlexarrException(recording.message)

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
 * [addresses] is where the server is reached, at home or away (see [ServerAddresses]).
 */
class FlexarrClient(
    val addresses: ServerAddresses,
    val apiKey: String? = null,
    val profileToken: String? = null,
    private val http: OkHttpClient = sharedHttp,
    /** Called whenever the server answers 401, so the app can notice its saved sign-in has stopped working. */
    internal val onUnauthorized: (() -> Unit)? = null,
) {
    /** A client for a server with only the one address. */
    constructor(
        baseUrl: HttpUrl,
        apiKey: String? = null,
        profileToken: String? = null,
        http: OkHttpClient = sharedHttp,
        onUnauthorized: (() -> Unit)? = null,
    ) : this(ServerAddresses(baseUrl, null), apiKey, profileToken, http, onUnauthorized)

    /** The address in use now: streams and artwork are resolved against it. */
    val baseUrl: HttpUrl get() = addresses.current

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

    /** A library's own home page: a hero of its titles, then rows of just this library (the TV's Browse). */
    suspend fun libraryBrowse(libraryId: String): Home = get("libraries/$libraryId/browse")

    suspend fun home(): Home = get("home")

    /** Just the Continue watching row, for the home-screen widget. */
    suspend fun continueWatching(take: Int): List<ItemSummary> = get("home/continue", mapOf("take" to "$take"))

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
        return if (body.isEmpty()) null else decodeText(serializer<ItemSummary>(), body)
    }

    suspend fun setWatched(id: String, watched: Boolean) {
        send("POST", "items/$id/watched", body = json.encodeToString(WatchedRequest(watched)))
    }

    suspend fun person(id: String): Person = get("people/$id")

    suspend fun search(query: String): SearchResults = get("search", mapOf("q" to query))

    // Playback

    suspend fun startPlayback(request: PlaybackRequest): PlaybackInfo =
        decodeText(serializer(), send("POST", "playback", body = json.encodeToString(request)))

    suspend fun reportProgress(sessionId: String, positionSeconds: Double, paused: Boolean, ended: Boolean = false) {
        val report = ProgressReport(positionSeconds.coerceAtLeast(0.0), paused, ended)
        send("POST", "playback/$sessionId/progress", body = json.encodeToString(report))
    }

    suspend fun stopPlayback(sessionId: String) {
        send("DELETE", "playback/$sessionId")
    }

    /**
     * Why the server ended this session's converted stream so another screen could play, if it did: its playlist
     * answers 410 with the reason. Null otherwise (it's still there, it's a file played as it is, or the server
     * didn't answer within a few seconds), so a hung server doesn't keep the player waiting.
     */
    suspend fun takenOverReason(sessionId: String): String? = withTimeoutOrNull(5_000) {
        try {
            send("GET", "playback/$sessionId/main.m3u8", racesAlternate = false)
            null
        } catch (e: FlexarrException.Http) {
            if (e.status == 410) e.message else null
        } catch (_: FlexarrException) {
            null
        }
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

    /**
     * Checks the PIN (if the profile has one) and returns the token for [profileToken]. A wrong PIN is a 403 saying so.
     * Leaving a kids profile takes the admin PIN as [parentPin]: without it, [FlexarrException.ParentPinNeeded].
     * With it right but [pin] wrong, [FlexarrException.OwnPinWrong].
     */
    suspend fun selectProfile(id: String, pin: String? = null, parentPin: String? = null): ProfileSelection =
        decodeText(serializer(), send("POST", "profiles/$id/select", body = json.encodeToString(SelectProfileRequest(pin, parentPin))))

    /**
     * Locks this device to kids profiles, choosing [profileId] (else the one chosen here, or the first). Takes the main
     * profile's PIN unless it's unlocked here ([FlexarrException.ParentPinNeeded] otherwise). Servers before 0.56 answer 404.
     */
    suspend fun lockToKids(pin: String?, profileId: String? = null): ProfileSelection =
        decodeText(serializer(), send("POST", "profiles/kids-lock", body = json.encodeToString(KidsLockRequest(pin, profileId))))

    /**
     * Takes a forgotten PIN off a profile with the admin PIN, from the profile picker ([FlexarrException.ParentPinNeeded]
     * if it's wrong). Returns the profile, now without one. Servers before 0.57 answer 404.
     */
    suspend fun forgetPin(id: String, adminPin: String): Profile =
        sendDecoding("POST", "profiles/$id/forgot-pin", ForgotPinRequest(adminPin))

    /** Releases this device from kids profiles with the admin PIN ([FlexarrException.ParentPinNeeded] if it's wrong). */
    suspend fun releaseKidsLock(pin: String) {
        send("POST", "profiles/kids-lock/release", body = json.encodeToString(KidsLockRequest(pin)))
    }

    /**
     * Saves the order of [profileId]'s libraries in the menu (see [libraryOrder]); the profile watching may set its own.
     * Servers before 0.54 answer 404.
     */
    suspend fun setLibraryOrder(profileId: String, order: List<String>): Profile =
        decodeText(serializer(), send("PUT", "profiles/$profileId/library-order", body = json.encodeToString(LibraryOrderRequest(order))))

    // Pairing (connecting without typing the API key)

    /** Asks the server for a code to show; someone signed in approves it in the web app. */
    /**
     * Says this app shows "Not allowed" itself after "Don't allow" (and asks again only on Try again), so the server asks
     * the main profile about every code it asks for (servers before 0.56 ignore it).
     */
    suspend fun startPairing(deviceName: String): PairingStart =
        decodeText(serializer(), send("POST", "pairing", body = json.encodeToString(PairingStartBody(deviceName, stopsWhenDeclined = true))))

    /** Polled while the code is on screen; carries this device's own token once approved. */
    suspend fun pairingStatus(id: String): PairingStatus = get("pairing/$id")

    /** Whether the server is new and nobody has set it up yet. Null when it doesn't say. */
    suspend fun setupStatus(): SetupStatus? = runCatching { get<SetupStatus>("setup") }.getOrNull()

    // Sign-in

    /** How this client signed in, which profile it acts for, and whether it may manage the server. */
    suspend fun me(): SignInInfo = get("me")

    /**
     * Unlocks managing the server on this sign-in for a while, with the admin PIN. A wrong one is a 403 saying
     * so; too many, 429 saying how long to wait; and 409 when the main profile has no PIN yet.
     */
    suspend fun unlockAdmin(pin: String): AdminUnlock = sendDecoding("POST", "admin/unlock", AdminUnlockRequest(pin))

    /** Swaps the server's API key for a token of this device's own, which can be signed out from the web app. */
    suspend fun deviceToken(deviceName: String): String =
        decodeText(serializer<DeviceToken>(), send("POST", "session/device", body = json.encodeToString(mapOf("deviceName" to deviceName)))).token

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

    internal suspend inline fun <reified T> get(path: String, query: Map<String, String> = emptyMap()): T =
        decodeText(serializer<T>(), send("GET", path, query))

    /**
     * The answer's text. Off the main thread throughout: Android refuses network reads on it, the body's included.
     * [racesAlternate]: false for a GET that is slow by nature (a subtitle search), so waiting on it never sends the
     * same work to the other address too.
     */
    internal suspend fun send(
        method: String,
        path: String,
        query: Map<String, String> = emptyMap(),
        body: String? = null,
        racesAlternate: Boolean = true,
        http: OkHttpClient = this.http,
    ): String = withContext(Dispatchers.IO) { exchange(method, path, query, body?.toRequestBody(jsonType), racesAlternate, http) }

    /** As [send], with a body of any type (a plain-text log, say) and its own time limit for the whole call. */
    internal suspend fun sendBody(
        method: String,
        path: String,
        query: Map<String, String>,
        body: RequestBody,
        timeoutSeconds: Long,
    ): String = withContext(Dispatchers.IO) {
        val slow = http.newBuilder().callTimeout(timeoutSeconds, TimeUnit.SECONDS).readTimeout(timeoutSeconds, TimeUnit.SECONDS).build()
        exchange(method, path, query, body, racesAlternate = false, http = slow)
    }

    private fun request(method: String, path: String, query: Map<String, String>, body: RequestBody?, base: HttpUrl): Request {
        // An id from outside the app (a link, say) mustn't climb out of the path it was put in: OkHttp would resolve
        // `..` against the segments before it, and splits on a backslash as well as a slash. Ids never are one.
        if (path.split('/', '\\').any { isDotSegment(it) }) throw FlexarrException.Http(404, null)
        // Read as already encoded, so a name made one segment by [pathSegment] stays one; anything else that needs
        // encoding (a space) still is.
        val url = base.newBuilder().addEncodedPathSegments("api/v1/$path").apply {
            query.forEach { (name, value) -> addQueryParameter(name, value) }
        }.build()
        return Request.Builder().url(url).header("Accept", "application/json").apply {
            signIn()
            val requestBody = body ?: if (method == "POST") ByteArray(0).toRequestBody(null) else null
            method(method, requestBody)
        }.build()
    }

    /** This device's sign-in, who's watching and what it is, on any request to the server. */
    internal fun Request.Builder.signIn(): Request.Builder = apply {
        apiKey?.takeIf { it.isNotEmpty() }?.let { header("X-Api-Key", it) }
        profileToken?.takeIf { it.isNotEmpty() }?.let { header("X-Flexarr-Profile", it) }
        deviceType?.let { header("X-Flexarr-Device", it) }
    }

    private suspend fun exchange(
        method: String,
        path: String,
        query: Map<String, String>,
        body: RequestBody?,
        racesAlternate: Boolean,
        http: OkHttpClient,
    ): String {
        val response = try {
            reach(method, path, query, body, racesAlternate, http)
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
                it.code == 409 && RecordingInTheWay.parse(text) != null ->
                    throw FlexarrException.RecordingInTheWayException(RecordingInTheWay.parse(text)!!)
                it.code == 403 && problemFlag(text, "parentPin") -> throw FlexarrException.ParentPinNeeded(problemMessage(text))
                it.code == 403 && problemFlag(text, "ownPin") -> throw FlexarrException.OwnPinWrong(problemMessage(text))
                it.code == 403 && problemFlag(text, "kidsLock") -> throw FlexarrException.KidsLocked(problemMessage(text))
                // A family guest's device its host removed: refused whatever it asks, so the app signs out (as for 401).
                it.code == 403 && problemFlag(text, "guestRemoved") -> {
                    onUnauthorized?.invoke()
                    throw FlexarrException.Http(it.code, problemMessage(text), problemDetail(text))
                }
                else -> throw FlexarrException.Http(it.code, problemMessage(text), problemDetail(text))
            }
        }
    }

    /**
     * Sends the request to the address that answered last. With a second address, a GET that can't connect to the
     * first (or hasn't heard back after [ServerAddresses.ALTERNATE_DELAY_MS]) is also sent there and the first answer
     * wins; anything else only moves over once the first address has failed to connect, so a write is never sent
     * twice. Whichever address answers is used from then on.
     */
    private suspend fun reach(
        method: String,
        path: String,
        query: Map<String, String>,
        body: RequestBody?,
        racesAlternate: Boolean,
        http: OkHttpClient,
    ): Response {
        val primary = addresses.current
        val first = request(method, path, query, body, primary)
        val alternate = addresses.alternate(primary) ?: return http.newCall(first).await()
        val second = request(method, path, query, body, alternate)
        val (response, reached) = if (method == "GET" && racesAlternate) {
            race(http, first to primary, second to alternate)
        } else {
            try {
                http.newCall(first).await() to primary
            } catch (e: IOException) {
                if (!couldNotConnect(e)) throw e
                http.newCall(second).await() to alternate
            }
        }
        addresses.reached(reached)
        checkHomeIfDue(http)
        return response
    }

    /**
     * Both requests at once, the second starting after a moment (or at once, if the first can't connect). The first
     * answer wins, and the other is cancelled. An answer that isn't a connection problem from the first address ends
     * it: the other address wouldn't say anything different.
     */
    private suspend fun race(
        http: OkHttpClient,
        first: Pair<Request, HttpUrl>,
        second: Pair<Request, HttpUrl>,
    ): Pair<Response, HttpUrl> = coroutineScope {
        val answers = Channel<Pair<HttpUrl, Result<Response>>>(capacity = 2)
        val firstFailed = CompletableDeferred<Unit>()
        val jobs = listOf(
            launch {
                val result = runCatching { http.newCall(first.first).await() }
                if (result.exceptionOrNull()?.let { it is IOException && couldNotConnect(it) } == true) firstFailed.complete(Unit)
                answers.send(first.second to result)
            },
            launch {
                withTimeoutOrNull(ServerAddresses.ALTERNATE_DELAY_MS) { firstFailed.await() }
                answers.send(second.second to runCatching { http.newCall(second.first).await() })
            },
        )
        try {
            repeat(2) {
                val (url, result) = answers.receive()
                result.onSuccess { return@coroutineScope it to url }
                val error = result.exceptionOrNull()!!
                if (url == first.second && !(error is IOException && couldNotConnect(error))) throw error
            }
            throw ConnectException("Neither of the server's addresses answered.")
        } finally {
            jobs.forEach { it.cancel() }
            // An answer that came in second isn't wanted.
            answers.close()
            while (true) answers.tryReceive().getOrNull()?.second?.getOrNull()?.close() ?: break
        }
    }

    /** While away, sees every few minutes whether home answers again, and goes back to it when it does. */
    private fun checkHomeIfDue(http: OkHttpClient) {
        if (!addresses.shouldCheckHome()) return
        val probe = request("GET", "system/status", emptyMap(), null, addresses.home)
        val quick = http.newBuilder().callTimeout(5, TimeUnit.SECONDS).build()
        background.launch {
            runCatching { quick.newCall(probe).await().use { if (it.code == 200) addresses.reached(addresses.home) } }
        }
    }

    /** The answer's text, for callers in this module that read it themselves (an empty answer meaning none). */
    internal suspend fun sendRaw(method: String, path: String): String = send(method, path)

    /** Reads an answer off the main thread: a long one (the guide, a big library) would hold up the screen. */
    internal suspend fun <T> decodeText(serializer: KSerializer<T>, text: String): T = withContext(Dispatchers.Default) { decode(serializer, text) }

    /** Sends [body] as JSON and reads the answer as a [T]. */
    internal suspend inline fun <reified B, reified T> sendDecoding(method: String, path: String, body: B): T {
        val text = send(method, path, body = body?.let { json.encodeToString(serializer<B>(), it) })
        return decodeText(serializer<T>(), text)
    }

    /** An answer that doesn't read as [T] (a field missing, a time that isn't one) is [FlexarrException.UnexpectedResponse]. */
    internal fun <T> decode(serializer: KSerializer<T>, text: String): T =
        try {
            json.decodeFromString(serializer, text)
        } catch (e: IllegalArgumentException) {
            // SerializationException is one of these.
            throw FlexarrException.UnexpectedResponse().apply { initCause(e) }
        } catch (e: DateTimeException) {
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

        /** For checks nobody waits on (whether home answers again). */
        private val background = CoroutineScope(SupervisorJob() + Dispatchers.IO)

        /** The address didn't answer at all (as opposed to answering with an error, or the request being cancelled). */
        internal fun couldNotConnect(error: IOException): Boolean =
            error is ConnectException || error is UnknownHostException || error is NoRouteToHostException ||
                error is SocketTimeoutException || error is SSLException

        /**
         * The family guest's tunnel, when this app has one (the app sets it): requests to 127.0.0.1 wait for it to be up,
         * go to the port it's listening on, and start it again if it's gone. Null leaves loopback requests alone.
         */
        @Volatile
        var loopbackTunnel: LoopbackTunnel? = null

        /** One connection pool for the whole app; clients for different sign-ins share it. */
        val sharedHttp: OkHttpClient = OkHttpClient.Builder()
            .connectTimeout(10, TimeUnit.SECONDS)
            .readTimeout(20, TimeUnit.SECONDS)
            .addInterceptor(LoopbackTunnel.interceptor { loopbackTunnel })
            .build()

        /**
         * Whether [id], from outside the app (a link), looks like one of the server's: letters, digits, `-` and `_`
         * (a GUID, say). Anything else could reach another of the server's paths once put in one.
         */
        fun isPlainId(id: String): Boolean = plainId.matches(id)

        private val plainId = Regex("^[A-Za-z0-9_-]+$")

        /**
         * Free text (a genre's name) as one segment of a path: its slashes, backslashes and percent signs encoded, so
         * "Action/Adventure" or a name with `\..\` in it can't become segments of their own.
         */
        fun pathSegment(text: String): String = text.replace("%", "%25").replace("/", "%2F").replace("\\", "%5C")

        /** `.` or `..`, however it's written (OkHttp reads `%2e` as a dot too). */
        internal fun isDotSegment(segment: String): Boolean = segment.replace("%2e", ".", ignoreCase = true).let { it == "." || it == ".." }

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

        /**
         * The addresses to try, in order, for what a person typed. As [normalizeAddress], except that a host on the
         * internet typed without a scheme is tried over https first (on 443, unless a port was typed), and only then as
         * http on Flexarr's port, so a sign-in isn't sent in the clear when it needn't be. A home-network host (a private
         * or Tailscale address, `.local`, `.ts.net`, a name with no dots) stays as it was.
         */
        fun addressCandidates(input: String): List<HttpUrl> {
            val text = input.trim()
            val url = normalizeAddress(text) ?: return emptyList()
            if ("://" in text || isHomeNetworkHost(url.host)) return listOf(url)
            val portTyped = Regex("^(?:\\[[^]]+]|[^/:]+):\\d+").containsMatchIn(text)
            val secure = url.newBuilder().scheme("https").port(if (portTyped) url.port else 443).build()
            return listOf(secure, url)
        }

        /** A host only reached at home or over Tailscale, where a server is usually plain http on its own port. */
        fun isHomeNetworkHost(host: String): Boolean {
            val name = host.lowercase().trimEnd('.')
            if ('.' !in name && ':' !in name) return true // "mini", "localhost"
            if (homeSuffixes.any { name.endsWith(it) }) return true
            val address = ipAddress(name) ?: return false
            return address.isSiteLocalAddress || address.isLoopbackAddress || address.isLinkLocalAddress || isTailscaleAddress(address) ||
                (address is Inet6Address && (address.address[0].toInt() and 0xfe) == 0xfc) // fc00::/7, IPv6's private range
        }

        private val homeSuffixes = listOf(".local", ".ts.net", ".lan", ".home", ".internal", ".home.arpa")

        /** [host] is an IPv4 address in 100.64.0.0/10, where Tailscale gives its devices addresses (never looked up). */
        fun isTailscaleHost(host: String): Boolean = ipAddress(host)?.let(::isTailscaleAddress) == true

        /** 100.64.0.0/10, where Tailscale gives its devices addresses. */
        private fun isTailscaleAddress(address: InetAddress): Boolean =
            address is Inet4Address && address.address[0].toInt() == 100 && (address.address[1].toInt() and 0xc0) == 0x40

        /** [host] as an IP address when it is one (never looked up: a name stays a name). */
        private fun ipAddress(host: String): InetAddress? {
            val literal = host.removePrefix("[").removeSuffix("]")
            val isIp = Regex("^\\d{1,3}(\\.\\d{1,3}){3}$").matches(literal) || (':' in literal && Regex("^[0-9a-fA-F:.%]+$").matches(literal))
            return if (isIp) runCatching { InetAddress.getByName(literal) }.getOrNull() else null
        }

        /**
         * Whether [url], which a server gave as its address for away from home, is one the app may send this sign-in
         * to: https on a Tailscale name (`*.ts.net`, what the server's remote access gives), or a Tailscale address. The
         * server says it without proving it's the same server, so an answer tampered with on the home network can't
         * point the app at a host on the internet that would collect the sign-in from then on.
         */
        fun isAcceptableRemote(url: HttpUrl): Boolean {
            val host = url.host.lowercase().trimEnd('.')
            if (url.isHttps && host.endsWith(".ts.net")) return true
            return ipAddress(host)?.let(::isTailscaleAddress) == true
        }

        /**
         * Which server answers at [url], asked without any sign-in (GET /setup): its id ([SystemStatus.serverId]), or
         * null when it gave none (a server before 0.55, or not a Flexarr server at all). Throws
         * [FlexarrException.Unreachable] when nothing answered there, which says nothing either way.
         */
        suspend fun serverIdAt(url: HttpUrl, http: OkHttpClient = sharedHttp.newBuilder().callTimeout(5, TimeUnit.SECONDS).build()): String? =
            try {
                FlexarrClient(url, http = http).get<SetupStatus>("setup").serverId
            } catch (e: FlexarrException.Unreachable) {
                throw e
            } catch (_: FlexarrException) {
                null
            }

        /** Whether the server's problem details carry [name] as true (`parentPin`, `kidsLock`). */
        internal fun problemFlag(text: String, name: String): Boolean =
            runCatching { (json.parseToJsonElement(text).jsonObject[name] as? JsonPrimitive)?.content == "true" }.getOrDefault(false)

        /** The `detail` of RFC 7807 problem details, alone: what the server says went wrong, in its words. */
        internal fun problemDetail(text: String): String? =
            runCatching { (json.parseToJsonElement(text).jsonObject["detail"] as? JsonPrimitive)?.takeIf { it.isString }?.content }.getOrNull()?.takeIf { it.isNotBlank() }

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
