package io.github.flexarr.core

import kotlinx.coroutines.test.runTest
import kotlinx.serialization.serializer
import mockwebserver3.MockResponse
import mockwebserver3.MockWebServer
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** FlexarrKit's MusicDecodingTests, against the same fixtures. */
class MusicTest {
    private inline fun <reified T> fixture(name: String): T = FlexarrClient.json.decodeFromString(serializer<T>(), text(name))

    private fun text(name: String) = javaClass.classLoader!!.getResource("$name.json")!!.readText()

    @Test
    fun `an artist lists their albums newest first`() {
        val detail = fixture<ItemDetail>("item-artist")
        assertEquals(MediaKind.Artist, detail.item.kind)
        assertTrue(detail.item.kind.isMusic)
        assertEquals(listOf("4 albums", "9.4"), detail.item.facts())
        assertTrue(detail.children.all { it.kind == MediaKind.Album })
        assertEquals(listOf(1991, 1982, 1980, 1979), detail.children.map { it.year })
        assertEquals("Dire Straits", detail.children.first().artistName)
    }

    @Test
    fun `an album has numbered tracks, on one disc`() {
        val detail = fixture<ItemDetail>("item-album")
        assertEquals(listOf("1991", "12 tracks", "7.5"), detail.item.facts())
        // Kids profiles see no scores.
        assertEquals(listOf("1991", "12 tracks"), detail.item.facts(scores = false))
        assertEquals(listOf(MediaKind.Artist), detail.ancestors.map { it.kind })
        assertEquals(listOf(1, 2, 3, 4, 5), detail.children.map { it.trackNumber })
        assertNull(detail.children.first().discNumber)
        assertEquals(387.4, detail.children.first().durationSeconds!!, 0.001)
        assertEquals("25m", totalRuntime(detail.children))
        val discs = discs(detail.children)
        assertEquals(1, discs.size)
        assertEquals(1, discs.first().first)
        assertEquals(5, discs.first().second.size)
    }

    @Test
    fun `a track's own artist is named before the album's`() {
        val tracks = fixture<List<ItemSummary>>("tracks")
        assertTrue(tracks.take(2).all { it.artistName == "Dire Straits" && it.trackArtist == null })
        assertEquals("Mark Knopfler", tracks[2].artistName)
        assertEquals(listOf(1, 2, 1), tracks.map { it.trackNumber })
    }

    @Test
    fun `synced lyrics light the line being sung`() {
        val lyrics = fixture<Lyrics>("lyrics")
        assertTrue(lyrics.synced)
        assertNull(lyrics.activeLine(5.0))
        assertEquals(0, lyrics.activeLine(12.0))
        assertEquals(1, lyrics.activeLine(17.0))
        assertEquals(3, lyrics.activeLine(500.0))
        assertNull(lyrics.copy(synced = false).activeLine(17.0))
    }

    @Test
    fun `a playlist carries its tracks in order`() {
        // A playlist as the server sends it, wrapped around the captured track summaries.
        val entries = fixture<List<ItemSummary>>("tracks")
        val captured = FlexarrClient.json.parseToJsonElement(text("tracks")) as kotlinx.serialization.json.JsonArray
        val wrapped = captured.mapIndexed { index, track -> """{"entryId":${index + 5},"track":$track}""" }.joinToString(",")
        val detail = FlexarrClient.json.decodeFromString(
            serializer<PlaylistDetail>(),
            """{"summary":{"id":"7c1d","name":"Road trip","trackCount":3,"durationSeconds":912.5,
            "covers":["/api/v1/images/items/a/poster","/api/v1/images/items/b/poster"],"cover":null,
            "createdAt":"2026-10-25T10:00:00","updatedAt":"2026-10-25T11:30:00.123456"},"tracks":[$wrapped]}""",
        )
        assertEquals("3 tracks", detail.summary.trackCountLabel)
        assertEquals(listOf(5, 6, 7), detail.tracks.map { it.entryId })
        assertEquals(entries, detail.tracks.map { it.track })
    }

    @Test
    fun `a Home row of playlists has its playlists and no items`() {
        val row = FlexarrClient.json.decodeFromString(
            serializer<HomeRow>(),
            """{"id":"playlists","title":"Your playlists","style":"playlists","items":[],"link":null,
            "playlists":[{"id":"p","name":"Mix","trackCount":1,"durationSeconds":200,"covers":[],"cover":null,
            "createdAt":"2026-10-25T10:00:00","updatedAt":"2026-10-25T10:00:00"}]}""",
        )
        assertEquals(HomeRowStyle.Playlists, row.style)
        assertEquals("1 track", row.playlists!!.single().trackCountLabel)
    }

    @Test
    fun `a server before playlists and lyrics has none`() = runTest {
        MockWebServer().use { server ->
            server.start()
            val notFound = MockResponse.Builder().code(404).build()
            server.enqueue(notFound)
            server.enqueue(notFound)
            val client = FlexarrClient(server.url("/"))
            assertEquals(emptyList<PlaylistSummary>(), client.playlists())
            assertNull(client.lyrics("track"))
        }
    }

    private fun json(body: String, code: Int = 200) =
        MockResponse.Builder().code(code).addHeader("Content-Type", "application/json").body(body).build()

    @Test
    fun `music rows that fail to load fall back to the profile's playlists`() = runTest {
        MockWebServer().use { server ->
            server.start()
            server.enqueue(MockResponse.Builder().code(500).build())
            server.enqueue(json("""[{"id":"p","name":"Road trip","trackCount":2,"durationSeconds":400}]"""))
            val rows = FlexarrClient(server.url("/")).musicRows("lib")
            assertEquals("/api/v1/libraries/lib/browse", server.takeRequest().url.encodedPath)
            assertEquals("/api/v1/playlists", server.takeRequest().url.encodedPath)
            val row = rows.single()
            assertEquals(HomeRowStyle.Playlists, row.style)
            assertEquals(listOf("Road trip"), row.playlists!!.map { it.name })
        }
    }

    @Test
    fun `music rows that load empty stay empty, without fetching playlists`() = runTest {
        MockWebServer().use { server ->
            server.start()
            server.enqueue(json("""{"featured":[],"rows":[]}"""))
            assertEquals(emptyList<HomeRow>(), FlexarrClient(server.url("/")).musicRows("lib"))
            assertEquals(1, server.requestCount)
        }
    }

    @Test
    fun `a Made for you row has its mixes and no items`() {
        val row = FlexarrClient.json.decodeFromString(
            serializer<HomeRow>(),
            """{"id":"music-mixes","title":"Made for you","style":"mixes","items":[],"link":null,"channels":null,"playlists":null,
            "mixes":[{"id":"1a2b3c4d","name":"Rock Mix","description":"Dire Straits, Queen and more",
            "covers":["/api/v1/images/items/a/poster"],"trackCount":40}]}""",
        )
        assertEquals(MUSIC_MIXES_ROW_ID, row.id)
        assertEquals(HomeRowStyle.Mixes, row.style)
        assertTrue(row.items.isEmpty())
        val mix = row.mixes!!.single()
        assertEquals("Rock Mix", mix.name)
        assertEquals(40, mix.trackCount)
        assertEquals(1, mix.covers.size)
    }

    @Test
    fun `a row from a server before mixes has none`() {
        val row = FlexarrClient.json.decodeFromString(serializer<HomeRow>(), """{"id":"recent","title":"Latest","style":"poster","items":[]}""")
        assertNull(row.mixes)
    }

    @Test
    fun `a mix is fetched with its tracks in order`() = runTest {
        MockWebServer().use { server ->
            server.start()
            val tracks = text("tracks")
            server.enqueue(json("""{"summary":{"id":"1a2b3c4d","name":"Rock Mix","description":"","covers":[],"trackCount":3},"tracks":$tracks}"""))
            val mix = FlexarrClient(server.url("/")).mix("1a2b3c4d")
            assertEquals("/api/v1/music/mixes/1a2b3c4d", server.takeRequest().url.encodedPath)
            assertEquals("Rock Mix", mix.summary.name)
            assertEquals(fixture<List<ItemSummary>>("tracks"), mix.tracks)
        }
    }

    @Test
    fun `a library's albums are paged, sorted and filtered like its titles`() = runTest {
        MockWebServer().use { server ->
            server.start()
            val tracks = text("tracks")
            server.enqueue(json("""{"total":41,"items":$tracks}"""))
            val page = FlexarrClient(server.url("/")).libraryAlbums("lib", LibrarySort.Year, genre = "Rock", skip = 60, take = 60)!!
            val url = server.takeRequest().url
            assertEquals("/api/v1/libraries/lib/albums", url.encodedPath)
            assertEquals("year", url.queryParameter("sort"))
            assertEquals("Rock", url.queryParameter("genre"))
            assertEquals("60", url.queryParameter("skip"))
            assertEquals(41, page.total)
            assertEquals(3, page.items.size)
        }
    }

    /** What a server answers for a path it doesn't know: the web app's page. */
    private fun webPage() = MockResponse.Builder().code(200).addHeader("Content-Type", "text/html; charset=utf-8").body("<!doctype html><html></html>").build()

    @Test
    fun `a server before albums has no Albums or Genres`() = runTest {
        MockWebServer().use { server ->
            server.start()
            // 0.58 answers an unknown path with the web app's page.
            server.enqueue(webPage())
            server.enqueue(webPage())
            val client = FlexarrClient(server.url("/"))
            assertNull(client.libraryAlbums("lib"))
            assertNull(client.libraryAlbumGenres("lib"))
            assertEquals(listOf(MusicView.Browse, MusicView.Artists), MusicView.offered(albums = false))
            assertEquals(MusicView.entries, MusicView.offered(albums = true))
        }
    }

    @Test
    fun `a 404 for albums reads as a server without them too`() = runTest {
        MockWebServer().use { server ->
            server.start()
            server.enqueue(MockResponse.Builder().code(404).build())
            server.enqueue(MockResponse.Builder().code(404).build())
            val client = FlexarrClient(server.url("/"))
            assertNull(client.libraryAlbums("lib"))
            assertNull(client.libraryAlbumGenres("lib"))
        }
    }

    private fun status(version: String) = json("""{"appName":"Flexarr","version":"$version"}""")

    @Test
    fun `albums are offered by the server's version, without asking for one`() = runTest {
        MockWebServer().use { server ->
            server.start()
            server.enqueue(status("0.58.0"))
            server.enqueue(status("0.59.0-dev.1a2b3c4"))
            server.enqueue(status("0.59.0+d4c892c6"))
            val client = FlexarrClient(server.url("/"))
            assertEquals(false, client.offersAlbums("lib"))
            assertEquals(true, client.offersAlbums("lib"))
            assertEquals(true, client.offersAlbums("lib"))
            assertEquals(3, server.requestCount)
        }
    }

    @Test
    fun `a version that doesn't read falls back to asking for one album`() = runTest {
        MockWebServer().use { server ->
            server.start()
            server.enqueue(status("custom"))
            server.enqueue(json("""{"total":3,"items":[]}"""))
            server.enqueue(MockResponse.Builder().code(500).build())
            server.enqueue(webPage())
            val client = FlexarrClient(server.url("/"))
            assertEquals(true, client.offersAlbums("lib"))
            server.takeRequest()
            val probe = server.takeRequest().url
            assertEquals("/api/v1/libraries/lib/albums", probe.encodedPath)
            assertEquals("1", probe.queryParameter("take"))
            assertEquals(false, client.offersAlbums("lib"))
        }
    }

    @Test
    fun `album genres count albums and carry an artist's fanart`() = runTest {
        MockWebServer().use { server ->
            server.start()
            server.enqueue(json("""[{"name":"Rock","count":12,"backdrop":"/api/v1/images/items/a/backdrop"},{"name":"Jazz","count":1}]"""))
            val genres = FlexarrClient(server.url("/")).libraryAlbumGenres("lib")!!
            assertEquals("/api/v1/libraries/lib/album-genres", server.takeRequest().url.encodedPath)
            assertEquals(listOf("Rock", "Jazz"), genres.map { it.name })
            assertNull(genres[1].backdrop)
            assertEquals("12 albums", albumCount(genres[0].count))
            assertEquals("1 album", albumCount(genres[1].count))
        }
    }

    @Test
    fun `a genre's tracks are asked for shuffled only when shuffling, and radio by artist`() = runTest {
        MockWebServer().use { server ->
            server.start()
            val tracks = text("tracks")
            repeat(3) { server.enqueue(json(tracks)) }
            val client = FlexarrClient(server.url("/"))
            client.genreTracks("lib", "Hip Hop")
            client.genreTracks("lib", "Hip Hop", shuffled = true)
            assertEquals(3, client.radio("artist").size)
            val plain = server.takeRequest().url
            assertEquals("/api/v1/libraries/lib/tracks", plain.encodedPath)
            assertEquals("Hip Hop", plain.queryParameter("genre"))
            assertNull(plain.queryParameter("shuffle"))
            assertEquals("true", server.takeRequest().url.queryParameter("shuffle"))
            assertEquals("/api/v1/items/artist/radio", server.takeRequest().url.encodedPath)
        }
    }

    @Test
    fun `a music library's Browse decodes its hero, bios and the tracks albums carry on from`() {
        val tracks = text("tracks")
        val captured = FlexarrClient.json.parseToJsonElement(tracks) as kotlinx.serialization.json.JsonArray
        val home = FlexarrClient.json.decodeFromString(
            serializer<Home>(),
            """{"featured":$tracks,"overviews":{"x":"A band from London."},"rows":[],"resumes":{"album":${captured[1]}}}""",
        )
        assertEquals(3, home.featured.size)
        assertEquals("A band from London.", home.overviews["x"])
        assertEquals(fixture<List<ItemSummary>>("tracks")[1], home.resumes!!["album"])
    }

    @Test
    fun `an album resumes at the track it was left on, part way in only when half played`() {
        val tracks = fixture<List<ItemSummary>>("tracks")
        val second = tracks[1]
        assertEquals(AlbumStart(0, null), albumStart(tracks, null))
        val halfWay = second.copy(userData = second.userData.copy(positionSeconds = 93.5, watched = false))
        assertEquals(AlbumStart(1, 93.5), albumStart(tracks, halfWay))
        // Finished, or not started: that track from its beginning.
        assertEquals(AlbumStart(1, null), albumStart(tracks, halfWay.copy(userData = halfWay.userData.copy(watched = true))))
        assertEquals(AlbumStart(1, null), albumStart(tracks, halfWay.copy(userData = halfWay.userData.copy(positionSeconds = 0.0))))
        // A track no longer on the album: from the top.
        assertEquals(AlbumStart(0, null), albumStart(tracks, halfWay.copy(id = "gone")))
    }

    @Test
    fun `artist and album counts read as the other apps do`() {
        assertEquals("1 artist", artistCount(1))
        assertEquals("1,204 artists", artistCount(1204))
        assertEquals(listOf(LibrarySort.Title, LibrarySort.Added), MusicView.Artists.sorts)
        assertEquals(listOf(LibrarySort.Title, LibrarySort.Added, LibrarySort.Year), MusicView.Albums.sorts)
    }
}
