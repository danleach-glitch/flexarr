package io.github.flexarr.core

import kotlinx.coroutines.test.runTest
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import mockwebserver3.MockResponse
import mockwebserver3.MockWebServer
import okhttp3.OkHttpClient
import okhttp3.Request
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.IOException
import java.time.Instant
import java.util.Base64

/** Family invites: reading an invite link, the tunnel in front of loopback requests, and a TV opening its sealed invite. */
class GuestInviteTest {
    private fun payload(vararg changes: Pair<String, Any?>): String {
        val fields = linkedMapOf<String, Any?>(
            "v" to 1, "n" to "Dan", "s" to "server-1", "a" to "100.101.102.103:8383", "k" to "tskey-auth-abc",
            "t" to "token-123", "g" to "Gran’s phone", "p" to "Gran", "e" to "2099-01-01T00:00:00Z",
        )
        changes.forEach { (key, value) -> fields[key] = value }
        val json = JsonObject(fields.mapValues { (_, value) -> if (value is Number) JsonPrimitive(value) else JsonPrimitive(value as String?) }).toString()
        return Base64.getUrlEncoder().withoutPadding().encodeToString(json.toByteArray())
    }

    @Test
    fun `reads a link, the app's link or the part after the hash`() {
        val encoded = payload()
        for (text in listOf("https://flexarr.io/join#$encoded", "flexarr://join#$encoded", "flexarr.io/join#$encoded", "  $encoded\n")) {
            val invite = GuestInvite.parse(text)
            assertNotNull(text, invite)
            assertEquals("Dan", invite!!.ownerName)
            assertEquals("Gran", invite.profileName)
            assertEquals("100.101.102.103:8383", invite.tailnetTarget)
            assertEquals("token-123", invite.token)
            assertEquals("tskey-auth-abc", invite.authKey)
            assertFalse(invite.isExpired())
        }
    }

    @Test
    fun `refuses anything that could point elsewhere`() {
        assertNull(GuestInvite.parse("https://evil.example/join#${payload()}"))
        assertNull(GuestInvite.parse("https://evilflexarr.io/join#${payload()}"))
        assertNull(GuestInvite.parse(payload("a" to "192.168.1.106:8383"))) // not a tailnet address
        assertNull(GuestInvite.parse(payload("a" to "100.101.102.103"))) // no port
        assertNull(GuestInvite.parse(payload("a" to "100.128.0.1:8383"))) // past 100.64.0.0/10
        assertNull(GuestInvite.parse(payload("a" to "mini.tail.ts.net:8383"))) // a name, which could be looked up anywhere
        assertNull(GuestInvite.parse(payload("k" to "not-a-key")))
        assertNull(GuestInvite.parse(payload("v" to 2)))
        assertNull(GuestInvite.parse(payload("t" to "")))
        assertNull(GuestInvite.parse(payload("p" to null)))
        assertNull(GuestInvite.parse("not an invite"))
        assertNull(GuestInvite.parse(""))
    }

    @Test
    fun `knows when it has run out`() {
        val invite = GuestInvite.parse(payload("e" to "2026-11-03T12:00:00.1234567Z"))!!
        assertFalse(invite.isExpired(Instant.ofEpochSecond(1_793_000_000)))
        assertTrue(invite.isExpired(Instant.ofEpochSecond(1_800_000_000)))
        // An invite without an expiry is left to the server to judge.
        assertFalse(GuestInvite.parse(payload("e" to null))!!.isExpired())
    }

    @Test
    fun `names the device on the tailnet as the Apple apps do`() {
        assertEquals("flexarr-guest-grans-ipad", GuestInvite.hostname("Gran’s iPad"))
        assertEquals("flexarr-guest-cafe-tv", GuestInvite.hostname("Café TV"))
        assertEquals("flexarr-guest-living-room-tv", GuestInvite.hostname("Living  room_TV"))
        assertEquals("flexarr-guest", GuestInvite.hostname("😀"))
        assertTrue(GuestInvite.hostname("a".repeat(80)).length <= 40)
        assertTrue(GuestInvite.hostname("Ünïcode Phone 2.0").matches(Regex("^[a-z0-9-]+$")))
    }

    @Test
    fun `says why joining didn't work, and whether trying again could`() {
        val used = GuestJoinProblem("That invite has run out or been used.", canRetry = false)
        assertEquals(used, guestJoinProblem(FlexarrException.Http(410, null), "Dan"))
        assertEquals(used, guestJoinProblem(IOException(), "Dan", keyRefused = true))
        assertEquals(GuestJoinProblem("Dan’s Flexarr isn’t taking guests just now.", canRetry = false), guestJoinProblem(FlexarrException.Http(503, null), "Dan"))
        assertEquals(
            GuestJoinProblem("Can’t reach Dan’s Flexarr. Check this device’s internet.", canRetry = true),
            guestJoinProblem(FlexarrException.Unreachable(IOException()), "Dan"),
        )
    }

    @Test
    fun `redeeming sends the token without a sign-in`() = runTest {
        MockWebServer().use { server ->
            server.start()
            server.enqueue(MockResponse.Builder().addHeader("Content-Type", "application/json").body("""{"apiKey":"guest-key","profileId":"p1","serverId":"s1","name":"Gran’s phone"}""").build())
            val redeemed = FlexarrClient(server.url("/")).redeemInvite("token-123")
            assertEquals("guest-key", redeemed.apiKey)
            val request = server.takeRequest()
            assertEquals("/api/v1/invites/redeem", request.url.encodedPath)
            assertNull(request.headers["X-Api-Key"])
            assertEquals("token-123", FlexarrClient.json.parseToJsonElement(request.body!!.utf8()).jsonObject["token"]!!.jsonPrimitive.content)
        }
    }

    @Test
    fun `loopback requests wait for the tunnel, go to its port and start it again once when it's gone`() {
        MockWebServer().use { server ->
            server.start()
            server.enqueue(MockResponse.Builder().body("ok").build())
            server.enqueue(MockResponse.Builder().body("again").build())
            var restarts = 0
            val tunnel = object : LoopbackTunnel {
                var listening = server.port
                override fun port() = listening
                override fun restart(failed: Int): Int {
                    restarts++
                    listening = server.port
                    return listening
                }
            }
            val http = OkHttpClient.Builder().addInterceptor(LoopbackTunnel.interceptor { tunnel }).build()
            // Saved with another port: sent to the tunnel's.
            val saved = server.url("/x").newBuilder().host("127.0.0.1").port(1).build()
            assertEquals("ok", http.newCall(Request.Builder().url(saved).build()).execute().body.string())
            // Nothing listening where the tunnel was: started again, and the request goes there.
            tunnel.listening = 1
            assertEquals("again", http.newCall(Request.Builder().url(saved).build()).execute().body.string())
            assertEquals(1, restarts)
        }
    }

    @Test
    fun `a TV opens the shared vector as the server seals it`() {
        val vector = FlexarrClient.json.parseToJsonElement(javaClass.classLoader!!.getResource("tv-invite.json")!!.readText()).jsonObject
        fun bytes(name: String) = Base64.getDecoder().decode(vector[name]!!.jsonPrimitive.content)
        val tv = GuestTvCode.fromRawKeys(bytes("tvPrivateKey"), bytes("tvPublicKey"), code = "ABCDEFGH")
        assertEquals(vector["tvPublicKey"]!!.jsonPrimitive.content, tv.publicKey)

        val opened = tv.open(bytes("serverKey"), bytes("sealed"))

        assertEquals(vector["plaintext"]!!.jsonPrimitive.content, String(opened))
    }

    @Test
    fun `a TV code is eight letters that can't be misread`() {
        val tv = GuestTvCode.create()
        assertEquals(8, tv.code.length)
        assertTrue(tv.code.all { it in GuestTvCode.ALPHABET })
        assertEquals("${tv.code.take(4)}-${tv.code.takeLast(4)}", tv.display)
        assertEquals(tv.code.take(4).toList().joinToString(" ") + ", " + tv.code.takeLast(4).toList().joinToString(" "), tv.spoken)
        assertTrue(tv.secret.matches(Regex("^[A-Za-z0-9_-]{32,128}$")))
        assertEquals(65, Base64.getDecoder().decode(tv.publicKey).size)
    }

    @Test
    fun `a TV registers, waits, and collects its invite from the relay once`() = runTest {
        MockWebServer().use { relay ->
            relay.start()
            relay.enqueue(MockResponse.Builder().code(409).build())
            relay.enqueue(MockResponse.Builder().code(201).body("""{"expiresIn":600}""").build())
            relay.enqueue(MockResponse.Builder().code(204).build())
            relay.enqueue(MockResponse.Builder().code(404).build())
            val tv = GuestTvCode.create()
            val url = relay.url("/api/pair")
            assertTrue(runCatching { tv.register(relay = url) }.exceptionOrNull() is GuestTvCode.Failure.CodeTaken)
            tv.register(relay = url)
            assertEquals(GuestTvCode.Answer.Waiting, tv.poll(relay = url))
            assertEquals(GuestTvCode.Answer.Expired, tv.poll(relay = url))

            relay.takeRequest()
            val registered = relay.takeRequest()
            assertEquals("POST", registered.method)
            assertTrue(registered.headers["Content-Type"]!!.startsWith("application/json"))
            val body = FlexarrClient.json.parseToJsonElement(registered.body!!.utf8()).jsonObject
            assertEquals(tv.code, body["code"]!!.jsonPrimitive.content)
            assertEquals(tv.publicKey, body["publicKey"]!!.jsonPrimitive.content)
            assertEquals(tv.secret, body["secret"]!!.jsonPrimitive.content)
            val polled = relay.takeRequest()
            assertEquals(tv.code, polled.url.queryParameter("code"))
            assertEquals(tv.secret, polled.url.queryParameter("secret"))
        }
    }
}
