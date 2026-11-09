package io.github.flexarr

import android.app.Application
import android.os.Looper
import io.github.flexarr.core.i18n.Words
import kotlinx.coroutines.launch
import mockwebserver3.MockResponse
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.Shadows.shadowOf
import org.robolectric.annotation.Config
import java.time.Duration

/** The real model refreshing against a stand-in server: what it says, when, and that it's said once. */
@RunWith(RobolectricTestRunner::class)
@Config(application = Application::class, sdk = [34])
class LiveTvRefreshTest {
    private fun status(refreshing: Boolean = false, finished: Int = 4, extra: String = "") =
        """{"configured":true,"channels":1,"refreshing":$refreshing,"refreshesFinished":$finished$extra}"""

    @Volatile private var statusAnswer: MockResponse = TestServer.json(status())
    private val server = TestServer { request ->
        when {
            request.url.encodedPath.endsWith("/livetv/status") -> statusAnswer
            request.url.encodedPath.endsWith("/livetv/refresh") -> MockResponse.Builder().code(202).build()
            else -> null
        }
    }
    private val model get() = server.model

    @After fun close() = server.close()

    private fun refreshAndWait(ended: Int = 1) {
        model.refreshLiveTv()
        server.waitUntil("the refresh to end") { model.liveTvRefreshesEnded == ended }
    }

    /** Refreshes, as far as the server having been asked: what it says from here is this refresh's. */
    private fun refreshAsked() {
        val asked = server.requestsTo("/livetv/refresh").size
        model.refreshLiveTv()
        server.waitUntil("the ask") { server.requestsTo("/livetv/refresh").size == asked + 1 }
    }

    private fun lookAtStatus() {
        var done = false
        model.scope.launch {
            model.reloadLiveTvStatus()
            done = true
        }
        server.waitUntil("the look") { done }
    }

    private fun pass(duration: Duration) = shadowOf(Looper.getMainLooper()).idleFor(duration)

    /** Refreshes with the server still at it when the wait runs out. */
    private fun givenUpOn() {
        statusAnswer = TestServer.json(status(refreshing = true))
        refreshAndWait()
        assertTrue(model.takeLiveTvRefreshEnd()!!.isTakingAWhile)
        assertTrue(model.isLiveTvRefreshTakingAWhile)
    }

    @Test fun aSlowRefreshThatFailsLater_isSaidAtTheNextLook() {
        givenUpOn()
        // Still at it: nothing to say yet.
        lookAtStatus()
        assertEquals(1, model.liveTvRefreshesEnded)
        assertTrue(model.isLiveTvRefreshTakingAWhile)

        statusAnswer = TestServer.json(status(finished = 5, extra = ""","downloadError":"The source failed""""))
        lookAtStatus()
        assertEquals("Goes up, so the screens load the guide again", 2, model.liveTvRefreshesEnded)
        assertFalse(model.isLiveTvRefreshTakingAWhile)
        val said = model.takeLiveTvRefreshEnd()!!
        assertEquals("The source failed", said.words)
        assertTrue(said.isProblem)

        // Said once.
        lookAtStatus()
        assertEquals(2, model.liveTvRefreshesEnded)
        assertNull(model.takeLiveTvRefreshEnd())
    }

    @Test fun aSlowRefreshThatWorksLater_isSaidToo() {
        givenUpOn()
        statusAnswer = TestServer.json(status(finished = 5))
        lookAtStatus()
        assertTrue(model.takeLiveTvRefreshEnd()!!.worked)
    }

    @Test fun aSlowRefreshNobodyCameBackTo_isntAnnouncedLongAfter_butTheGuideStillLoads() {
        givenUpOn()
        pass(Duration.ofHours(20))
        statusAnswer = TestServer.json(status(finished = 5, extra = ""","downloadError":"The source failed""""))
        lookAtStatus()
        assertEquals(2, model.liveTvRefreshesEnded)
        assertNull(model.takeLiveTvRefreshEnd())
    }

    @Test fun aNewRefresh_forgetsTheOneGivenUpOn() {
        givenUpOn()
        statusAnswer = TestServer.json(status())
        refreshAsked()
        assertFalse(model.isLiveTvRefreshTakingAWhile)
        statusAnswer = TestServer.json(status(finished = 5))
        server.waitUntil("the refresh") { model.liveTvRefreshesEnded == 2 }
        assertTrue(model.takeLiveTvRefreshEnd()!!.worked)
    }

    @Test fun aResultHeldOverTwoMinutes_isDropped() {
        refreshAsked()
        statusAnswer = TestServer.json(status(finished = 5, extra = ""","error":"No sources""""))
        server.waitUntil("the refresh") { model.liveTvRefreshesEnded == 1 }
        pass(Duration.ofMinutes(3))
        assertNull(model.takeLiveTvRefreshEnd())
    }

    @Test fun aServerThatStopsAnswering_saysTheSharedWords() {
        refreshAsked()
        statusAnswer = TestServer.json("{}", 502)
        server.waitUntil("the refresh") { model.liveTvRefreshesEnded == 1 }
        val said = model.takeLiveTvRefreshEnd()!!
        assertEquals(Words.serverDidntAnswer, said.words)
        assertTrue(said.isProblem)
    }

    @Test fun aSourceNotAskedAgain_saysSoBesideItsFailure_andOnlyThen() {
        refreshAsked()
        statusAnswer = TestServer.json(status(finished = 5, extra = ""","downloadError":"The source failed","downloadSkipped":true"""))
        server.waitUntil("the refresh") { model.liveTvRefreshesEnded == 1 }
        val skipped = model.takeLiveTvRefreshEnd()!!
        assertEquals(Words.guideSourcesAskedRecently, skipped.sourcesNote)
        assertEquals("The source failed ${Words.guideSourcesAskedRecently}", skipped.allWords)

        // An older server sends no such field.
        refreshAsked()
        statusAnswer = TestServer.json(status(finished = 6, extra = ""","downloadError":"The source failed""""))
        server.waitUntil("the refresh") { model.liveTvRefreshesEnded == 2 }
        assertNull(model.takeLiveTvRefreshEnd()!!.sourcesNote)
    }
}
