package io.github.flexarr.core

import kotlinx.coroutines.runBlocking
import mockwebserver3.MockResponse
import mockwebserver3.MockWebServer
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.time.Instant

class ClientLogsTest {
    private fun entry(message: String, level: ClientLogLevel = ClientLogLevel.Warning, at: Long = 0) =
        ClientLogEntry(timestamp = Instant.ofEpochMilli(at), level = level, category = "test", message = message)

    @Test
    fun redactsSignInsQueriesAndHexRuns() {
        val redactor = LogRedactor(secrets = listOf("supersecret"))
        assertEquals("key <redacted> here", redactor.redact("key supersecret here"))
        assertEquals("GET http://host:8383/api/v1/stream.m3u8 failed", redactor.redact("GET http://host:8383/api/v1/stream.m3u8?api_key=abc&s=1 failed"))
        assertEquals("X-Api-Key: <redacted>", redactor.redact("X-Api-Key: abc123"))
        assertEquals("session <redacted>", redactor.redact("session 0123456789abcdef0123456789abcdef"))
        assertEquals("http://host/x", redactor.redact("http://dan:pw@host/x"))
    }

    @Test
    fun onlyWarningsAndErrorsAreQueued() {
        val queue = ClientLogQueue()
        assertFalse(queue.add(entry("hello", ClientLogLevel.Information)))
        assertTrue(queue.add(entry("broke", ClientLogLevel.Error)))
        assertEquals(1, queue.pending.size)
    }

    @Test
    fun repeatsAreCountedNotQueued() {
        val queue = ClientLogQueue()
        queue.add(entry("again"))
        queue.add(entry("again", at = 1_000))
        queue.add(entry("again", at = 2_000))
        assertEquals(1, queue.pending.size)
        assertEquals("2", queue.pending[0].properties?.get("repeats"))
    }

    @Test
    fun repeatsAfterSendingWaitForTheWindowAndSayHowMany() {
        val queue = ClientLogQueue()
        queue.add(entry("again"))
        queue.nextBatch(Instant.ofEpochMilli(0))
        queue.delivered(Instant.ofEpochMilli(0))
        assertFalse(queue.add(entry("again", at = 10_000)))
        assertTrue(queue.add(entry("again", at = 400_000)))
        assertEquals("1", queue.pending.single().properties?.get("repeats"))
    }

    @Test
    fun aFailedBatchStaysAndBacksOff() {
        val queue = ClientLogQueue()
        queue.add(entry("one"))
        assertEquals(1, queue.nextBatch().size)
        assertTrue(queue.nextBatch().isEmpty())
        queue.failed()
        assertEquals(1, queue.pending.size)
        assertEquals(10_000, queue.retryDelayMs)
        queue.nextBatch()
        queue.failed()
        assertEquals(20_000, queue.retryDelayMs)
    }

    @Test
    fun capacityDropsTheOldest() {
        val queue = ClientLogQueue(capacity = 2)
        queue.add(entry("a"))
        queue.add(entry("b"))
        queue.add(entry("c"))
        assertEquals(listOf("b", "c"), queue.pending.map { it.message })
        assertEquals(1, queue.overflowed)
    }

    @Test
    fun entriesEncodeAsTheServerWantsThem() {
        val text = FlexarrClient.json.encodeToString(ClientLogEntry.serializer(), entry("x").copy(timestamp = Instant.parse("2026-10-29T08:00:00.123456Z")))
        assertTrue(text, text.contains("\"timestamp\":\"2026-10-29T08:00:00.123Z\""))
        assertTrue(text, text.contains("\"level\":\"Warning\""))
    }

    @Test
    fun aLongLogKeepsItsNewestLines() {
        val log = (1..100).joinToString("\n") { "line $it ".padEnd(20, '.') }
        val body = ClientDiagnostics.body("Header", log, maxBytes = 600)
        assertTrue(body.startsWith("Header\n\n… "))
        assertTrue(body.endsWith(log.lines().last()))
        assertTrue(body.toByteArray().size <= 600)
    }

    @Test
    fun scrubberTakesOutAddressesPathsAndTitles() {
        val scrubber = ErrorReportScrubber()
        assertEquals("<email> at http://<server>/x", scrubber.scrub("dan@example.com at http://192.168.1.106:8383/x"))
        assertEquals("open <path> failed", scrubber.scrub("open /data/user/0/com.danleach.flexarr/files/a.mp4 failed"))
        assertEquals("couldn't play <quoted>", scrubber.scrub("couldn't play \"The Matrix\""))
        assertEquals("reach <server>", scrubber.scrub("reach mini.local:8383"))
    }

    @Test
    fun unreachableAndCancelledErrorsAreNotReported() {
        assertFalse(ErrorReportFilter.shouldReport(kotlinx.coroutines.CancellationException("gone")))
        assertFalse(ErrorReportFilter.shouldReport(FlexarrException.Unreachable(java.net.ConnectException())))
        assertTrue(ErrorReportFilter.shouldReport(IllegalStateException("bug")))
    }

    @Test
    fun logsAndDiagnosticsGoToTheirEndpoints() = runBlocking {
        MockWebServer().use { server ->
            server.start()
            server.enqueue(MockResponse.Builder().code(204).build())
            server.enqueue(MockResponse.Builder().code(200).body("""{"id":"abc"}""").addHeader("Content-Type", "application/json").build())
            val client = FlexarrClient(server.url("/"), apiKey = "k")
            client.sendLogs("android", "0.54.0", listOf(entry("x")))
            val logs = server.takeRequest()
            assertEquals("/api/v1/logs/client", logs.url.encodedPath)
            assertTrue(logs.body!!.utf8().contains("\"platform\":\"android\""))
            assertEquals("abc", client.sendDiagnostics("android", "0.54.0", "it froze", "the log"))
            val diagnostics = server.takeRequest()
            assertEquals("it froze", diagnostics.url.queryParameter("note"))
            assertTrue(diagnostics.headers["Content-Type"]!!.startsWith("text/plain"))
            assertEquals("the log", diagnostics.body!!.utf8())
        }
    }

    @Test
    fun theLogStartsAfreshForAnotherServerOnly() {
        val directory = java.nio.file.Files.createTempDirectory("logs").toFile()
        try {
            val owner = ClientLogOwner(java.io.File(directory, "server.txt"))
            assertFalse(owner.signedIn("https://home.example"))
            assertFalse(owner.signedIn("https://home.example"))
            assertTrue(owner.signedIn("https://other.example"))
            assertFalse(ClientLogOwner(java.io.File(directory, "server.txt")).signedIn("https://other.example"))
            // Signing out keeps it about that server.
            owner.signedOut()
            assertFalse(owner.signedIn("https://other.example"))
        } finally {
            directory.deleteRecursively()
        }
    }

    @Test
    fun aLogFromBeforeTheOwnerWasKeptGoesAtTheFirstSignInAfterSigningOut() {
        val directory = java.nio.file.Files.createTempDirectory("logs").toFile()
        try {
            // Updated while signed out: whichever server is signed in to next gets a fresh log.
            val owner = ClientLogOwner(java.io.File(directory, "server.txt"))
            owner.signedOut()
            assertTrue(owner.signedIn("https://new.example"))
            assertFalse(owner.signedIn("https://new.example"))
        } finally {
            directory.deleteRecursively()
        }
    }

    @Test
    fun clearingLetsGoOfWhatWaitsAndTheFile() = runBlocking {
        val directory = java.nio.file.Files.createTempDirectory("logs").toFile()
        try {
            val storage = java.io.File(directory, "waiting.json")
            val uploader = ClientLogUploader(storage, debounceMs = 60_000) { ClientLogDelivery.Unavailable }
            uploader.add(entry("about the old server"))
            repeat(50) { if (uploader.pending().isEmpty()) kotlinx.coroutines.delay(20) }
            assertEquals(1, uploader.pending().size)
            assertTrue(storage.exists())
            uploader.clear()
            assertTrue(uploader.pending().isEmpty())
            assertFalse(storage.exists())
            assertTrue(ClientLogUploader(storage) { ClientLogDelivery.Unavailable }.pending().isEmpty())

            val file = ClientLogFile(directory)
            file.append("watched The Matrix")
            file.clear()
            assertEquals("", file.contents())
            file.append("after")
            assertEquals("after\n", file.contents())
        } finally {
            directory.deleteRecursively()
        }
    }

    @Test
    fun anEntryMadeNowStillSaysWhen() {
        val text = FlexarrClient.json.encodeToString(ClientLogEntry.serializer(), ClientLogEntry(level = ClientLogLevel.Error, category = "c", message = "m"))
        assertTrue(text, text.contains("\"timestamp\":"))
    }
}
