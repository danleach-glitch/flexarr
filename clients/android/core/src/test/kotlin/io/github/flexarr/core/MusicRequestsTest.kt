package io.github.flexarr.core

import kotlinx.coroutines.test.runTest
import kotlinx.serialization.serializer
import mockwebserver3.MockResponse
import mockwebserver3.MockWebServer
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Before
import org.junit.Test

/** The music request calls, what they send, and how the server's answers read. */
class MusicRequestsTest {
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

    private fun client() = FlexarrClient(server.url("/"))

    private fun json(body: String) = MockResponse.Builder().addHeader("Content-Type", "application/json").body(body).build()

    private inline fun <reified T> decode(text: String): T = FlexarrClient.json.decodeFromString(serializer<T>(), text)

    @Test
    fun `a server before music requests has them off`() = runTest {
        server.enqueue(json("""{"configured":true,"reachable":true}"""))
        server.enqueue(MockResponse.Builder().code(404).build())
        assertEquals(MusicRequestStatus(configured = true, reachable = true), client().musicRequestStatus())
        assertEquals("/api/v1/requests/music/status", server.takeRequest().target)
        assertFalse(client().musicRequestStatus().configured)
    }

    @Test
    fun `a server that answers the status with its web page or anything else has them off`() = runTest {
        server.enqueue(MockResponse.Builder().addHeader("Content-Type", "text/html").body("<!doctype html><html></html>").build())
        server.enqueue(json("""{"something":"else"}"""))
        server.enqueue(json("not json"))
        repeat(3) { assertEquals(MusicRequestStatus(configured = false, reachable = false), client().musicRequestStatus()) }
    }

    @Test
    fun `discover's rows have a shape and genres`() {
        val discover = decode<MusicDiscover>(
            """{"rows":[{"id":"like","title":"Like artists you play","shape":"artists","items":[
                {"kind":"artist","deezerId":27,"title":"Daft Punk","artist":null,"artistDeezerId":null,"year":null,"image":"/api/v1/images/remote?url=x","availability":"Available","libraryItemId":"8f0c"}]},
               {"id":"charts","title":"Top charts","shape":"albums","items":[
                {"kind":"album","deezerId":302127,"title":"Discovery","artist":"Daft Punk","artistDeezerId":27,"year":2001,"image":null,"availability":"None","libraryItemId":null,"recordType":"album"}]}],
               "genres":[{"id":132,"name":"Pop","image":"/api/v1/images/remote?url=y"}]}""",
        )
        assertTrue(discover.rows.first().isArtists)
        assertFalse(discover.rows.last().isArtists)
        val artist = discover.rows.first().items.single()
        assertTrue(artist.isArtist)
        assertEquals("8f0c", artist.libraryItemId)
        assertFalse(artist.canRequest)
        val album = discover.rows.last().items.single()
        assertEquals("album-302127", album.id)
        assertTrue(album.canRequest)
        assertEquals("Album, 2001", album.kindLine)
        assertEquals("Pop", discover.genres.single().name)
    }

    @Test
    fun `an album only Lidarr knows goes by its release group`() {
        val album = decode<MusicExternal>("""{"kind":"album","deezerId":0,"title":"Live","availability":"None","recordType":"ep","musicBrainzId":"rg-1"}""")
        assertEquals("album-rg-1", album.id)
        assertTrue(album.canRequest)
        assertEquals("EP", album.kindLine)
        // Neither id: nothing to request it by.
        assertFalse(decode<MusicExternal>("""{"kind":"album","deezerId":0,"title":"Live","availability":"None"}""").canRequest)
    }

    @Test
    fun `a newer server's availability reads as unknown and isn't requestable`() {
        val album = decode<MusicExternal>("""{"kind":"album","deezerId":1,"title":"New","availability":"Blocked"}""")
        assertEquals(Availability.Unknown, album.availability)
        assertFalse(album.canRequest)
    }

    @Test
    fun `an artist's releases go on albums, EPs and singles, and compilations shelves`() {
        fun album(id: Long, type: String?) = MusicExternal("album", id, "A$id", recordType = type)
        val shelves = listOf(album(1, "album"), album(2, "single"), album(3, null), album(4, "ep"), album(5, "compile")).releaseShelves()
        assertEquals(listOf("Albums", "EPs and singles", "Compilations"), shelves.map { it.first })
        assertEquals(listOf(1L, 3L), shelves[0].second.map { it.deezerId })
        assertEquals(listOf(2L, 4L), shelves[1].second.map { it.deezerId })
        // Only the shelves with something on them.
        assertEquals(listOf("EPs and singles"), listOf(album(6, "ep")).releaseShelves().map { it.first })
    }

    @Test
    fun `an album's page has its tracks`() {
        val album = decode<MusicAlbumDetail>(
            """{"deezerId":302127,"title":"Discovery","artist":"Daft Punk","artistDeezerId":27,"image":null,"year":2001,"recordType":"album",
               "genres":["Electro"],"tracks":[{"title":"One More Time","durationSeconds":320,"number":1,"disc":1,"explicit":false}],
               "durationSeconds":3660,"availability":"Requested","libraryItemId":null}""",
        )
        assertEquals("One More Time", album.tracks.single().title)
        assertEquals(Availability.Requested, album.availability)
        assertEquals("album-302127", album.summary.id)
        assertFalse(album.summary.canRequest)
    }

    @Test
    fun `fetches the outside pages and a library artist's extras`() = runTest {
        val client = client()
        server.enqueue(json("""{"artists":[{"kind":"artist","deezerId":27,"title":"Daft Punk","availability":"None"}],"albums":[]}"""))
        server.enqueue(json("""{"deezerId":27,"name":"Daft Punk","image":null,"fans":4000000,"albums":[],"similar":[],"followed":true,"availability":"None","libraryItemId":null}"""))
        server.enqueue(json("""{"deezerId":1,"title":"A","artist":"B","genres":[],"tracks":[],"durationSeconds":0,"availability":"None"}"""))
        server.enqueue(json("""{"id":132,"name":"Pop","artists":[]}"""))
        server.enqueue(json("""{"missing":[],"similar":[],"followed":false,"deezerId":null}"""))

        assertEquals("Daft Punk", client.searchMusic("daft punk").artists.single().title)
        assertEquals("/api/v1/requests/music/search?q=daft%20punk", server.takeRequest().target)
        assertTrue(client.musicArtist(27).followed)
        assertEquals("/api/v1/requests/music/artist/27", server.takeRequest().target)
        client.musicAlbum(1)
        assertEquals("/api/v1/requests/music/album/1", server.takeRequest().target)
        assertEquals("Pop", client.musicGenre(132).name)
        assertEquals("/api/v1/requests/music/genres/132", server.takeRequest().target)
        assertNull(client.musicArtistExtras("a1").deezerId)
        assertEquals("/api/v1/requests/music/library-artist/a1", server.takeRequest().target)
    }

    @Test
    fun `requests an album, by Deezer id or release group, and follows an artist`() = runTest {
        val client = client()
        repeat(4) { server.enqueue(MockResponse.Builder().code(202).build()) }

        client.requestAlbum(MusicExternal("album", 302127, "Discovery", availability = Availability.None))
        client.requestAlbum(MusicExternal("album", 0, "Live", availability = Availability.None, musicBrainzId = "rg-1"))
        client.followArtist(27)
        client.followLibraryArtist("a1")
        val sent = List(4) { server.takeRequest() }
        assertTrue(sent.all { it.method == "POST" && it.target == "/api/v1/requests/music" })
        assertEquals(
            listOf(
                """{"kind":"album","deezerId":302127}""",
                """{"kind":"album","musicBrainzId":"rg-1"}""",
                """{"kind":"artist","deezerId":27}""",
                """{"kind":"artist","artistId":"a1"}""",
            ),
            sent.map { it.body?.utf8() },
        )
    }

    @Test
    fun `unfollows an artist by library id, Deezer id or MusicBrainz id`() = runTest {
        val client = client()
        repeat(3) { server.enqueue(MockResponse.Builder().code(204).build()) }

        client.unfollowArtist(artistId = "a1", deezerId = 27)
        client.unfollowArtist(deezerId = 27)
        client.unfollowArtist(musicBrainzId = "mb-1")
        val sent = List(3) { server.takeRequest() }
        assertTrue(sent.all { it.method == "POST" && it.target == "/api/v1/requests/music/unfollow" })
        assertEquals(listOf("""{"artistId":"a1"}""", """{"deezerId":27}""", """{"musicBrainzId":"mb-1"}"""), sent.map { it.body?.utf8() })
    }

    @Test
    fun `cancels an album's request by release group when known, else Deezer id`() = runTest {
        val client = client()
        repeat(2) { server.enqueue(MockResponse.Builder().code(204).build()) }

        client.unrequestAlbum(MusicExternal("album", 302127, "Discovery", availability = Availability.Requested, musicBrainzId = "rg-1"))
        client.unrequestAlbum(MusicExternal("album", 302127, "Discovery", availability = Availability.Requested))
        val sent = List(2) { server.takeRequest() }
        assertTrue(sent.all { it.method == "POST" && it.target == "/api/v1/requests/music/unrequest" })
        assertEquals(listOf("""{"musicBrainzId":"rg-1"}""", """{"deezerId":302127}"""), sent.map { it.body?.utf8() })
    }

    @Test
    fun `an undo Lidarr refuses says why`() = runTest {
        server.enqueue(
            MockResponse.Builder().code(502).addHeader("Content-Type", "application/problem+json")
                .body("""{"title":"Bad Gateway","status":502,"detail":"Lidarr isn't answering."}""").build(),
        )
        try {
            client().unrequestAlbum(MusicExternal("album", 1, "A", availability = Availability.Requested))
            fail("expected a refusal")
        } catch (e: FlexarrException.Http) {
            assertEquals(502, e.status)
            assertTrue(e.message!!.contains("Lidarr isn't answering."))
        }
    }

    @Test
    fun `an undo on an older server says to update it`() = runTest {
        server.enqueue(MockResponse.Builder().code(404).build())
        server.enqueue(MockResponse.Builder().code(404).build())
        val client = client()
        for (undo in listOf<suspend () -> Unit>({ client.unfollowArtist(deezerId = 27) }, { client.unrequestAlbum(MusicExternal("album", 1, "A", availability = Availability.Requested)) })) {
            try {
                undo()
                fail("expected a 404")
            } catch (e: FlexarrException.Http) {
                assertEquals(404, e.status)
                assertEquals(UndoNeedsNewerServer, e.message)
            }
        }
    }

    @Test
    fun `a guest's undo says the server's words`() = runTest {
        server.enqueue(
            MockResponse.Builder().code(403).addHeader("Content-Type", "application/problem+json")
                .body("""{"title":"Forbidden","status":403,"detail":"Ask your host to stop following them."}""").build(),
        )
        try {
            client().unfollowArtist(artistId = "a1")
            fail("expected a refusal")
        } catch (e: FlexarrException.Http) {
            assertEquals(403, e.status)
            assertTrue(e.message!!.contains("Ask your host"))
        }
    }

    @Test
    fun `a refused request says why`() = runTest {
        server.enqueue(
            MockResponse.Builder().code(409).addHeader("Content-Type", "application/problem+json")
                .body("""{"title":"Conflict","status":409,"detail":"Lidarr doesn't know this artist."}""").build(),
        )
        try {
            client().followArtist(27)
            fail("expected a refusal")
        } catch (e: FlexarrException.Http) {
            assertEquals(409, e.status)
            assertTrue(e.message!!.contains("Lidarr doesn't know this artist."))
        }
    }
}
