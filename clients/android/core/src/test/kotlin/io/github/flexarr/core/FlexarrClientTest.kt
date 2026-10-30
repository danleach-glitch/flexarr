package io.github.flexarr.core

import kotlinx.coroutines.runBlocking
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
    fun `entries follow the library order, with ones it doesn't mention after`() {
        val entries = listOf("a", "podcasts", "b", "live-tv", "new")
        assertEquals(listOf("live-tv", "b", "podcasts", "a", "new"), sortedByLibraryOrder(entries, listOf("live-tv", "B", "podcasts", "a")) { it })
        // No order saved: as they came.
        assertEquals(entries, sortedByLibraryOrder(entries, emptyList()) { it })
    }

    @Test
    fun `genres have a backdrop from servers that send one`() = runTest {
        server.enqueue(json("""[{"name":"Drama","count":12,"backdrop":"/api/v1/images/x/backdrop"},{"name":"Comedy","count":3}]"""))
        val genres = client().libraryGenres("films")
        assertEquals("/api/v1/images/x/backdrop", genres[0].backdrop)
        assertNull(genres[1].backdrop)
        assertEquals(3, genres[1].count)
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
    fun `the main profile locked or unlocked, from servers that say and ones that don't`() = runTest {
        server.enqueue(json("""{"profileId":"default","method":"device","zone":"Local","isAdmin":false,"tailscaleLogin":null,"canUnlock":true,"adminPinMissing":false,"unlockedUntil":null}"""))
        server.enqueue(json("""{"profileId":"default","method":"device","zone":"Local","isAdmin":true,"canUnlock":false,"unlockedUntil":"2026-10-30T09:15:00Z"}"""))
        server.enqueue(json("""{"profileId":"kid","method":"device","zone":"Local","isAdmin":false,"canUnlock":false,"adminPinMissing":true}"""))
        // Before 0.56: the main profile was always an admin.
        server.enqueue(json("""{"profileId":"default","method":"device","zone":"Local","isAdmin":true,"tailscaleLogin":null}"""))

        val locked = client().me()
        assertEquals(false, locked.isAdmin)
        assertEquals(true, locked.canUnlock)
        assertEquals(true, locked.isMainProfile)
        val unlocked = client().me()
        assertEquals(true, unlocked.isMainProfile)
        assertEquals("2026-10-30T09:15:00Z", unlocked.unlockedUntil)
        val kid = client().me()
        assertEquals(false, kid.isMainProfile)
        assertEquals(true, kid.adminPinMissing)
        val older = client().me()
        assertEquals(true, older.isMainProfile)
        assertEquals(false, older.canUnlock)
        assertNull(older.unlockedUntil)
    }

    @Test
    fun `unlocking sends the PIN, and says why not`() = runTest {
        server.enqueue(json("""{"unlockedUntil":"2026-10-30T09:15:00Z"}"""))
        server.enqueue(
            MockResponse.Builder().code(429).addHeader("Content-Type", "application/problem+json")
                .body("""{"title":"Too many wrong PINs. Try again in 30 seconds.","status":429,"retryAfterSeconds":30}""").build(),
        )
        server.enqueue(json("""{"detail":"The main profile needs a PIN before the server's settings can be unlocked.","status":409,"pinMissing":true}""", code = 409))

        assertEquals("2026-10-30T09:15:00Z", client().unlockAdmin("1234").unlockedUntil)
        val request = server.takeRequest()
        assertEquals("POST", request.method)
        assertEquals("/api/v1/admin/unlock", request.url.encodedPath)
        assertEquals("""{"pin":"1234"}""", request.body!!.utf8())
        val tooMany = runCatching { client().unlockAdmin("0000") }.exceptionOrNull()
        assertTrue(tooMany is FlexarrException.Http && tooMany.status == 429)
        assertEquals("Too many wrong PINs. Try again in 30 seconds.", tooMany!!.message)
        val missing = runCatching { client().unlockAdmin("0000") }.exceptionOrNull()
        assertTrue(missing is FlexarrException.Http && missing.status == 409)
    }

    @Test
    fun `whether choosing a profile asks for its PIN, falling back to having one on older servers`() = runTest {
        server.enqueue(
            json(
                """[{"id":"default","name":"Dan","color":"#a9c2ec","hasPin":true,"isMain":true,"asksPin":false},""" +
                    """{"id":"kid","name":"Kid","color":"#a9c2ec","hasPin":true,"asksPin":true},""" +
                    """{"id":"old","name":"Old","color":"#a9c2ec","hasPin":true}]""",
            ),
        )
        assertEquals(listOf(false, true, true), client().profiles().map { it.takesPin })
    }

    @Test
    fun `problem details become the error's message`() = runTest {
        server.enqueue(json("""{"title":"Wrong PIN."}""", code = 403))
        val error = runCatching { client().selectProfile("kid", pin = "0000") }.exceptionOrNull()
        assertEquals("Wrong PIN.", error?.message)
    }

    @Test
    fun `leaving a kids profile asks for the main profile's PIN`() = runTest {
        server.enqueue(json("""{"title":"Enter the main profile’s PIN.","parentPin":true}""", code = 403))
        val error = runCatching { client().selectProfile("mum", parentPin = "2580") }.exceptionOrNull()
        assertTrue(error is FlexarrException.ParentPinNeeded)
        assertEquals("Enter the main profile’s PIN.", error?.message)
        assertEquals("""{"parentPin":"2580"}""", server.takeRequest().body?.utf8())

        server.enqueue(json("""{"title":"This device is locked to kids profiles.","kidsLock":true}""", code = 403))
        assertTrue(runCatching { client().selectProfile("mum") }.exceptionOrNull() is FlexarrException.KidsLocked)
    }

    @Test
    fun `the right parent PIN with the wrong own PIN says which was wrong`() = runTest {
        server.enqueue(json("""{"title":"That PIN isn't right.","ownPin":true}""", code = 403))
        val error = runCatching { client().selectProfile("dad", pin = "1111", parentPin = "2580") }.exceptionOrNull()
        assertTrue(error is FlexarrException.OwnPinWrong)
        assertEquals("That PIN isn't right.", error?.message)
        assertEquals("""{"pin":"1111","parentPin":"2580"}""", server.takeRequest().body?.utf8())
    }

    @Test
    fun `kids profiles and the lock are read, and older servers have neither`() = runTest {
        server.enqueue(json("""{"token":"ada.sig","profile":{"id":"ada","name":"Ada","color":"#e8a0bf","maxRatingAge":8,"isKids":true,"isCurrent":true}}"""))
        val selection = client().lockToKids(pin = "2580")
        assertTrue(selection.profile.isKids && selection.profile.hasAgeLimit)
        assertEquals("/api/v1/profiles/kids-lock", server.takeRequest().url.encodedPath)

        server.enqueue(json("""{"profileId":"ada","method":"device","isAdmin":false,"kidsLock":true}"""))
        assertTrue(client().me().kidsLock)
        assertTrue(!Profile(id = "p", name = "P", color = "#fff").hasAgeLimit)
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

    @Test
    fun `an internet host typed without a scheme is tried over https first`() {
        assertEquals(
            listOf("https://flexarr.example.com/", "http://flexarr.example.com:8383/"),
            FlexarrClient.addressCandidates("flexarr.example.com").map { it.toString() },
        )
        assertEquals(listOf("https://example.com:9000/", "http://example.com:9000/"), FlexarrClient.addressCandidates("example.com:9000").map { it.toString() })
        // Typed with a scheme, it's what was asked for.
        assertEquals(listOf("http://example.com/"), FlexarrClient.addressCandidates("http://example.com").map { it.toString() })
        assertEquals(emptyList<String>(), FlexarrClient.addressCandidates("not an address").map { it.toString() })
    }

    @Test
    fun `home network hosts stay on http and port 8383`() {
        for (host in listOf("mini.local", "mini", "localhost", "192.168.1.5", "10.0.0.2", "172.20.1.1", "127.0.0.1", "100.101.2.3", "mini.tail1234.ts.net", "nas.lan", "[fd12::1]")) {
            assertEquals(host, 1, FlexarrClient.addressCandidates(host).size)
            assertEquals(host, "http", FlexarrClient.addressCandidates(host).single().scheme)
            assertEquals(host, 8383, FlexarrClient.addressCandidates(host).single().port)
        }
        assertEquals(2, FlexarrClient.addressCandidates("8.8.8.8").size)
        assertEquals(2, FlexarrClient.addressCandidates("100.200.1.1").size) // outside Tailscale's range
    }

    @Test
    fun `only a Tailscale address is taken as the server's remote address`() {
        fun acceptable(url: String) = FlexarrClient.isAcceptableRemote(FlexarrClient.normalizeAddress(url)!!)
        assertTrue(acceptable("https://mini.tail1234.ts.net:8443"))
        assertTrue(acceptable("http://100.101.2.3:8383"))
        assertTrue(!acceptable("http://mini.tail1234.ts.net:8443"))
        assertTrue(!acceptable("https://flexarr.example.com"))
        assertTrue(!acceptable("https://ts.net.example.com"))
        assertTrue(!acceptable("http://192.168.1.5:8383"))
    }

    @Test
    fun `asks another address which server is there, without the sign-in`() = runTest {
        server.enqueue(json("""{"open":false,"codeRequired":true,"serverId":"abc123"}"""))
        server.enqueue(json("""{"open":false}"""))
        server.enqueue(MockResponse.Builder().addHeader("Content-Type", "text/html").body("<html></html>").build())
        val url = server.url("/")

        assertEquals("abc123", FlexarrClient.serverIdAt(url))
        val request = server.takeRequest()
        assertEquals("/api/v1/setup", request.url.encodedPath)
        assertNull(request.headers["X-Api-Key"])
        assertNull(request.headers["X-Flexarr-Profile"])
        // A server before 0.55, and something that isn't a Flexarr server: no id.
        assertNull(FlexarrClient.serverIdAt(url))
        assertNull(FlexarrClient.serverIdAt(url))
        server.close()
        assertTrue(runCatching { FlexarrClient.serverIdAt(url) }.exceptionOrNull() is FlexarrException.Unreachable)
    }

    @Test
    fun `ids from links are the server's kind`() {
        assertTrue(FlexarrClient.isPlainId("01a16be6-5a0b-790c-8734-28f147909cd9"))
        assertTrue(FlexarrClient.isPlainId("r1"))
        for (id in listOf("", "..", "a/b", "a%2Fb", "x?y", "a b", "%2e%2e")) assertTrue(id, !FlexarrClient.isPlainId(id))
    }

    @Test
    fun `a dot segment never reaches the server`() = runTest {
        for (id in listOf("..", "%2e%2E", ".")) {
            val error = runCatching { client().item(id) }.exceptionOrNull()
            assertTrue("$id: $error", error is FlexarrException.Http && error.status == 404)
        }
        assertEquals(0, server.requestCount)
    }

    @Test
    fun `an answer that doesn't read is an unexpected response, a bad time included`() = runTest {
        server.enqueue(json("""{"id":"1"}"""))
        server.enqueue(json("""[{"id":"d","name":"TV","online":true,"lastUsedAt":"yesterday","profileId":"p"}]"""))
        val client = client()
        assertTrue(runCatching { client.libraries() }.exceptionOrNull() is FlexarrException.UnexpectedResponse)
        assertTrue(runCatching { client.devices() }.exceptionOrNull() is FlexarrException.UnexpectedResponse)
    }

    // Real time, not the test's: the answer comes from another thread, inside a time limit.
    @Test
    fun `a stream another screen took says why`() = runBlocking {
        server.enqueue(json("""{"title":"Playing somewhere else","status":410,"detail":"Dan started watching on the TV."}""", code = 410))
        assertEquals("Dan started watching on the TV.", client().takenOverReason("s1"))
        assertEquals("/api/v1/playback/s1/main.m3u8", server.takeRequest().url.encodedPath)

        server.enqueue(json("""{"title":"Not Found","status":404}""", code = 404))
        assertNull(client().takenOverReason("s2"))
        server.enqueue(MockResponse.Builder().addHeader("Content-Type", "application/vnd.apple.mpegurl").body("#EXTM3U\n").build())
        assertNull(client().takenOverReason("s3"))
    }
}
