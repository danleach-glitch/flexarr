package io.github.flexarr

import android.content.ComponentName
import android.os.Handler
import android.os.HandlerThread
import androidx.media3.session.MediaBrowser
import androidx.media3.session.SessionToken
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import io.github.flexarr.audio.AudioService
import io.github.flexarr.audio.CarLibrary
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Assume.assumeTrue
import org.junit.Test
import org.junit.runner.RunWith
import java.util.concurrent.FutureTask
import java.util.concurrent.TimeUnit

/**
 * Browses and plays as Android Auto does, through a MediaBrowser, against a real server. Run with
 * `./gradlew :app:connectedDebugAndroidTest -Pandroid.testInstrumentationRunnerArguments.server=<url>
 * -Pandroid.testInstrumentationRunnerArguments.apiKey=<key>`; without them it's skipped. Nothing is recorded (no history).
 */
@RunWith(AndroidJUnit4::class)
class CarLibraryTest {
    @Test
    fun browsesAndPlaysAsTheCarDoes() {
        val instrumentation = InstrumentationRegistry.getInstrumentation()
        val arguments = InstrumentationRegistry.getArguments()
        val server = arguments.getString("server")
        val key = arguments.getString("apiKey")
        assumeTrue("No server given", server != null && key != null)
        val context = instrumentation.targetContext
        val model = context.appModel
        instrumentation.runOnMainSync {
            model.recordsHistory = false
            model.signInForThisLaunch(server!!, key!!)
        }

        // The browser is called on a thread of its own (as a car's connection would be), its answers waited for here.
        val thread = HandlerThread("car").apply { start() }
        val handler = Handler(thread.looper)
        fun <T> onBrowser(block: () -> T): T = FutureTask(block).also { handler.post(it) }.get(10, TimeUnit.SECONDS)
        val token = SessionToken(context, ComponentName(context, AudioService::class.java))
        val browser = onBrowser { MediaBrowser.Builder(context, token).setApplicationLooper(thread.looper).buildAsync() }.get(10, TimeUnit.SECONDS)
        fun children(id: String) = onBrowser { browser.getChildren(id, 0, 50, null) }.get(20, TimeUnit.SECONDS).value!!
        try {
            val root = onBrowser { browser.getLibraryRoot(null) }.get(10, TimeUnit.SECONDS).value!!
            assertEquals(CarLibrary.ROOT, root.mediaId)
            val tabs = children(root.mediaId)
            assertEquals(listOf("Up Next", "New episodes", "Podcasts", "Music"), tabs.map { it.mediaMetadata.title.toString() })

            val shows = children(CarLibrary.PODCASTS)
            assertTrue("No podcasts followed", shows.isNotEmpty())
            assertTrue(shows.all { it.mediaMetadata.isBrowsable == true })
            val episodes = children(shows.first().mediaId)
            val music = children(CarLibrary.MUSIC)
            assertTrue("No music", music.isNotEmpty())
            val tracks = children(music.first().mediaId)
            // Searching (the car's search screen, and how "play …" is heard): a show followed is found by its name.
            val showName = shows.first().mediaMetadata.title.toString()
            onBrowser { browser.search(showName, null) }.get(20, TimeUnit.SECONDS)
            val found = onBrowser { browser.getSearchResult(showName, 0, 50, null) }.get(20, TimeUnit.SECONDS).value!!
            assertTrue("$showName not found by searching", found.any { it.mediaId == shows.first().mediaId })

            println("CarLibraryTest: ${shows.size} shows, ${episodes.size} episodes in the first, ${music.size} in Music, ${tracks.size} tracks in ${music.first().mediaMetadata.title}")

            // The car plays an episode: the app's own podcast player takes it.
            val episode = episodes.firstOrNull() ?: children(CarLibrary.NEW).first()
            onBrowser {
                browser.setMediaItem(episode)
                browser.prepare()
                browser.play()
            }
            val id = episode.mediaId.removePrefix("episode:")
            val deadline = System.currentTimeMillis() + 15_000
            while (model.podcasts.current?.id != id && System.currentTimeMillis() < deadline) Thread.sleep(200)
            assertEquals(id, model.podcasts.current?.id)

            // And a track: the music player takes over, with the rest of the list queued after it.
            val track = tracks[minOf(1, tracks.lastIndex)]
            onBrowser {
                browser.setMediaItem(track)
                browser.prepare()
                browser.play()
            }
            val trackDeadline = System.currentTimeMillis() + 15_000
            while (model.music.current?.title != track.mediaMetadata.title.toString() && System.currentTimeMillis() < trackDeadline) Thread.sleep(200)
            assertEquals(track.mediaMetadata.title.toString(), model.music.current?.title)
            assertEquals(tracks.size, model.music.queue.size)
        } finally {
            onBrowser { browser.release() }
            thread.quitSafely()
            instrumentation.runOnMainSync {
                model.podcasts.stop()
                model.music.stop()
            }
        }
    }
}
