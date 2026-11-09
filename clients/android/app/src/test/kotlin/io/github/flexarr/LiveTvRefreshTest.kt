package io.github.flexarr

import android.app.Application
import android.os.Looper
import io.github.flexarr.core.LiveTvStatus
import io.github.flexarr.core.i18n.Words
import kotlinx.coroutines.launch
import mockwebserver3.MockResponse
import mockwebserver3.SocketEffect
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
    @Volatile private var refreshAnswer: MockResponse = MockResponse.Builder().code(202).build()
    private val server = TestServer { request ->
        when {
            request.url.encodedPath.endsWith("/livetv/status") -> statusAnswer
            request.url.encodedPath.endsWith("/livetv/refresh") -> refreshAnswer
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

    private fun lookAtStatus(): Boolean {
        var heard: Boolean? = null
        model.scope.launch { heard = model.reloadLiveTvStatus() }
        server.waitUntil("the look") { heard != null }
        return heard!!
    }

    private val noConnection get() = MockResponse.Builder().onResponseStart(SocketEffect.CloseSocket()).build()

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

    /** Asked, then the server stops answering for long enough to be told so. */
    private fun lostTheServer() {
        refreshAsked()
        statusAnswer = TestServer.json("{}", 502)
        server.waitUntil("the refresh") { model.liveTvRefreshesEnded == 1 }
        assertEquals(Words.serverDidntAnswer, model.takeLiveTvRefreshEnd()!!.words)
        assertTrue("The server carries on without us", model.isLiveTvRefreshTakingAWhile)
    }

    @Test fun afterNoAnswer_aLookThatStillFails_changesNothing() {
        lostTheServer()
        lookAtStatus()
        assertEquals(1, model.liveTvRefreshesEnded)
        assertTrue(model.isLiveTvRefreshTakingAWhile)
    }

    @Test fun afterNoAnswer_theEndIsSaidAtTheNextLook() {
        lostTheServer()
        statusAnswer = TestServer.json(status(finished = 5))
        lookAtStatus()
        assertEquals(2, model.liveTvRefreshesEnded)
        assertFalse(model.isLiveTvRefreshTakingAWhile)
        assertTrue(model.takeLiveTvRefreshEnd()!!.worked)

        lookAtStatus()
        assertEquals("Said once", 2, model.liveTvRefreshesEnded)
    }

    @Test fun afterNoAnswer_aFailedEndIsSaidToo() {
        lostTheServer()
        statusAnswer = TestServer.json(status(finished = 5, extra = ""","error":"No sources""""))
        lookAtStatus()
        assertEquals("No sources", model.takeLiveTvRefreshEnd()!!.words)
    }

    @Test fun afterNoAnswer_stillRunning_saysSoOnce_thenHowItEnded() {
        lostTheServer()
        statusAnswer = TestServer.json(status(refreshing = true))
        lookAtStatus()
        assertEquals(2, model.liveTvRefreshesEnded)
        assertTrue("Replaces the line that says it didn't answer", model.takeLiveTvRefreshEnd()!!.isTakingAWhile)
        assertTrue(model.isLiveTvRefreshTakingAWhile)

        lookAtStatus()
        assertEquals(2, model.liveTvRefreshesEnded)
        assertNull(model.takeLiveTvRefreshEnd())

        statusAnswer = TestServer.json(status(finished = 5))
        lookAtStatus()
        assertEquals(3, model.liveTvRefreshesEnded)
        assertTrue(model.takeLiveTvRefreshEnd()!!.worked)
    }

    @Test fun afterNoAnswer_anEndLongAfter_isntAnnounced_butTheScreensStillHear() {
        lostTheServer()
        pass(Duration.ofMinutes(5))
        statusAnswer = TestServer.json(status(finished = 5))
        lookAtStatus()
        assertEquals(2, model.liveTvRefreshesEnded)
        assertNull(model.takeLiveTvRefreshEnd())
        assertFalse(model.isLiveTvRefreshTakingAWhile)
    }

    @Test fun afterNoAnswer_aNewRefreshStartsAfresh() {
        lostTheServer()
        statusAnswer = TestServer.json(status(refreshing = true))
        refreshAsked()
        assertFalse(model.isLiveTvRefreshTakingAWhile)
        lookAtStatus()
        assertEquals("No news from the old one while the new one's waited for", 1, model.liveTvRefreshesEnded)
        statusAnswer = TestServer.json(status(finished = 5))
        server.waitUntil("the refresh") { model.liveTvRefreshesEnded == 2 }
        assertTrue(model.takeLiveTvRefreshEnd()!!.worked)
    }

    @Test fun aRefusedRefresh_isntGivenUpOn_soIsntAnnouncedAsDone() {
        refreshAnswer = TestServer.json("""{"title":"Unlock first"}""", 403)
        refreshAndWait()
        assertTrue(model.takeLiveTvRefreshEnd()!!.isProblem)
        assertFalse(model.isLiveTvRefreshTakingAWhile)
        lookAtStatus()
        assertEquals(1, model.liveTvRefreshesEnded)
        assertNull(model.takeLiveTvRefreshEnd())
    }

    @Test fun aServerThatNeverAnswered_wasNeverAsked_soIsntAnnouncedAsDone() {
        statusAnswer = TestServer.json("{}", 502)
        refreshAndWait()
        assertTrue(model.takeLiveTvRefreshEnd()!!.isProblem)
        assertEquals(0, server.requestsTo("/livetv/refresh").size)
        assertFalse(model.isLiveTvRefreshTakingAWhile)
        statusAnswer = TestServer.json(status())
        lookAtStatus()
        assertEquals(1, model.liveTvRefreshesEnded)
    }

    @Test fun aLook_saysWhetherTheServerWasHeardFrom() {
        assertTrue(lookAtStatus())
        statusAnswer = TestServer.json("{}", 502)
        assertFalse(lookAtStatus())
        statusAnswer = noConnection
        assertFalse(lookAtStatus())
        statusAnswer = MockResponse.Builder().code(404).build()
        assertFalse("Nothing to go on: no Live TV there", lookAtStatus())
    }

    @Test fun aRefusedPress_standsWhateverTheStatusSays() {
        refreshAnswer = TestServer.json("""{"title":"Unlock first"}""", 403)
        refreshAndWait()
        val said = model.takeLiveTvRefreshEnd()!!
        assertEquals("Unlock first", said.words)
        assertEquals(said, said.following(parsed()))
        assertEquals(said, said.following(parsed(error = "Something else")))
    }

    @Test fun aPressThatDidntGetThere_isntARefusal() {
        refreshAnswer = noConnection
        refreshAndWait()
        assertNull("Goes with a good look", model.takeLiveTvRefreshEnd()!!.following(parsed()))

        refreshAnswer = TestServer.json("{}", 502)
        refreshAndWait(ended = 2)
        assertNull(model.takeLiveTvRefreshEnd()!!.following(parsed()))
    }

    private fun parsed(refreshing: Boolean = false, error: String? = null, downloadError: String? = null, skipped: Boolean = false) =
        LiveTvStatus(configured = true, channels = 1, refreshing = refreshing, error = error, downloadError = downloadError, downloadSkipped = skipped)

    @Test fun aProblemNote_followsTheStatus() {
        val note = LiveTvRefreshOutcome("Was wrong", isProblem = true)
        assertNull("A clean status clears it", note.following(parsed()))
        assertEquals("Under way settles nothing", note, note.following(parsed(refreshing = true)))
        assertEquals("No status settles nothing", note, note.following(null))
        assertEquals("Now wrong", note.following(parsed(error = "Now wrong"))?.words)
        val kept = note.following(parsed(downloadError = "Source failed", skipped = true))!!
        assertEquals("Source failed", kept.words)
        assertEquals(Words.guideSourcesAskedRecently, kept.sourcesNote)
    }

    @Test fun aNoteThatWorked_isLeftAlone() {
        val worked = LiveTvRefreshOutcome("Guide refreshed")
        assertEquals(worked, worked.following(parsed(error = "Later failure")))
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
