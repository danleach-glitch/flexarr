package io.github.flexarr.ui.detail

import io.github.flexarr.core.ItemDetail
import io.github.flexarr.fixture
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.launch
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/** A show page asks for each season once at a time, however many parts of it want the season. */
class SeasonFetchesTest {
    private val template = fixture<ItemDetail>("item-movie")

    @Test
    fun `choosing the season the page is already fetching waits for that answer`() = runTest {
        val asked = mutableListOf<String>()
        val answer = CompletableDeferred<Unit>()
        val fetches = SeasonFetches { id ->
            asked += id
            answer.await()
            template.copy(item = template.item.copy(id = id))
        }
        val load = launch { fetches.refresh("s1") }
        val chosen = launch { fetches.ensure("s1") }
        testScheduler.runCurrent()
        answer.complete(Unit)
        load.join()
        chosen.join()
        assertEquals(listOf("s1"), asked)
        assertTrue("s1" in fetches.seasons)
        // Here already: choosing it again asks nothing; the page loading again does.
        fetches.ensure("s1")
        assertEquals(listOf("s1"), asked)
        fetches.refresh("s1")
        assertEquals(listOf("s1", "s1"), asked)
    }

    @Test
    fun `a season the load couldn't get is asked for again when chosen`() = runTest {
        var fail = true
        val asked = mutableListOf<String>()
        val fetches = SeasonFetches { id ->
            asked += id
            if (fail) error("offline")
            template.copy(item = template.item.copy(id = id))
        }
        fetches.refresh("s2")
        fail = false
        fetches.ensure("s2")
        assertEquals(listOf("s2", "s2"), asked)
        assertTrue("s2" in fetches.seasons)
    }
}
