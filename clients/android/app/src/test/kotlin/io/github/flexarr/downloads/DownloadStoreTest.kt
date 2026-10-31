package io.github.flexarr.downloads

import io.github.flexarr.core.DownloadQuality
import io.github.flexarr.core.FlexarrClient
import io.github.flexarr.core.LocalDownload
import io.github.flexarr.core.LocalPhase
import io.github.flexarr.core.MediaKind
import io.github.flexarr.core.PlaybackInfo
import io.github.flexarr.fixture
import kotlinx.serialization.builtins.ListSerializer
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.jsonObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.io.File

/** Reading the saved downloads list back: what one app version wrote, another must not lose. */
class DownloadStoreTest {
    @get:Rule
    val folder = TemporaryFolder()

    private val episode = fixture<PlaybackInfo>("playback").item

    private fun download(id: String) = LocalDownload(
        id = id,
        profileId = "p",
        quality = DownloadQuality.Medium,
        item = episode,
        phase = LocalPhase.Downloaded,
        fileName = "$id.mp4",
    )

    private fun element(download: LocalDownload) = FlexarrClient.json.encodeToJsonElement(LocalDownload.serializer(), download).jsonObject

    private val manifest: File get() = File(folder.root, "downloads.json")

    /** Each time the list couldn't all be read: how many entries, and where the file was copied. */
    private val reports = mutableListOf<Pair<Int, File?>>()

    private fun load() = DownloadStore.load(manifest, LocalDownload.serializer()) { count, keptAs -> reports += count to keptAs }

    @Test
    fun `no list yet is no downloads`() {
        assertEquals(emptyList<LocalDownload>(), load())
        assertTrue(reports.isEmpty())
    }

    @Test
    fun `a list that reads comes back as saved, and isn't copied`() {
        val saved = listOf(download("a"), download("b"))
        manifest.writeText(FlexarrClient.json.encodeToString(ListSerializer(LocalDownload.serializer()), saved))

        assertEquals(saved, load())
        assertTrue(reports.isEmpty())
        assertEquals(listOf("downloads.json"), folder.root.list()!!.toList())
    }

    @Test
    fun `a kind a newer app saved is kept, as unknown`() {
        val newer = element(download("a")).let { entry ->
            JsonObject(entry + ("item" to JsonObject(entry["item"]!!.jsonObject + ("kind" to JsonPrimitive("Hologram")))))
        }
        manifest.writeText(JsonArray(listOf(newer)).toString())

        val loaded = load().single()

        assertEquals(MediaKind.Unknown, loaded.item.kind)
        assertEquals("a", loaded.id)
        assertTrue(reports.isEmpty())
    }

    @Test
    fun `an entry that doesn't read is dropped, and the file is copied aside first`() {
        val broken = JsonObject(element(download("b")) - "profileId")
        val text = JsonArray(listOf(element(download("a")), broken)).toString()
        manifest.writeText(text)

        val loaded = load()

        assertEquals(listOf("a"), loaded.map { it.id })
        val (count, keptAs) = reports.single()
        assertEquals(1, count)
        assertNotNull(keptAs)
        assertTrue(keptAs!!.name, keptAs.name.matches(Regex("""downloads\.unreadable-.+\.json""")))
        assertEquals(text, keptAs.readText())
        // The list itself is left as it was, for the next save to replace.
        assertEquals(text, manifest.readText())
    }

    @Test
    fun `a file that isn't a list at all counts as one unreadable entry, and is kept`() {
        manifest.writeText("{ not json")

        assertEquals(emptyList<LocalDownload>(), load())
        val (count, keptAs) = reports.single()
        assertEquals(1, count)
        assertEquals("{ not json", keptAs!!.readText())
    }

    @Test
    fun `when the copy can't be made, it says so rather than failing`() {
        manifest.writeText("[1]")
        // Nothing can be written beside the list.
        folder.root.setWritable(false)
        try {
            assertEquals(emptyList<LocalDownload>(), load())
        } finally {
            folder.root.setWritable(true)
        }
        val (count, keptAs) = reports.single()
        assertEquals(1, count)
        assertNull(keptAs)
    }
}
