package io.github.flexarr.core

import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.async
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.launch
import kotlinx.coroutines.test.StandardTestDispatcher
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertSame
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.IOException

/** One answer shared by everything that asks for the same thing around the same time (the server's status at launch). */
@OptIn(ExperimentalCoroutinesApi::class)
class SharedAnswerTest {
    private var clock = 1_000_000L
    private var asked = 0

    @Test
    fun `everyone asking while it's fetched gets the one answer`() = runTest {
        val shared = SharedAnswer<String, String>(CoroutineScope(SupervisorJob() + StandardTestDispatcher(testScheduler))) { clock }
        val gate = CompletableDeferred<String>()
        val answers = (1..4).map { async { shared.get("home", 10_000) { asked++; gate.await() } } }
        runCurrent()
        assertEquals(1, asked)

        gate.complete("0.62.1")
        assertEquals(List(4) { "0.62.1" }, answers.map { it.await() })
        assertEquals(1, asked)
    }

    @Test
    fun `an answer is given again while it's fresh, and asked for again after`() = runTest {
        val shared = SharedAnswer<String, Int>(CoroutineScope(SupervisorJob() + StandardTestDispatcher(testScheduler))) { clock }
        assertEquals(1, shared.get("home", 10_000) { ++asked })
        clock += 10_000
        assertEquals(1, shared.get("home", 10_000) { ++asked })
        // Someone content with an older answer still gets it; someone who isn't asks, and that's the one kept.
        clock += 1
        assertEquals(1, shared.get("home", 300_000) { ++asked })
        assertEquals(2, shared.get("home", 10_000) { ++asked })
        assertEquals(2, shared.get("home", 300_000) { ++asked })
    }

    @Test
    fun `another sign-in asks afresh`() = runTest {
        val shared = SharedAnswer<String, String>(CoroutineScope(SupervisorJob() + StandardTestDispatcher(testScheduler))) { clock }
        assertEquals("home 1", shared.get("home", 10_000) { "home ${++asked}" })
        assertEquals("away 2", shared.get("away", 10_000) { "away ${++asked}" })
        // Only the last is kept.
        assertEquals("home 3", shared.get("home", 10_000) { "home ${++asked}" })
    }

    @Test
    fun `a fetch that fails reaches everyone waiting and isn't kept`() = runTest {
        val shared = SharedAnswer<String, String>(CoroutineScope(SupervisorJob() + StandardTestDispatcher(testScheduler))) { clock }
        val gate = CompletableDeferred<String>()
        val waiting = (1..2).map { async { runCatching { shared.get("home", 10_000) { asked++; gate.await() } } } }
        runCurrent()
        gate.completeExceptionally(IOException("unreachable"))
        waiting.forEach { assertTrue(it.await().exceptionOrNull() is IOException) }
        assertEquals(1, asked)

        assertEquals("0.62.1", shared.get("home", 10_000) { asked++; "0.62.1" })
        assertEquals(2, asked)
    }

    @Test
    fun `asking outright always asks, and shares what it gets`() = runTest {
        val shared = SharedAnswer<String, Int>(CoroutineScope(SupervisorJob() + StandardTestDispatcher(testScheduler))) { clock }
        assertEquals(1, shared.get("home", 10_000) { ++asked })
        // The profiles' load asks who's signed in afresh each time; the main screen opening just after takes its answer.
        val gate = CompletableDeferred<Unit>()
        val load = async { shared.ask("home") { gate.await(); ++asked } }
        val screen = async { shared.get("home", 5_000) { ++asked } }
        runCurrent()
        gate.complete(Unit)
        assertEquals(2, load.await())
        assertEquals(2, screen.await())
        assertEquals(2, shared.get("home", 5_000) { ++asked })
    }

    @Test
    fun `forgotten, the next asks`() = runTest {
        val shared = SharedAnswer<String, Int>(CoroutineScope(SupervisorJob() + StandardTestDispatcher(testScheduler))) { clock }
        assertEquals(1, shared.get("home", 10_000) { ++asked })
        shared.forget()
        assertEquals(2, shared.get("home", 10_000) { ++asked })
    }

    @Test
    fun `whoever asked first going away doesn't lose the answer for the rest`() = runTest {
        val shared = SharedAnswer<String, String>(CoroutineScope(SupervisorJob() + StandardTestDispatcher(testScheduler))) { clock }
        val gate = CompletableDeferred<String>()
        val first = launch { shared.get("home", 10_000) { asked++; gate.await() } }
        val second = async { shared.get("home", 10_000) { asked++; gate.await() } }
        runCurrent()
        first.cancelAndJoin()

        val answer = "0.62.1"
        gate.complete(answer)
        assertSame(answer, second.await())
        assertEquals(1, asked)
    }
}
