package io.github.flexarr.core

import kotlinx.coroutines.flow.toList
import kotlinx.coroutines.test.runTest
import mockwebserver3.MockResponse
import mockwebserver3.MockWebServer
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class DevicesTest {
    @Test
    fun `server-sent events are read line by line, comments skipped`() {
        val events = ServerSentEvents()
        assertNull(events.feed(": keep-alive"))
        assertNull(events.feed("event: command"))
        assertNull(events.feed("data: {\"a\":1,"))
        assertNull(events.feed("data: \"b\":2}"))
        assertEquals(ServerSentEvents.Event("command", "{\"a\":1,\n\"b\":2}"), events.feed(""))
        assertNull(events.feed(""))
    }

    @Test
    fun `commands arrive from the stream, newer kinds as unknown`() = runTest {
        MockWebServer().use { server ->
            server.start()
            server.enqueue(
                MockResponse.Builder().addHeader("Content-Type", "text/event-stream").body(
                    ": hello\n\nevent: command\ndata: {\"id\":\"1\",\"kind\":\"Play\",\"itemId\":\"film\",\"fromBeginning\":true,\"sentAt\":\"2026-10-29T08:00:00Z\"}\n\n" +
                        "event: command\ndata: {\"id\":\"2\",\"kind\":\"Rewind\",\"sentAt\":\"2026-10-29T08:00:00Z\"}\n\n",
                ).build(),
            )
            val commands = FlexarrClient(server.url("/"), apiKey = "token").commandStream().toList()
            assertEquals(listOf(DeviceCommandKind.Play, DeviceCommandKind.Unknown), commands.map { it.kind })
            assertEquals("film", commands[0].itemId)
            assertNull(commands[0].profileId)
            assertEquals("/api/v1/devices/commands", server.takeRequest().url.encodedPath)
        }
    }

    @Test
    fun `a command can say which profile to play as`() {
        val command = FlexarrClient.json.decodeFromString(
            DeviceCommand.serializer(),
            """{"id":"1","kind":"Play","itemId":"film","profileId":"kid","sentAt":"2026-10-29T08:00:00Z"}""",
        )
        assertEquals("kid", command.profileId)
    }

    @Test
    fun `playing on another device sends the profile only when there is one`() = runTest {
        MockWebServer().use { server ->
            server.start()
            val result = """{"delivered":true,"deviceName":"TV"}"""
            server.enqueue(MockResponse.Builder().addHeader("Content-Type", "application/json").body(result).build())
            server.enqueue(MockResponse.Builder().addHeader("Content-Type", "application/json").body(result).build())
            val client = FlexarrClient(server.url("/"), apiKey = "token")
            client.playOn("tv", "film")
            client.playOn("tv", "film", profileId = "kid")
            assertEquals("""{"itemId":"film","fromBeginning":false,"shuffle":false}""", server.takeRequest().body!!.utf8())
            assertEquals("""{"itemId":"film","fromBeginning":false,"shuffle":false,"profileId":"kid"}""", server.takeRequest().body!!.utf8())
        }
    }

    @Test
    fun `the command stream tells the app when its sign-in stops working`() = runTest {
        MockWebServer().use { server ->
            server.start()
            server.enqueue(MockResponse.Builder().code(401).build())
            var told = false
            val client = FlexarrClient(server.url("/"), apiKey = "token", onUnauthorized = { told = true })
            val error = runCatching { client.commandStream().toList() }.exceptionOrNull()
            assertTrue(error is FlexarrException.Unauthorized)
            assertTrue(told)
        }
    }
}
