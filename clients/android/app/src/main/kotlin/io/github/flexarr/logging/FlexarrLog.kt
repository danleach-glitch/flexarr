package io.github.flexarr.logging

import android.content.Context
import android.util.Log
import io.github.flexarr.BuildConfig
import io.github.flexarr.core.ClientDiagnostics
import io.github.flexarr.core.ClientLogDelivery
import io.github.flexarr.core.ClientLogEntry
import io.github.flexarr.core.ClientLogFile
import io.github.flexarr.core.ClientLogLevel
import io.github.flexarr.core.ClientLogUploader
import io.github.flexarr.core.FlexarrClient
import io.github.flexarr.core.FlexarrException
import io.github.flexarr.core.LogRedactor
import io.github.flexarr.core.sendDiagnostics
import io.github.flexarr.core.sendLogs
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.File
import kotlin.concurrent.thread

/**
 * One part of the app's log (playback, music, downloads…). Every entry goes to Logcat as before, and to a file on the
 * device that Settings › Send diagnostics sends; warnings and errors are also sent to the server by themselves (see
 * [LogHub]). Messages are redacted first, so sign-ins, session ids and query strings never leave. As the Apple apps'
 * FlexarrLog.
 */
class FlexarrLog(val category: String) {
    fun debug(message: String, error: Throwable? = null, properties: Map<String, String?> = emptyMap()) =
        write(ClientLogLevel.Debug, message, error, properties)

    fun info(message: String, error: Throwable? = null, properties: Map<String, String?> = emptyMap()) =
        write(ClientLogLevel.Information, message, error, properties)

    fun warning(message: String, error: Throwable? = null, properties: Map<String, String?> = emptyMap()) =
        write(ClientLogLevel.Warning, message, error, properties)

    /** With an [error], also an error report when the server has them on: the error's kind only, never the message. */
    fun error(message: String, error: Throwable? = null, properties: Map<String, String?> = emptyMap()) {
        write(ClientLogLevel.Error, message, error, properties)
        if (error != null) ErrorReports.capture(error, category)
    }

    private fun write(level: ClientLogLevel, message: String, error: Throwable?, properties: Map<String, String?>) {
        val entry = LogHub.record(
            ClientLogEntry(
                level = level,
                category = category,
                message = message,
                error = error?.let(::describe),
                properties = properties.filterValues { it != null }.mapValues { it.value!! }.ifEmpty { null },
            ),
        )
        val text = StringBuilder(entry.message)
        entry.error?.let { text.append(" | error: ").append(it) }
        entry.properties.orEmpty().toSortedMap().forEach { (key, value) -> text.append(" | ").append(key).append('=').append(value) }
        val tag = "Flexarr.$category"
        when (level) {
            ClientLogLevel.Debug -> Log.d(tag, text.toString())
            ClientLogLevel.Information -> Log.i(tag, text.toString())
            ClientLogLevel.Warning -> Log.w(tag, text.toString())
            ClientLogLevel.Error -> Log.e(tag, text.toString())
        }
    }

    /** What went wrong, and its type, which says more than the sentence. */
    private fun describe(error: Throwable): String {
        val cause = error.cause?.let { " ← ${it.javaClass.simpleName}" }.orEmpty()
        return "${error.message ?: "no message"} (${error.javaClass.simpleName}$cause)"
    }
}

/**
 * Where every [FlexarrLog] entry goes: redacted, then the file on the device and, for warnings and errors, the uploader
 * that sends them to the signed-in server. Never sends anywhere else.
 */
object LogHub {
    const val PLATFORM = "android"

    private lateinit var file: ClientLogFile
    /** Made off the main thread (it reads what was waiting from last time); until then, entries wait in [early]. */
    @Volatile private var uploader: ClientLogUploader? = null
    private val early = mutableListOf<ClientLogEntry>()
    @Volatile private var redactor = LogRedactor()
    @Volatile private var client: FlexarrClient? = null

    fun start(context: Context) {
        val directory = File(context.cacheDir, "Logs").apply { mkdirs() }
        file = ClientLogFile(directory)
        thread(name = "flexarr-log-start", isDaemon = true) {
            val made = ClientLogUploader(File(directory, "waiting.json"), systemLog = { Log.i("Flexarr.logs", it) }) { entries ->
                val client = client ?: return@ClientLogUploader ClientLogDelivery.Unavailable
                try {
                    client.sendLogs(PLATFORM, BuildConfig.VERSION_NAME, entries)
                    ClientLogDelivery.Delivered
                } catch (e: FlexarrException) {
                    ClientLogDelivery.of(e)
                }
            }
            synchronized(early) {
                early.forEach(made::add)
                early.clear()
                uploader = made
            }
            if (client != null) made.kick()
        }
    }

    /** Redacts the entry, writes it to the file and queues it for the server if it's a warning or error. */
    internal fun record(entry: ClientLogEntry): ClientLogEntry {
        val kept = redactor.redact(entry)
        if (!::file.isInitialized) return kept
        file.append(kept.line)
        if (kept.level.isSentAutomatically) {
            uploader?.add(kept) ?: synchronized(early) { uploader?.add(kept) ?: early.add(kept) }
        }
        return kept
    }

    /** The sign-in's secrets, which nothing sent may contain. */
    val secrets: List<String> get() = redactor.secrets

    /** The server to send to (null when signed out), whose sign-in is also kept out of the log from now on. */
    fun signedIn(client: FlexarrClient?) {
        this.client = client
        redactor = redactor.copy(secrets = listOfNotNull(client?.apiKey, client?.profileToken).filter { it.isNotEmpty() })
        if (client != null) uploader?.kick()
    }

    /** Sends the whole log, with a few lines about this device on top, and returns the server's id for it. */
    suspend fun sendDiagnostics(client: FlexarrClient, header: String, note: String?): String {
        val body = withContext(Dispatchers.IO) { ClientDiagnostics.body(header, file.contents()) }
        return client.sendDiagnostics(PLATFORM, BuildConfig.VERSION_NAME, note?.trim()?.ifEmpty { null }, body)
    }
}
