package io.github.flexarr.core

import kotlinx.coroutines.delay
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.test.advanceTimeBy
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Test

/** A download's speed taps: sent once they settle, the latest only, and never out of order. */
class SettledSenderTest {
    @Test
    fun `quick taps send only the last speed, once`() = runTest {
        var speed = 1.0
        val sent = mutableListOf<Double>()
        val sender = SettledSender<String>(backgroundScope, Mutex()) { sent += speed }
        repeat(6) {
            speed += 0.05
            sender.changed("book")
            advanceTimeBy(200)
        }
        advanceTimeBy(2_000)
        assertEquals(1, sent.size)
        assertEquals(1.3, sent.single(), 0.0001)
    }

    @Test
    fun `a slow send isn't overtaken by a newer one`() = runTest {
        var speed = 1.0
        val landed = mutableListOf<Double>()
        val sender = SettledSender<String>(backgroundScope, Mutex()) {
            val sending = speed
            delay(if (sending == 1.05) 5_000 else 10)
            landed += sending
        }
        speed = 1.05
        sender.changed("book")
        advanceTimeBy(1_500) // the first is on its way, slowly
        speed = 1.1
        sender.changed("book")
        advanceTimeBy(10_000)
        assertEquals(listOf(1.05, 1.1), landed)
    }

    @Test
    fun `catching up waits for a speed being sent`() = runTest {
        val lock = Mutex()
        val order = mutableListOf<String>()
        val sender = SettledSender<String>(backgroundScope, lock) {
            delay(3_000)
            order += "speed"
        }
        sender.changed("book")
        advanceTimeBy(1_500)
        lock.lock()
        order += "catch up"
        lock.unlock()
        advanceTimeBy(10_000)
        assertEquals(listOf("speed", "catch up"), order)
    }
}
