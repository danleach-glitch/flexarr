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
