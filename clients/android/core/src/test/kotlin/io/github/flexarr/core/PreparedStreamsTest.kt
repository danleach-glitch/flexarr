package io.github.flexarr.core

import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.async
import kotlinx.coroutines.test.TestScope
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

@OptIn(ExperimentalCoroutinesApi::class)
class PreparedStreamsTest {
    private val profile = DeviceProfile(client = "android", containers = emptyList(), videoCodecs = emptyList(), audioCodecs = emptyList(),
        hdr = false, maxHeight = 1080, maxAudioChannels = 2, maxBitrate = 0)
    private fun info(id: String) = PlaybackInfo(sessionId = id, method = PlaybackMethod.DirectPlay, url = "/stream", durationSeconds = 60.0, startPositionSeconds = 0.0,
        item = ItemSummary(id = "ep", libraryId = "tv", kind = MediaKind.Episode, title = "Pilot", childCount = 0, artwork = Artwork(), userData = UserData(0.0, false, 0)))

    private fun TestScope.streams(answers: MutableList<CompletableDeferred<PlaybackInfo>>, stopped: MutableList<String>, clock: () -> Long = { 0L }) =
        PreparedStreams(this, start = { CompletableDeferred<PlaybackInfo>().also(answers::add).await() }, stop = { stopped.add(it) }, now = clock)

    @Test
    fun playTakesOverExactlyTheSameStream_EvenBeforeItsAnswer() = runTest {
        val answers = mutableListOf<CompletableDeferred<PlaybackInfo>>()
        val stopped = mutableListOf<String>()
        val streams = streams(answers, stopped)
        streams.getReady(PlaybackRequest(itemId = "ep", profile = profile, measuredBitrate = 8_000_000))
        runCurrent()
        val taking = async { streams.take(PlaybackRequest(itemId = "ep", profile = profile)) }
        runCurrent()
        answers.single().complete(info("s1"))
        assertEquals("s1", taking.await()?.sessionId)
        assertTrue(stopped.isEmpty())
    }

    @Test
    fun anythingElseIsntTakenOver() = runTest {
        val answers = mutableListOf<CompletableDeferred<PlaybackInfo>>()
        val streams = streams(answers, mutableListOf())
        streams.getReady(PlaybackRequest(itemId = "ep", profile = profile))
        runCurrent()
        answers.single().complete(info("s1"))
        runCurrent()
        assertNull(streams.take(PlaybackRequest(itemId = "ep", profile = profile, startPositionSeconds = 0.0)))
        assertNull(streams.take(PlaybackRequest(itemId = "ep", profile = profile, audioStreamIndex = 2)))
        assertTrue(streams.holds("ep"))
    }

    @Test
    fun anotherProfileNeverTakesItOver() = runTest {
        val answers = mutableListOf<CompletableDeferred<PlaybackInfo>>()
        val streams = streams(answers, mutableListOf())
        streams.getReady(PlaybackRequest(itemId = "ep", profile = profile), owner = "dad")
        runCurrent()
        answers.single().complete(info("s1"))
        runCurrent()
        assertNull(streams.take(PlaybackRequest(itemId = "ep", profile = profile), owner = "kid"))
        assertEquals("s1", streams.take(PlaybackRequest(itemId = "ep", profile = profile), owner = "dad")?.sessionId)
    }

    @Test
    fun anOldOneIsntUsed() = runTest {
        var clock = 0L
        val answers = mutableListOf<CompletableDeferred<PlaybackInfo>>()
        val streams = streams(answers, mutableListOf()) { clock }
        streams.getReady(PlaybackRequest(itemId = "ep", profile = profile))
        runCurrent()
        answers.single().complete(info("s1"))
        runCurrent()
        clock = PreparedStreams.MAX_AGE_MS + 1
        assertNull(streams.take(PlaybackRequest(itemId = "ep", profile = profile)))
    }

    @Test
    fun droppedOnesGo_IncludingOneThatAnswersLater() = runTest {
        val answers = mutableListOf<CompletableDeferred<PlaybackInfo>>()
        val stopped = mutableListOf<String>()
        val streams = streams(answers, stopped)
        streams.getReady(PlaybackRequest(itemId = "ep", profile = profile))
        runCurrent()
        streams.drop("film") // another title's: kept
        assertTrue(streams.holds("ep"))
        streams.drop("ep")
        assertFalse(streams.holds("ep"))
        answers.single().complete(info("late"))
        runCurrent()
        assertEquals(listOf("late"), stopped)
    }
}
