package io.github.flexarr.core

import kotlinx.coroutines.test.runTest
import mockwebserver3.MockResponse
import mockwebserver3.MockWebServer
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test

class FlexarrClientTest {
    private lateinit var server: MockWebServer

    @Before
    fun start() {
        server = MockWebServer()
        server.start()
    }

    @After
    fun stop() {
        server.close()
    }

    private fun client(profileToken: String? = null) =
        FlexarrClient(server.url("/"), apiKey = "device-token", profileToken = profileToken)

    private fun json(body: String, code: Int = 200) =
        MockResponse.Builder().code(code).addHeader("Content-Type", "application/json").body(body).build()

    @Test
    fun `sends the sign-in and profile with every request`() = runTest {
        server.enqueue(json("[]"))
        client(profileToken = "profile-token").libraries()

        val request = server.takeRequest()
        assertEquals("/api/v1/libraries", request.url.encodedPath)
        assertEquals("device-token", request.headers["X-Api-Key"])
        assertEquals("profile-token", request.headers["X-Flexarr-Profile"])
    }

    @Test
    fun `a newer server's enum values and fields don't break decoding`() = runTest {
        server.enqueue(
            json(
                """[{"id":"1","name":"Radio","type":"Radio","folders":[],"itemCount":3,"somethingNew":true}]""",
            ),
        )
        val libraries = client().libraries()

        assertEquals(LibraryType.Unknown, libraries.single().type)
        assertEquals("Radio", libraries.single().name)
    }

    @Test
    fun `saves a profile's library order`() = runTest {
        server.enqueue(json("""{"id":"kid","name":"Kid","color":"#a9c2ec","libraryOrder":["b","live-tv","a"]}"""))
        val profile = client().setLibraryOrder("kid", listOf("b", "live-tv", "a"))

        val request = server.takeRequest()
        assertEquals("PUT", request.method)
        assertEquals("/api/v1/profiles/kid/library-order", request.url.encodedPath)
        assertEquals("""{"order":["b","live-tv","a"]}""", request.body?.utf8())
        assertEquals(listOf("b", "live-tv", "a"), profile.libraryOrder)
    }

    @Test
    fun `profiles from servers before library order have none`() = runTest {
        server.enqueue(json("""[{"id":"main","name":"Dan","color":"#a9c2ec","isMain":true}]"""))
        assertNull(client().profiles().single().libraryOrder)
    }

    @Test
    fun `a new library order keeps what isn't shown in its place`() {
        // Live TV and a hidden library stay where they were among the shown ones.
        assertEquals(listOf("c", "live-tv", "a", "hidden", "b"), libraryOrder(listOf("a", "live-tv", "b", "hidden", "c"), listOf("c", "a", "b")))
        // Libraries the saved order didn't mention (none saved yet, or added since) go at the end.
        assertEquals(listOf("b", "a"), libraryOrder(emptyList(), listOf("b", "a")))
        assertEquals(listOf("new", "live-tv", "a"), libraryOrder(listOf("a", "live-tv"), listOf("new", "a")))
    }

    @Test
    fun `next up is null when everything is watched`() = runTest {
        server.enqueue(MockResponse.Builder().code(204).build())
        assertNull(client().nextUp("show"))
    }

    @Test
    fun `401 is unauthorized and tells the app`() = runTest {
        var told = false
        server.enqueue(json("{}", code = 401))
        val client = FlexarrClient(server.url("/"), apiKey = "old", onUnauthorized = { told = true })

        val error = runCatching { client.me() }.exceptionOrNull()
        assertTrue(error is FlexarrException.Unauthorized)
        assertTrue(told)
    }

    @Test
    fun `problem details become the error's message`() = runTest {
        server.enqueue(json("""{"title":"Wrong PIN."}""", code = 403))
        val error = runCatching { client().selectProfile("kid", pin = "0000") }.exceptionOrNull()
        assertEquals("Wrong PIN.", error?.message)
    }

    @Test
    fun `an HTML page is not a Flexarr server`() = runTest {
        server.enqueue(MockResponse.Builder().addHeader("Content-Type", "text/html").body("<html></html>").build())
        val error = runCatching { client().systemStatus() }.exceptionOrNull()
        assertTrue(error is FlexarrException.UnexpectedResponse)
    }

    @Test
    fun `addresses default to http and port 8383`() {
        assertEquals("http://mini.local:8383/", FlexarrClient.normalizeAddress("mini.local").toString())
        assertEquals("http://192.168.1.5:9000/", FlexarrClient.normalizeAddress(" 192.168.1.5:9000 ").toString())
        assertEquals("https://flexarr.example.com/", FlexarrClient.normalizeAddress("https://flexarr.example.com/").toString())
        assertEquals("http://host:8383/flexarr/", FlexarrClient.normalizeAddress("http://host:8383/flexarr/?a=b").toString())
        assertNull(FlexarrClient.normalizeAddress("not an address"))
        assertNull(FlexarrClient.normalizeAddress(""))
    }

    @Test
    fun `images get a width hint`() {
        val client = client()
        assertEquals(server.url("/api/v1/items/1/poster?w=300").toString(), client.imageUrl("/api/v1/items/1/poster", 300))
        assertEquals(server.url("/api/v1/items/1/poster?v=2&w=300").toString(), client.imageUrl("/api/v1/items/1/poster?v=2", 300))
        assertNull(client.imageUrl(null, 300))
    }
}
