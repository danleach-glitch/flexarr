package io.github.flexarr.core

import kotlinx.serialization.Serializable
import kotlinx.serialization.builtins.ListSerializer
import okhttp3.RequestBody.Companion.toRequestBody
import java.text.NumberFormat
import java.time.Duration
import java.time.Instant
import java.util.Locale

// Trakt, per profile, as FlexarrKit's Trakt.swift: connect by a code entered at trakt.tv/activate, how the sync is
// going, the first import, the profile's Trakt lists, and disconnecting. Absent from servers before 0.57, which answer
// trakt/status with the web app's page; kids profiles get a 403. Either way [traktStatus] is null and the apps show
// nothing.

/** Where a connect is: waiting for the code to be entered, or how it ended. */
@Serializable(with = TraktConnectState.Serializer::class)
enum class TraktConnectState(override val raw: String) : ForwardCompatibleEnum {
    Waiting("Waiting"),
    Connected("Connected"),
    Expired("Expired"),
    Denied("Denied"),

    /** That Trakt account is already connected to another profile here. */
    InUse("InUse"),
    Failed("Failed"),
    Unknown("");

    /** Why a connect ended without connecting, in a few words; null while waiting or once connected. */
    val problem: String?
        get() = when (this) {
            Waiting, Connected -> null
            Expired -> "The code ran out. Try again."
            Denied -> "Trakt said no."
            InUse -> "That Trakt account is connected to another profile."
            Failed, Unknown -> "That didn’t work. Try again."
        }

    object Serializer : ForwardCompatibleEnumSerializer<TraktConnectState>("TraktConnectState", entries, Unknown)
}

/** Whether the profile has Trakt: not connected, connected, or connected but Trakt has since signed Flexarr out. */
@Serializable(with = TraktAccountState.Serializer::class)
enum class TraktAccountState(override val raw: String) : ForwardCompatibleEnum {
    Disconnected("disconnected"),
    Connected("connected"),
    NeedsSignIn("needsSignIn"),
    Unknown("");

    object Serializer : ForwardCompatibleEnumSerializer<TraktAccountState>("TraktAccountState", entries, Unknown)
}

/** A connect under way (the code to enter, and where), or how one just ended. */
@Serializable
data class TraktPendingConnect(
    val state: TraktConnectState = TraktConnectState.Unknown,
    val userCode: String = "",
    val verificationUrl: String = "",
    /** What to put in a QR code: Trakt's activation page. */
    val qrUrl: String = "",
    val expiresAt: String? = null,
    val error: String? = null,
) {
    /** "trakt.tv/activate" for "https://trakt.tv/activate". */
    val shortVerificationUrl: String get() = verificationUrl.replace(Regex("^https?://"), "").trimEnd('/')
}

@Serializable
data class TraktStatus(
    /** The server can offer Trakt at all. */
    val available: Boolean = false,
    val state: TraktAccountState = TraktAccountState.Unknown,
    val username: String? = null,
    val connectedAt: String? = null,
    val lastSyncAt: String? = null,
    val lastError: String? = null,
    val watchlistFull: Boolean = false,
    val importedAt: String? = null,
    val importing: Boolean = false,
    val queued: Int = 0,
    val pending: TraktPendingConnect? = null,
) {
    val isConnected: Boolean get() = state == TraktAccountState.Connected

    /** A code is waiting to be entered, or the import is running: worth asking again in a moment. */
    val isBusy: Boolean get() = pending?.state == TraktConnectState.Waiting || importing

    /** The code to show, while one is waiting. */
    val waitingCode: TraktPendingConnect? get() = pending?.takeIf { it.state == TraktConnectState.Waiting }

    val lastSync: Instant? get() = lastSyncAt?.let { runCatching { InstantSerializer.parseInstant(it) }.getOrNull() }

    /** When the history came across, once it has. */
    val imported: Instant? get() = importedAt?.let { runCatching { InstantSerializer.parseInstant(it) }.getOrNull() }

    /** "Synced 5 minutes ago", or "Not synced yet". */
    fun syncedText(now: Instant = Instant.now()): String =
        lastSync?.let { "Synced " + relativeTime(it, now).replaceFirstChar(Char::lowercaseChar) } ?: "Not synced yet"

    /** What Settings' Trakt row says: the account, "Signed out", or "Off". */
    val settingsValue: String
        get() = when (state) {
            TraktAccountState.Connected -> username ?: "On"
            TraktAccountState.NeedsSignIn -> "Signed out"
            else -> "Off"
        }

    /** The label of the button that connects: again, by name, when Trakt signed Flexarr out. */
    val connectLabel: String get() = if (state == TraktAccountState.NeedsSignIn) "Connect ${username ?: "Trakt"} again" else "Connect Trakt"

    /** The line under the connect button: why it's needed again, how the last try ended, or what it does. */
    val connectNote: String
        get() = when {
            state == TraktAccountState.NeedsSignIn -> "Trakt signed Flexarr out."
            else -> pending?.takeIf { it.state.problem != null }?.let { it.error?.takeIf(String::isNotBlank) ?: it.state.problem }
                ?: "Each profile connects its own account."
        }

    /** Something to say about how syncing is going, if anything. */
    val syncNote: String? get() = lastError ?: if (watchlistFull) "Trakt’s watchlist is full. New titles stay on Flexarr’s list." else null
}

/** What the first history import would do, shown before it's confirmed. */
@Serializable
data class TraktImportPreview(
    val traktFilms: Int = 0,
    val traktEpisodes: Int = 0,
    val inLibrary: Int = 0,
    val newToFlexarr: Int = 0,
    val notOnServer: Int = 0,
    val unmatched: Int = 0,
    val toTrakt: Int = 0,
) {
    /** "Trakt has 12 films and 340 episodes". */
    fun summary(locale: Locale = Locale.getDefault()): String =
        "Trakt has ${count(traktFilms, "film", locale)} and ${count(traktEpisodes, "episode", locale)}"

    /** What changes, in a line or two. */
    fun details(locale: Locale = Locale.getDefault()): String {
        val n = { value: Int -> NumberFormat.getIntegerInstance(locale).format(value) }
        val here = if (inLibrary == 0) "None are here yet" else "${n(inLibrary)} are here" + if (newToFlexarr > 0) ", ${n(newToFlexarr)} not yet watched in Flexarr" else ""
        val parts = mutableListOf(here)
        if (notOnServer > 0) parts += "${n(notOnServer)} will show as watched if they arrive"
        if (toTrakt > 0) parts += "${count(toTrakt, "play", locale)} from Flexarr ${if (toTrakt == 1) "goes" else "go"} to Trakt"
        return parts.joinToString(". ") + "."
    }

    private fun count(n: Int, one: String, locale: Locale) = "${NumberFormat.getIntegerInstance(locale).format(n)} ${if (n == 1) one else one + "s"}"
}

/** One of the profile's Trakt lists, its own or one it liked, and whether it comes across as a collection. */
@Serializable
data class TraktList(
    /** "{user slug}/{list id}": what [saveTraktLists] takes. */
    val id: String,
    val name: String = "",
    /** Whose list it is, for one the profile liked; null for its own. */
    val owner: String? = null,
    val itemCount: Int = 0,
    val liked: Boolean = false,
    val selected: Boolean = false,
) {
    /** "24 titles", or "Someone · 24 titles" for a liked list (owner first, as on the web). */
    val detail: String
        get() = listOfNotNull(owner?.takeIf { liked && it.isNotBlank() }, "$itemCount ${if (itemCount == 1) "title" else "titles"}").joinToString(" · ")
}

@Serializable
internal data class TraktListsBody(val ids: List<String>)

/** "Just now", "5 minutes ago", "3 hours ago", "Yesterday", "4 days ago": for when Trakt last synced. */
fun relativeTime(then: Instant, now: Instant = Instant.now()): String {
    val ago = Duration.between(then, now)
    return when {
        ago.toMinutes() < 1 -> "Just now"
        ago.toMinutes() < 60 -> ago.toMinutes().let { "$it ${if (it == 1L) "minute" else "minutes"} ago" }
        ago.toHours() < 24 -> ago.toHours().let { "$it ${if (it == 1L) "hour" else "hours"} ago" }
        ago.toDays() < 2 -> "Yesterday"
        else -> "${ago.toDays()} days ago"
    }
}

/** The words the phone and TV Trakt pages share, so both (and the web and Apple apps) say the same thing. */
object TraktWords {
    const val GENERIC_ERROR = "That didn’t work. Try again."
    const val UNAVAILABLE = "Trakt isn’t available on this server."
    const val UNREACHABLE = "Couldn’t reach the server."
    const val ENTER_CODE = "Enter this code at trakt.tv/activate."
    const val WAITS = "Flexarr waits, so you can leave this."
    const val IMPORT = "Bring your Trakt history across"
    const val IMPORT_CONFIRM = "Bring across"
    const val IMPORTING = "Bringing your history across…"
    const val IMPORTED = "History brought across"
    const val LISTS_NOTE = "Chosen lists show in Collections, just for you."
    const val DISCONNECT_TITLE = "Disconnect Trakt?"
    const val DISCONNECT_TEXT = "What you’ve watched stays as it is, here and on Trakt."
    const val SYNCING = "Syncing…"

    /** The server's own words for a call that failed ("Couldn't reach Trakt."), or [GENERIC_ERROR]. */
    fun problem(error: Throwable): String = (error as? FlexarrException.Http)?.detail ?: GENERIC_ERROR

    /** "A B 1 2 C D": a code read out a character at a time. */
    fun spelled(code: String): String = code.toList().joinToString(" ")
}

// Client

/** Null from servers without Trakt (before 0.57: the web app's page, or a 404), and on kids profiles (403). */
suspend fun FlexarrClient.traktStatus(): TraktStatus? {
    val text = try {
        sendRaw("GET", "trakt/status")
    } catch (e: FlexarrException.Http) {
        if (e.status == 404 || e.status == 403) return null else throw e
    } catch (_: FlexarrException.UnexpectedResponse) {
        return null
    }
    return try {
        decodeText(TraktStatus.serializer(), text)
    } catch (_: FlexarrException.UnexpectedResponse) {
        null
    }
}

/** Asks Trakt for a code; the server waits for it to be entered, and [traktStatus] says how that goes. */
suspend fun FlexarrClient.connectTrakt(): TraktPendingConnect = decodeText(TraktPendingConnect.serializer(), send("POST", "trakt/connect"))

suspend fun FlexarrClient.cancelTraktConnect() {
    send("DELETE", "trakt/connect")
}

/** Trakt forgets the sign-in, and so does Flexarr; what's been watched stays, on both. */
suspend fun FlexarrClient.disconnectTrakt() {
    send("DELETE", "trakt")
}

/** What bringing the Trakt history across would do, without doing it. */
suspend fun FlexarrClient.previewTraktImport(): TraktImportPreview = decodeText(TraktImportPreview.serializer(), send("POST", "trakt/import"))

/** Brings the Trakt history across, in the background ([TraktStatus.importing] until it's done). */
suspend fun FlexarrClient.confirmTraktImport() {
    send("POST", "trakt/import", mapOf("confirm" to "true"))
}

/** Syncs now: the server answers once it's done (204), or at once (202) when asked again within a minute. */
suspend fun FlexarrClient.syncTraktNow() {
    // A whole sync round can take longer than an ordinary call's 20 seconds.
    sendBody("POST", "trakt/sync", emptyMap(), ByteArray(0).toRequestBody(null), timeoutSeconds = 90)
}

/** The profile's Trakt lists, its own and liked ones, saying which come across as collections. */
suspend fun FlexarrClient.traktLists(): List<TraktList> = decodeText(ListSerializer(TraktList.serializer()), send("GET", "trakt/lists"))

/** Chooses the lists that come across as collections; the rest go. */
suspend fun FlexarrClient.saveTraktLists(ids: List<String>) {
    send("PUT", "trakt/lists", body = FlexarrClient.json.encodeToString(TraktListsBody.serializer(), TraktListsBody(ids)))
}
