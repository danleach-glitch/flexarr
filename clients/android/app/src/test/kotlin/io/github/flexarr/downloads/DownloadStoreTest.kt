package io.github.flexarr.downloads

import io.github.flexarr.core.DownloadQuality
import io.github.flexarr.core.FlexarrClient
import io.github.flexarr.core.LocalDownload
import io.github.flexarr.core.LocalPhase
import io.github.flexarr.core.MediaKind
import io.github.flexarr.core.PlaybackInfo
import io.github.flexarr.fixture
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.launch
import kotlinx.coroutines.test.TestScope
import kotlinx.coroutines.test.advanceTimeBy
import kotlinx.coroutines.test.currentTime
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
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

/** Reading the saved downloads list back: what one app version wrote, another must not lose. And how often it's saved. */
@OptIn(ExperimentalCoroutinesApi::class)
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
    fun `a short list is saved again a moment later, as it always was`() {
        assertEquals(250L, DownloadStore.saveGapMillis(0))
        assertEquals(250L, DownloadStore.saveGapMillis(5))
        assertEquals(250L, DownloadStore.saveGapMillis(25))
    }

    @Test
    fun `a list that takes long to write rests ten times as long before the next save, two seconds at most`() {
        assertEquals(400L, DownloadStore.saveGapMillis(40))
        assertEquals(1_000L, DownloadStore.saveGapMillis(100))
        assertEquals(2_000L, DownloadStore.saveGapMillis(200))
        assertEquals(2_000L, DownloadStore.saveGapMillis(5_000))
    }

    /** The store's save loop, with lists that take [took] to write: what was written, and when. */
    private class Saves(private val scope: TestScope, private val took: Long) {
        val saves = Channel<String>(Channel.CONFLATED)
        val hurry = Channel<Unit>(Channel.CONFLATED)
        val written = mutableListOf<Pair<String, Long>>()

        init {
            scope.backgroundScope.launch {
                DownloadStore.saveAsTheyCome(saves, hurry) { list ->
                    written += list to scope.currentTime
                    took
                }
            }
        }

        fun save(list: String) {
            saves.trySend(list)
        }

        /** As deleting a download does: the list without it, to be saved without the long rest. */
        fun delete(list: String) {
            save(list)
            hurry.trySend(Unit)
        }

        fun at(millis: Long) {
            scope.advanceTimeBy(millis - scope.currentTime)
            scope.runCurrent()
        }
    }

    @Test
    fun `a change is saved at once, and a burst after it is one save of the newest`() = runTest {
        val saves = Saves(this, took = 5)

        saves.save("a")
        saves.at(0)
        assertEquals(listOf("a" to 0L), saves.written)

        saves.at(100)
        saves.save("b")
        saves.save("c")
        saves.at(249)
        assertEquals(listOf("a" to 0L), saves.written)
        saves.at(250)
        assertEquals(listOf("a" to 0L, "c" to 250L), saves.written)

        // Nothing more to save: nothing more written, and the next change after a quiet spell goes at once.
        saves.at(10_000)
        saves.save("d")
        saves.at(10_000)
        assertEquals(listOf("a" to 0L, "c" to 250L, "d" to 10_000L), saves.written)
    }

    @Test
    fun `a long list's changes wait out its rest, and the newest is saved after it`() = runTest {
        val saves = Saves(this, took = 100)

        saves.save("a")
        saves.at(0)
        // A thousand tracks arriving four times a second.
        for (n in 1..3) {
            saves.at(n * 250L)
            saves.save("b$n")
        }
        saves.at(999)
        assertEquals(listOf("a" to 0L), saves.written)
        saves.at(1_000)
        assertEquals(listOf("a" to 0L, "b3" to 1_000L), saves.written)
    }

    @Test
    fun `a deletion is saved without the long rest, though not sooner than a short list's would be`() = runTest {
        val saves = Saves(this, took = 200)

        saves.save("a")
        saves.at(0)
        // Deleted during the moment every save leaves: saved when that's over.
        saves.at(100)
        saves.delete("b")
        saves.at(249)
        assertEquals(listOf("a" to 0L), saves.written)
        saves.at(250)
        assertEquals(listOf("a" to 0L, "b" to 250L), saves.written)

        // Deleted during the long rest after that: saved at once.
        saves.at(1_000)
        saves.delete("c")
        saves.at(1_000)
        assertEquals(listOf("a" to 0L, "b" to 250L, "c" to 1_000L), saves.written)

        // An ordinary change after it still waits the two seconds out.
        saves.at(1_300)
        saves.save("d")
        saves.at(2_999)
        assertEquals(3, saves.written.size)
        saves.at(3_000)
        assertEquals("d" to 3_000L, saves.written.last())
    }

    @Test
    fun `a deletion that is itself the save being written doesn't cut the next rest short`() = runTest {
        val saves = Saves(this, took = 200)

        saves.delete("a")
        saves.at(0)
        saves.at(300)
        saves.save("b")
        saves.at(1_999)
        assertEquals(listOf("a" to 0L), saves.written)
        saves.at(2_000)
        assertEquals(listOf("a" to 0L, "b" to 2_000L), saves.written)
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
