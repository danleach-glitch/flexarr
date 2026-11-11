package io.github.flexarr.downloads

import android.app.Application
import io.github.flexarr.TestServer
import io.github.flexarr.core.DownloadQuality
import kotlinx.coroutines.launch
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

/** Removing a downloaded show: its episodes leave the list in one go, their files go, and the server hears of each. */
@RunWith(RobolectricTestRunner::class)
@Config(application = Application::class, sdk = [34])
class DownloadRemoveAllTest {
    private val server = TestServer { request ->
        val path = request.url.encodedPath
        when {
            request.method == "POST" && path.endsWith("/offline") -> {
                val itemId = Regex("\"itemId\":\"([^\"]+)\"").find(request.body!!.utf8())!!.groupValues[1]
                TestServer.json(
                    """{"id":"d-$itemId","itemId":"$itemId","quality":"Medium","status":"Queued",
                    "item":{"id":"$itemId","libraryId":"tv","kind":"Episode","title":"$itemId","childCount":0,"artwork":{},
                    "userData":{"positionSeconds":0,"watched":false,"playCount":0},"seriesId":"show","seriesTitle":"Show"}}""",
                )
            }
            request.method == "DELETE" && path.contains("/offline/") -> mockwebserver3.MockResponse.Builder().code(204).build()
            else -> null
        }
    }
    private val model get() = server.model
    private val store get() = model.downloads

    @After
    fun tearDown() = server.close()

    @Test
    fun `a show's episodes go at once, with their files, and the server is told`() {
        store.attach(model.client, "p")
        var asked = 0
        model.scope.launch {
            for (id in listOf("e1", "e2", "e3")) store.download(id, DownloadQuality.Medium, automatic = false)
            asked = 3
        }
        server.waitUntil("the episodes asked for") { asked == 3 && store.all.size == 3 }
        val folder = DownloadStore.folder(org.robolectric.RuntimeEnvironment.getApplication())
        folder.mkdirs()
        val part = DownloadStore.fileIn(folder, "${DownloadStore.safeName("d-e2")}.part")!!.apply { writeText("half") }

        store.removeAll(store.all.map { it.id })

        assertTrue(store.all.isEmpty())
        server.waitUntil("the server told") { server.requests.count { it.method == "DELETE" } == 3 }
        server.waitUntil("the file deleted") { !part.exists() }
        assertFalse(part.exists())
        assertEquals(setOf("/api/v1/offline/d-e1", "/api/v1/offline/d-e2", "/api/v1/offline/d-e3"), server.requests.filter { it.method == "DELETE" }.map { it.url.encodedPath }.toSet())
    }
}
