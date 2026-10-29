package io.github.flexarr.core

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.serialization.KSerializer
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.builtins.ListSerializer
import kotlinx.serialization.descriptors.PrimitiveKind
import kotlinx.serialization.descriptors.PrimitiveSerialDescriptor
import kotlinx.serialization.encoding.Decoder
import kotlinx.serialization.encoding.Encoder
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.RequestBody.Companion.toRequestBody
import java.io.File
import java.time.Instant
import java.time.format.DateTimeFormatter
import java.time.temporal.ChronoUnit
import java.util.concurrent.Executors
import kotlin.math.max
import kotlin.math.min
import kotlin.math.pow

// The app's own log, as the server takes it: warnings and errors sent by themselves in small batches, everything kept
// in a file on the device for the times someone sends it on purpose (Settings › Send diagnostics). It only ever goes to
// the person's own server. As FlexarrKit's ClientLogs.swift.

/** How serious a log entry is, named as the server's log levels are. */
@Serializable
enum class ClientLogLevel(val tag: String) {
    @SerialName("Debug") Debug("DEBUG"),
    @SerialName("Information") Information("INFO "),
    @SerialName("Warning") Warning("WARN "),
    @SerialName("Error") Error("ERROR");

    /** Sent to the server as they happen (the rest only with diagnostics). */
    val isSentAutomatically: Boolean get() = this >= Warning
}

/** An ISO 8601 time with milliseconds, as the server and FlexarrKit write it. */
internal object LogTimeSerializer : KSerializer<Instant> {
    override val descriptor = PrimitiveSerialDescriptor("LogTime", PrimitiveKind.STRING)
    override fun serialize(encoder: Encoder, value: Instant) = encoder.encodeString(format(value))
    override fun deserialize(decoder: Decoder): Instant = runCatching { Instant.parse(decoder.decodeString()) }.getOrDefault(Instant.now())

    fun format(value: Instant): String = DateTimeFormatter.ISO_INSTANT.format(value.truncatedTo(ChronoUnit.MILLIS))
}

/** One line of the log, as `POST /logs/client` wants it. */
@Serializable
data class ClientLogEntry(
    @Serializable(with = LogTimeSerializer::class) val timestamp: Instant = Instant.now(),
    val level: ClientLogLevel,
    val category: String,
    val message: String,
    val error: String? = null,
    val properties: Map<String, String>? = null,
) {
    /** The entry as a line of the log file: time, level, category, message, then the error and properties. */
    val line: String
        get() {
            val text = StringBuilder("${LogTimeSerializer.format(timestamp)} ${level.tag} [$category] $message")
            error?.let { text.append(" | error: ").append(it) }
            properties.orEmpty().toSortedMap().forEach { (key, value) -> text.append(" | ").append(key).append('=').append(value) }
            return text.toString().replace("\r\n", "\\n").replace("\n", "\\n")
        }

    /** What makes two entries the same thing happening again (the time and properties aside). */
    internal val repeatKey: String get() = "${level.name}|$category|$message|${error.orEmpty()}"
}

// Redaction

/**
 * Takes what mustn't leave the device out of a log message: the sign-in (API key, tokens), session ids (the stream
 * URLs' credential) and anything that looks like one, query strings, and passwords in URLs.
 */
data class LogRedactor(
    /** Known secrets (this device's API key or token, the profile's token, the push token), replaced wherever they appear. */
    val secrets: List<String> = emptyList(),
    /** Longer messages are cut here. */
    val maxLength: Int = 4000,
) {
    fun redact(text: String): String {
        var result = text
        for (secret in secrets) if (secret.length >= 6) result = result.replace(secret, PLACEHOLDER)
        // Query strings go whole, before named secrets leave a placeholder in one.
        result = userInfo.replace(result, "$1")
        result = query.replace(result, "$1")
        result = fragment.replace(result, "$1")
        result = namedSecret.replace(result) { "${it.groupValues[1]}${it.groupValues[2]}$PLACEHOLDER" }
        result = hexRun.replace(result, PLACEHOLDER)
        if (result.length > maxLength) result = result.take(maxLength) + "…"
        return result
    }

    /** The entry with its message, error and properties redacted. */
    fun redact(entry: ClientLogEntry): ClientLogEntry = entry.copy(
        category = redact(entry.category),
        message = redact(entry.message),
        error = entry.error?.let(::redact),
        properties = entry.properties?.mapValues { redact(it.value) },
    )

    companion object {
        const val PLACEHOLDER = "<redacted>"

        /** `api_key=…`, `X-Api-Key: …`, `"token": "…"`, `Authorization: Bearer …`. */
        private val namedSecret = Regex(
            """(?i)\b(x-api-key|x-flexarr-profile|api[_-]?key|access[_-]?token|refresh[_-]?token|device[_-]?token|push[_-]?token|token|password|passwd|pin|secret|authorization)(["']?\s*[:=]\s*["']?)(?:bearer\s+)?[^\s&"',;})\]]+""",
        )
        /** The user and password in `scheme://user:pass@host`. */
        private val userInfo = Regex("""(?i)(\b[a-z][a-z0-9+.-]*://)[^\s/@"'<>]+@""")
        /** A query string after a URL or a path (anything with a slash before the `?`). */
        private val query = Regex("""([^\s?#"'<>]*/[^\s?#"'<>]*)\?[^\s"'<>]+""")
        /** A URL's fragment. */
        private val fragment = Regex("""(?i)(\b[a-z][a-z0-9+.-]*://[^\s#"'<>]*)#[^\s"'<>]+""")
        /** 32 or more hex digits in a row: API keys, tokens, session ids and push tokens all look like this. */
        private val hexRun = Regex("[0-9a-fA-F]{32,}")
    }
}

// The queue of warnings and errors to send

/**
 * Warnings and errors waiting to go to the server, in order. The same thing happening over and over is sent once
 * (with how many more times it happened), sending is capped per ten minutes, and what can't be sent waits, up to
 * [capacity] entries, oldest dropped first. Only [pending] is kept across launches. Not thread-safe: [ClientLogUploader]
 * guards it.
 */
class ClientLogQueue(
    pending: List<ClientLogEntry> = emptyList(),
    /** At most this many wait; past it the oldest go. */
    val capacity: Int = 500,
    /** The same entry again within this long (ms) is counted rather than queued. */
    val repeatWindowMs: Long = 300_000,
    /** At most this many entries are sent per [rateWindowMs]. */
    val rateLimit: Int = 300,
    val rateWindowMs: Long = 600_000,
) {
    private val queued = pending.toMutableList()
    val pending: List<ClientLogEntry> get() = queued.toList()

    /** Sends in a row that failed (no connection, server error), for the backoff. */
    var failures = 0
        private set

    /** Entries lost to the capacity since launch. */
    var overflowed = 0
        private set

    /** The first entries of [pending], out being sent: left alone until the answer comes. */
    private var inFlight = 0
    private val lastQueued = mutableMapOf<String, Instant>()
    private val leftOut = mutableMapOf<String, Int>()
    private val sent = mutableListOf<Pair<Instant, Int>>()

    /**
     * Queues a warning or error. False when it wasn't queued: not a warning or error, or a repeat (counted on the one
     * already waiting, or on the next one queued).
     */
    fun add(entry: ClientLogEntry): Boolean {
        if (!entry.level.isSentAutomatically) return false
        val key = entry.repeatKey
        val waiting = (queued.size - 1 downTo inFlight).firstOrNull { queued[it].repeatKey == key }
        if (waiting != null) {
            queued[waiting] = queued[waiting].copy(properties = counting(queued[waiting].properties, 1))
            return false
        }
        val last = lastQueued[key]
        if (last != null && entry.timestamp.toEpochMilli() - last.toEpochMilli() < repeatWindowMs) {
            leftOut[key] = (leftOut[key] ?: 0) + 1
            return false
        }
        var added = entry
        leftOut.remove(key)?.let { added = added.copy(properties = counting(added.properties, it)) }
        lastQueued[key] = added.timestamp
        if (lastQueued.size > 500) lastQueued.entries.removeAll { added.timestamp.toEpochMilli() - it.value.toEpochMilli() >= repeatWindowMs }
        queued.add(added)
        val over = queued.size - capacity
        if (over > 0) {
            val start = min(inFlight, queued.size)
            val end = min(start + over, queued.size)
            repeat(end - start) { queued.removeAt(start) }
            overflowed += end - start
        }
        return true
    }

    /** The next entries to send (none while some are out, or while the rate cap is reached), marked as out. */
    fun nextBatch(now: Instant = Instant.now()): List<ClientLogEntry> {
        if (inFlight != 0) return emptyList()
        sent.removeAll { now.toEpochMilli() - it.first.toEpochMilli() >= rateWindowMs }
        val allowance = rateLimit - sent.sumOf { it.second }
        val count = minOf(BATCH_SIZE, max(0, allowance), queued.size)
        inFlight = count
        return queued.take(count)
    }

    /** The batch out arrived. */
    fun delivered(now: Instant = Instant.now()) {
        sent.add(now to inFlight)
        repeat(inFlight) { queued.removeAt(0) }
        inFlight = 0
        failures = 0
    }

    /** The server refused the batch out (it's malformed, or too much is being sent): it's let go, not retried. */
    fun refused() {
        repeat(inFlight) { queued.removeAt(0) }
        inFlight = 0
    }

    /** The batch out couldn't be sent: it stays, and the next try waits longer. */
    fun failed() {
        inFlight = 0
        failures++
    }

    /** How long (ms) to wait after [failures] failed sends: 10 seconds, doubling, at most 15 minutes. */
    val retryDelayMs: Long
        get() = if (failures == 0) 0 else min(10_000.0 * 2.0.pow(min(failures, 12) - 1), 900_000.0).toLong()

    /** While the rate cap holds entries back, how long (ms) until some can go. */
    fun rateWaitMs(now: Instant = Instant.now()): Long? {
        if (queued.isEmpty() || inFlight != 0) return null
        val live = sent.filter { now.toEpochMilli() - it.first.toEpochMilli() < rateWindowMs }
        if (live.sumOf { it.second } < rateLimit) return null
        val oldest = live.minOfOrNull { it.first.toEpochMilli() } ?: return null
        return max(1_000, rateWindowMs - (now.toEpochMilli() - oldest))
    }

    companion object {
        const val BATCH_SIZE = 100

        /** [properties] with `repeats` (how many more times it happened) raised by [more]. */
        private fun counting(properties: Map<String, String>?, more: Int): Map<String, String> {
            val result = properties.orEmpty().toMutableMap()
            result["repeats"] = ((result["repeats"]?.toIntOrNull() ?: 0) + more).toString()
            return result
        }
    }
}

// Sending

/** How a batch sent to the server went. */
enum class ClientLogDelivery {
    Delivered,
    /** Refused (4xx, 429 included): let go rather than retried. */
    Refused,
    /** Didn't get there (no connection, a server error): tried again later. */
    Failed,
    /** Not signed in: kept until there's a server to send to. */
    Unavailable;

    companion object {
        fun of(error: Throwable): ClientLogDelivery = when (error) {
            is FlexarrException.Unauthorized -> Refused
            is FlexarrException.Http -> if (error.status in 400..499) Refused else Failed
            else -> Failed
        }
    }
}

/**
 * Sends warnings and errors to the server a few seconds after they happen, in batches, keeping what couldn't be sent
 * (in [storage] too, for the next launch) and trying again with backoff. Its own failures go to [systemLog] only,
 * never back into itself.
 */
class ClientLogUploader(
    private val storage: File?,
    private val debounceMs: Long = 5_000,
    private val systemLog: (String) -> Unit = {},
    private val send: suspend (List<ClientLogEntry>) -> ClientLogDelivery,
) {
    private val lock = Mutex()
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private var queue = ClientLogQueue(storage?.let(::load).orEmpty())
    private var scheduled: Job? = null
    private var flushing = false

    suspend fun pending(): List<ClientLogEntry> = lock.withLock { queue.pending }

    fun add(entry: ClientLogEntry) {
        scope.launch {
            val added = lock.withLock { queue.add(entry).also { if (it) save() } }
            if (added) schedule(debounceMs)
        }
    }

    /** Sends what's waiting soon: there's a server to send to now, say. */
    fun kick() {
        scope.launch { if (lock.withLock { queue.pending.isNotEmpty() }) schedule(debounceMs) }
    }

    /** Sends what's waiting now, batch after batch, until it's all gone or something stops it. */
    suspend fun flush() {
        lock.withLock {
            scheduled?.cancel()
            scheduled = null
            if (flushing) return
            flushing = true
        }
        try {
            while (true) {
                val batch = lock.withLock { queue.nextBatch() }
                if (batch.isEmpty()) {
                    lock.withLock { queue.rateWaitMs() }?.let { schedule(it) }
                    return
                }
                when (send(batch)) {
                    ClientLogDelivery.Delivered -> lock.withLock { queue.delivered(); save() }
                    ClientLogDelivery.Refused -> {
                        systemLog("The server refused ${batch.size} log entries; dropped")
                        lock.withLock { queue.refused(); save() }
                    }
                    ClientLogDelivery.Unavailable -> {
                        lock.withLock { queue.failed() }
                        return
                    }
                    ClientLogDelivery.Failed -> {
                        val wait = lock.withLock { queue.failed(); save(); queue.retryDelayMs }
                        systemLog("Couldn't send ${batch.size} log entries; trying again in ${wait / 1000}s")
                        schedule(wait)
                        return
                    }
                }
            }
        } finally {
            lock.withLock { flushing = false }
        }
    }

    private suspend fun schedule(delayMs: Long) {
        lock.withLock {
            if (scheduled != null) return
            scheduled = scope.launch {
                delay(delayMs)
                lock.withLock { scheduled = null }
                flush()
            }
        }
    }

    private fun save() {
        val storage = storage ?: return
        runCatching {
            val pending = queue.pending
            if (pending.isEmpty()) storage.delete()
            else storage.writeText(FlexarrClient.json.encodeToString(ListSerializer(ClientLogEntry.serializer()), pending))
        }.onFailure { systemLog("Couldn't keep the log entries waiting to be sent: ${it.message}") }
    }

    private companion object {
        fun load(file: File): List<ClientLogEntry>? = runCatching {
            FlexarrClient.json.decodeFromString(ListSerializer(ClientLogEntry.serializer()), file.readText())
        }.getOrNull()
    }
}

// The log file

/**
 * Every entry, as lines in a file that rolls over at [maxFileBytes] (the one before is kept), so diagnostics have the
 * recent past even after a relaunch or a crash. Written on a thread of its own, never on the caller's.
 */
class ClientLogFile(val directory: File, private val maxFileBytes: Long = 1_000_000) {
    private val writer = Executors.newSingleThreadExecutor { Thread(it, "flexarr-log").apply { isDaemon = true } }

    val current: File get() = File(directory, "flexarr.log")
    val previous: File get() = File(directory, "flexarr.1.log")

    fun append(line: String) {
        writer.execute { write(line) }
    }

    /** Both files, oldest line first, once everything appended so far is written. */
    fun contents(): String = writer.submit<String> {
        (previous.takeIf { it.exists() }?.readText().orEmpty()) + (current.takeIf { it.exists() }?.readText().orEmpty())
    }.get()

    private fun write(line: String) {
        runCatching {
            directory.mkdirs()
            val data = (line + "\n").toByteArray()
            if (current.exists() && current.length() > 0 && current.length() + data.size > maxFileBytes) {
                previous.delete()
                current.renameTo(previous)
            }
            current.appendBytes(data)
        }
    }
}

// Diagnostics

object ClientDiagnostics {
    /** The most the server takes. */
    const val MAX_BYTES = 5 * 1024 * 1024

    /** The header, a blank line, then the log, its oldest lines left out as needed to fit [maxBytes]. */
    fun body(header: String, log: String, maxBytes: Int = MAX_BYTES): String {
        val head = if (header.endsWith("\n")) header + "\n" else header + "\n\n"
        var budget = maxBytes - head.toByteArray().size
        if (budget <= 0) return String(head.toByteArray().copyOf(maxBytes))
        if (log.toByteArray().size <= budget) return head + log
        // Keep the newest lines that fit, with a line saying how many went.
        val lines = log.split("\n")
        budget -= 80 // the note
        val kept = ArrayDeque<String>()
        for (line in lines.asReversed()) {
            val cost = line.toByteArray().size + 1
            if (budget - cost < 0) break
            budget -= cost
            kept.addFirst(line)
        }
        return head + "… ${lines.size - kept.size} older lines left out to fit\n" + kept.joinToString("\n")
    }
}

// The client's part

@Serializable
private data class ClientLogBatch(val platform: String, val appVersion: String, val entries: List<ClientLogEntry>)

@Serializable
private data class DiagnosticsReceipt(val id: String)

/**
 * Warnings and errors from this app (at most [ClientLogQueue.BATCH_SIZE]). The server answers 429 when too much is
 * being sent. Who sent them, the server knows from the sign-in.
 */
suspend fun FlexarrClient.sendLogs(platform: String, appVersion: String, entries: List<ClientLogEntry>) {
    send("POST", "logs/client", body = FlexarrClient.json.encodeToString(ClientLogBatch.serializer(), ClientLogBatch(platform, appVersion, entries)))
}

/** The app's whole log, sent on purpose from Settings, for looking into a problem; returns its id on the server. */
suspend fun FlexarrClient.sendDiagnostics(platform: String, appVersion: String, note: String?, text: String): String {
    val query = buildMap {
        put("platform", platform)
        put("appVersion", appVersion)
        if (!note.isNullOrEmpty()) put("note", note)
    }
    val answer = sendBody("POST", "logs/client/diagnostics", query, text.toRequestBody("text/plain; charset=utf-8".toMediaType()), timeoutSeconds = 120)
    return decodeText(DiagnosticsReceipt.serializer(), answer).id
}
