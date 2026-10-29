package io.github.flexarr.core

import kotlinx.coroutines.flow.toList
import kotlinx.coroutines.test.runTest
import mockwebserver3.MockResponse
import mockwebserver3.MockWebServer
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.time.Instant

class PairingTest {
    private val waiting = """{"code":"ABC-DEF","deviceName":"Living room","expiresAt":"2026-10-29T12:00:00Z","requestedFrom":"192.168.1.70","zone":"Local"}"""

    @Test
    fun `devices asking to connect arrive on the command stream beside commands, unknown events skipped`() = runTest {
        MockWebServer().use { server ->
            server.start()
            server.enqueue(
                MockResponse.Builder().addHeader("Content-Type", "text/event-stream").body(
                    ": hello\n\nevent: pairing\ndata: $waiting\n\n" +
                        "event: something-newer\ndata: {\"x\":1}\n\n" +
                        "event: pairing\ndata: not json\n\n" +
                        "event: command\ndata: {\"id\":\"1\",\"kind\":\"Pause\",\"sentAt\":\"2026-10-29T08:00:00Z\"}\n\n" +
                        "event: pairing-ended\ndata: {\"code\":\"ABC-DEF\"}\n\n",
                ).build(),
            )
            val events = FlexarrClient(server.url("/"), apiKey = "token").deviceEvents().toList()
            assertEquals(3, events.size)
            val pairing = (events[0] as DeviceEvent.Pairing).device
            assertEquals("ABC-DEF", pairing.code)
            assertEquals("Living room", pairing.deviceName)
            assertEquals(Instant.parse("2026-10-29T12:00:00Z"), pairing.expiresAt)
            assertEquals(DeviceCommandKind.Pause, (events[1] as DeviceEvent.Command).command.kind)
            assertEquals(DeviceEvent.PairingEnded("ABC-DEF"), events[2])
        }
    }

    @Test
    fun `the command stream alone carries only commands`() = runTest {
        MockWebServer().use { server ->
            server.start()
            server.enqueue(
                MockResponse.Builder().addHeader("Content-Type", "text/event-stream").body(
                    "event: pairing\ndata: $waiting\n\nevent: command\ndata: {\"id\":\"1\",\"kind\":\"Stop\",\"sentAt\":\"2026-10-29T08:00:00Z\"}\n\n",
                ).build(),
            )
            val commands = FlexarrClient(server.url("/"), apiKey = "token").commandStream().toList()
            assertEquals(listOf(DeviceCommandKind.Stop), commands.map { it.kind })
        }
    }

    @Test
    fun `where a device is asking from`() {
        val device = FlexarrClient.json.decodeFromString(WaitingDevice.serializer(), waiting)
        assertEquals("It’s asking from your home network (192.168.1.70).", device.whereFrom)
        assertEquals("It’s asking over Tailscale (100.64.0.2).", device.copy(zone = "Tailscale", requestedFrom = "100.64.0.2").whereFrom)
        assertEquals("It’s asking from your home network.", device.copy(requestedFrom = null).whereFrom)
        assertEquals("It’s asking from 203.0.113.9, outside your home network.", device.copy(zone = "Internet", requestedFrom = "203.0.113.9").whereFrom)
        // Only what's needed: a server that leaves out the address and zone.
        val bare = FlexarrClient.json.decodeFromString(
            WaitingDevice.serializer(),
            """{"code":"ABC-DEF","deviceName":"TV","expiresAt":"2026-10-29T12:00:00.123+00:00"}""",
        )
        assertEquals("It’s asking from outside your home network.", bare.whereFrom)
    }

    @Test
    fun `the waiting list is empty from servers before it`() = runTest {
        MockWebServer().use { server ->
            server.start()
            server.enqueue(MockResponse.Builder().addHeader("Content-Type", "application/json").body("[$waiting]").build())
            server.enqueue(MockResponse.Builder().code(404).build())
            val client = FlexarrClient(server.url("/"), apiKey = "token")
            assertEquals(listOf("ABC-DEF"), client.waitingDevices().map { it.code })
            assertEquals(emptyList<WaitingDevice>(), client.waitingDevices())
            assertEquals("/api/v1/pairing/waiting", server.takeRequest().url.encodedPath)
        }
    }

    @Test
    fun `allowing and declining send the code, and say why the server refused`() = runTest {
        MockWebServer().use { server ->
            server.start()
            server.enqueue(MockResponse.Builder().code(204).build())
            server.enqueue(MockResponse.Builder().code(204).build())
            server.enqueue(
                MockResponse.Builder().code(429).addHeader("Content-Type", "application/problem+json")
                    .body("""{"title":"Too many","detail":"Ten devices connected in the last hour. Try again later."}""").build(),
            )
            val client = FlexarrClient(server.url("/"), apiKey = "token")
            client.allowDevice("ABC-DEF")
            client.declineDevice("ABC-DEF")
            val refused = runCatching { client.allowDevice("ABC-DEF") }.exceptionOrNull()
            assertTrue(refused is FlexarrException.Http && refused.status == 429)
            assertEquals("Ten devices connected in the last hour. Try again later.", refused!!.message)
            val allow = server.takeRequest()
            assertEquals("POST", allow.method)
            assertEquals("/api/v1/pairing/code/ABC-DEF/approve", allow.url.encodedPath)
            assertEquals("{}", allow.body!!.utf8())
            assertEquals("/api/v1/pairing/code/ABC-DEF/decline", server.takeRequest().url.encodedPath)
            // A code that isn't one never reaches the server.
            assertTrue(runCatching { client.allowDevice("../setup") }.exceptionOrNull() is FlexarrException.Http)
            assertEquals(3, server.requestCount)
        }
    }
}
