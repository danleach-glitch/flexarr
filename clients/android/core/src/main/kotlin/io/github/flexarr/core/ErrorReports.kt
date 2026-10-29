package io.github.flexarr.core

import kotlinx.coroutines.CancellationException
import java.io.IOException

// Error reports (crashes and errors, to Sentry) are the server owner's choice, off unless turned on in the web app's
// Settings › Logs. What goes is scrubbed hard first: what went wrong and where in the code, never what was being
// watched, by whom, or where. The Sentry side lives in the app; this is the part that can be tested anywhere. As
// FlexarrKit's ErrorReports.swift.

/**
 * Which errors are worth a report. Some are expected: a coroutine cancelled because the viewer moved on, or the network
 * or server out of reach (a home server is often off or out of range). Those stay in the app's own log and its
 * diagnostics, but a report would only use up the quota and hide the real problems.
 */
object ErrorReportFilter {
    fun shouldReport(error: Throwable): Boolean = when (error) {
        is CancellationException -> false
        is FlexarrException.Unreachable -> false
        is IOException -> !FlexarrClient.couldNotConnect(error)
        else -> true
    }
}

/**
 * Takes everything that could say who or where out of an error report's text: what the log redacts (sign-ins, long hex
 * runs, query strings), then server addresses, IP addresses, email addresses, file paths and anything quoted (titles
 * usually are).
 */
class ErrorReportScrubber(private val redactor: LogRedactor = LogRedactor(maxLength = 2000)) {
    /** The text with nothing identifying left. */
    fun scrub(text: String): String {
        var result = redactor.redact(text)
        for ((pattern, template) in rules) result = pattern.replace(result, template)
        return result
    }

    companion object {
        /** In order: emails before hosts (an email has one), file URLs before other URLs, URLs before bare paths. */
        private val rules: List<Pair<Regex, String>> = listOf(
            Regex("""[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)+""") to "<email>",
            Regex("""(?i)\b(?:file|content)://\S+""") to "<path>",
            // A URL keeps its scheme and path; the host (and port) goes.
            Regex("""(?i)\b([a-z][a-z0-9+.-]*://)[^\s/"'<>?#]+""") to "$1<server>",
            Regex("""\b(?:25[0-5]|2[0-4]\d|1?\d?\d)(?:\.(?:25[0-5]|2[0-4]\d|1?\d?\d)){3}(?::\d+)?\b""") to "<ip>",
            Regex("""(?i)(?<![\w:])(?:[0-9a-f]{0,4}:){3,7}[0-9a-f]{0,4}(?![\w:])""") to "<ip>",
            // Names the network gives machines: mini.local, nas.lan, tv.tailnet.ts.net.
            Regex("""(?i)\b[a-z0-9-]+(?:\.[a-z0-9-]+)*\.(?:local|lan|home|internal|ts\.net)(?::\d+)?\b""") to "<server>",
            Regex("""~/\S*""") to "<path>",
            // An absolute path: at least two parts, not the path of a URL already dealt with.
            Regex("""(?<![\w:/>.])/(?:[^\s/"']+/)+[^\s"']*""") to "<path>",
            Regex(""""[^"\n]*"|“[^”\n]*”|‘[^’\n]*’|«[^»\n]*»|(?<!\w)'[^'\n]*'(?!\w)""") to "<quoted>",
        )

        /** Of the device, only what kind it is: never its name, its storage or memory, or an id tied to it. */
        val deviceKeys = setOf("model", "model_id", "family", "manufacturer", "brand", "arch", "archs", "simulator")
        val osKeys = setOf("name", "version", "build", "kernel_version", "rooted")
        val appKeys = setOf("app_version", "app_build", "build_type")

        /** Only the tags the app sets itself. */
        val tagKeys = setOf("category")
    }
}
