package io.github.flexarr.core

import io.github.flexarr.core.i18n.t
import kotlinx.serialization.Serializable
import kotlinx.serialization.builtins.ListSerializer
import okhttp3.RequestBody.Companion.toRequestBody
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
            Expired -> t("The code ran out. Try again.")
            Denied -> t("Trakt said no.")
            InUse -> t("That Trakt account is connected to another profile.")
            Failed, Unknown -> TraktWords.GENERIC_ERROR
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

    /**
     * Where Copy code and open Trakt goes: the server's [verificationUrl] when it's an https page on trakt.tv, else
     * Trakt's own activation page, so a server can't send someone to a look-alike sign-in page.
     */
    val openableVerificationUrl: String get() = if (isTraktPage(verificationUrl)) verificationUrl else TRAKT_ACTIVATE

    companion object {
        const val TRAKT_ACTIVATE = "https://trakt.tv/activate"

        internal fun isTraktPage(url: String): Boolean {
            val uri = runCatching { java.net.URI(url) }.getOrNull() ?: return false
            val host = uri.host?.lowercase() ?: return false
            return uri.scheme.equals("https", ignoreCase = true) && uri.rawUserInfo == null &&
                (uri.port == -1 || uri.port == 443) && (host == "trakt.tv" || host.endsWith(".trakt.tv"))
        }
    }
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
    /** Why the last history import didn't finish, if it didn't. Absent from servers before 0.57's last builds. */
    val importError: String? = null,
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
        lastSync?.let { syncedAgo(it, now) } ?: t("Not synced yet")

    /** What Settings' Trakt row says: the account, "Signed out", or "Off". */
    val settingsValue: String
        get() = when (state) {
            TraktAccountState.Connected -> username ?: t("On")
            TraktAccountState.NeedsSignIn -> t("Signed out")
            else -> t("Off")
        }

    /** The label of the button that connects: again, by name, when Trakt signed Flexarr out. */
    val connectLabel: String get() = if (state == TraktAccountState.NeedsSignIn) t("Connect {account} again", "account" to (username ?: "Trakt")) else t("Connect Trakt")

    /** The line under the connect button: why it's needed again, how the last try ended, or what it does. */
    val connectNote: String
        get() = when {
            state == TraktAccountState.NeedsSignIn -> t("Trakt signed Flexarr out.")
            else -> pending?.takeIf { it.state.problem != null }?.let { it.error?.takeIf(String::isNotBlank) ?: it.state.problem }
                ?: t("Each profile connects its own account.")
        }

    /** Something to say about how syncing is going, if anything. */
    val syncNote: String? get() = lastError ?: if (watchlistFull) t("Trakt’s watchlist is full. New titles stay on Flexarr’s list.") else null
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
    @Suppress("UNUSED_PARAMETER")
    fun summary(locale: Locale = io.github.flexarr.core.i18n.Localization.locale): String =
        t("Trakt has {films, plural, one {# movie} other {# movies}} and {episodes, plural, one {# episode} other {# episodes}}", "films" to traktFilms, "episodes" to traktEpisodes)

    /** What changes, in a line or two. */
    @Suppress("UNUSED_PARAMETER")
    fun details(locale: Locale = io.github.flexarr.core.i18n.Localization.locale): String {
        val parts = mutableListOf(
            when {
                inLibrary == 0 -> t("None are here yet.")
                newToFlexarr > 0 -> t("{count} are here, {new} not yet watched in Flexarr.", "count" to inLibrary, "new" to newToFlexarr)
                else -> t("{count} are here.", "count" to inLibrary)
            },
        )
        if (notOnServer > 0) parts += t("{count} will show as watched if they arrive.", "count" to notOnServer)
        if (toTrakt > 0) parts += t("{count, plural, one {# play from Flexarr goes to Trakt.} other {# plays from Flexarr go to Trakt.}}", "count" to toTrakt)
        return parts.joinToString(" ")
    }
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
        get() = listOfNotNull(owner?.takeIf { liked && it.isNotBlank() }, t("{count, plural, one {# title} other {# titles}}", "count" to itemCount)).joinToString(", ")
}

@Serializable
internal data class TraktListsBody(val ids: List<String>)

/** "Just now", "5 minutes ago", "3 hours ago", "Yesterday", "4 days ago": for when Trakt last synced. */
fun relativeTime(then: Instant, now: Instant = Instant.now()): String {
    val ago = Duration.between(then, now)
    return when {
        ago.toMinutes() < 1 -> t("Just now")
        ago.toMinutes() < 60 -> t("{count, plural, one {# minute ago} other {# minutes ago}}", "count" to ago.toMinutes())
        ago.toHours() < 24 -> t("{count, plural, one {# hour ago} other {# hours ago}}", "count" to ago.toHours())
        ago.toDays() < 2 -> t("Yesterday")
        else -> t("{count, plural, one {# day ago} other {# days ago}}", "count" to ago.toDays())
    }
}

/** "Synced 5 minutes ago": [relativeTime] in a sentence of its own, so each language can word it. */
private fun syncedAgo(then: Instant, now: Instant): String {
    val ago = Duration.between(then, now)
    return when {
        ago.toMinutes() < 1 -> t("Synced just now")
        ago.toMinutes() < 60 -> t("{count, plural, one {Synced # minute ago} other {Synced # minutes ago}}", "count" to ago.toMinutes())
        ago.toHours() < 24 -> t("{count, plural, one {Synced # hour ago} other {Synced # hours ago}}", "count" to ago.toHours())
        ago.toDays() < 2 -> t("Synced yesterday")
        else -> t("{count, plural, one {Synced # day ago} other {Synced # days ago}}", "count" to ago.toDays())
    }
}

/** The words the phone and TV Trakt pages share, so both (and the web and Apple apps) say the same thing. */
object TraktWords {
    val GENERIC_ERROR: String get() = t("That didn’t work. Try again.")
    val UNAVAILABLE: String get() = t("Trakt isn’t available on this server.")
    val UNREACHABLE: String get() = t("Couldn’t reach the server.")
    val TRY_AGAIN: String get() = t("Try again")
    val ENTER_CODE: String get() = t("Enter this code at {link}.", "link" to "trakt.tv/activate")
    val WAITS: String get() = t("Flexarr waits, so you can leave this.")
    val IMPORT: String get() = t("Bring your Trakt history across")
    val IMPORT_CONFIRM: String get() = t("Bring across")
    val IMPORTING: String get() = t("Bringing your history across…")
    val IMPORTED: String get() = t("History brought across")
    val LISTS_NOTE: String get() = t("Chosen lists show in Collections, just for you.")
    val DISCONNECT_TITLE: String get() = t("Disconnect Trakt?")
    val DISCONNECT_TEXT: String get() = t("What you’ve watched stays as it is, here and on Trakt.")
    val SYNCING: String get() = t("Syncing…")

    /**
     * The server's own words for a call that failed (its problem details' `detail`, never the `title`), [UNREACHABLE]
     * when the server couldn't be reached at all, or [GENERIC_ERROR].
     */
    fun problem(error: Throwable): String = when (error) {
        is FlexarrException.Http -> error.detail?.takeIf(String::isNotBlank) ?: GENERIC_ERROR
        is FlexarrException.Unreachable, is java.io.IOException -> UNREACHABLE
        else -> GENERIC_ERROR
    }

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
