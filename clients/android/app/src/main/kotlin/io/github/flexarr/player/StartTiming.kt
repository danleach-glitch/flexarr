package io.github.flexarr.player

import android.os.SystemClock
import io.github.flexarr.core.PlaybackInfo
import io.github.flexarr.logging.FlexarrLog

/**
 * One start, timed from Play to the first picture (or sound), and sent to the server's log (Settings › Logs) as one
 * Information entry; docs/fast-start.md lists its fields.
 */
class StartTiming(private val kind: String, private val pressedAt: Long = SystemClock.elapsedRealtime()) {
    private var answeredAt: Long? = null
    private var warmed = false
    private var method: String? = null
    private var playbackId: String? = null

    val isAnswered: Boolean get() = answeredAt != null

    /** The server's start answer came (at once, for a stream got ready ahead of Play). */
    fun answered(info: PlaybackInfo, warmed: Boolean) {
        if (answeredAt != null) return
        answeredAt = SystemClock.elapsedRealtime()
        this.warmed = warmed
        method = info.method.name
        playbackId = info.playbackId
    }

    /** The first picture or sound is here: the entry goes. */
    fun report(log: FlexarrLog, serverHost: String?, guest: Boolean) {
        val now = SystemClock.elapsedRealtime()
        // The id keeps each start its own entry, should anything group them by message.
        log.measured(
            "Playback ${playbackId ?: "unknown"} started",
            mapOf(
                "kind" to kind, "method" to (method ?: "unknown"), "warmed" to warmed.toString(),
                "remote" to (guest || looksRemote(serverHost)).toString(),
                "requestMs" to ((answeredAt ?: now) - pressedAt).coerceAtLeast(0).toString(),
                "firstFrameMs" to (now - pressedAt).coerceAtLeast(0).toString(), "playbackId" to playbackId,
            ),
        )
    }

    companion object {
        const val VIDEO = "video"
        const val BOOK = "book"

        /** Best guess whether the server is reached from away: not this device, a private address or a .local name. */
        fun looksRemote(host: String?): Boolean {
            val name = host?.trim('[', ']')?.lowercase() ?: return false
            if (name == "localhost" || name.endsWith(".local") || name == "::1") return false
            val parts = name.split('.').mapNotNull { it.toIntOrNull() }
            if (parts.size == 4 && name.count { it == '.' } == 3) {
                val (a, b) = parts[0] to parts[1]
                return !(a == 127 || a == 10 || (a == 172 && b in 16..31) || (a == 192 && b == 168) || (a == 169 && b == 254))
            }
            if (':' in name && (name.startsWith("fc") || name.startsWith("fd") || name.startsWith("fe80"))) return false
            return true
        }
    }
}
