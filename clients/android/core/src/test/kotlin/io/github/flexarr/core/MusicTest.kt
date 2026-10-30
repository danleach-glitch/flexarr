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
}
