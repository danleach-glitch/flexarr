package io.github.flexarr.core

import kotlinx.serialization.serializer
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** The same checks as FlexarrKit's ScoresTests: IMDb and Rotten Tomatoes from new and older servers. */
class ScoresTest {
    private inline fun <reified T> fixture(name: String): T {
        val text = javaClass.classLoader!!.getResource("$name.json")!!.readText()
        return FlexarrClient.json.decodeFromString(serializer<T>(), text)
    }

    private val movie get() = fixture<ItemDetail>("item-movie").item

    @Test
    fun aNewServersScores() {
        val scores = movie.copy(imdbRating = 8.1, rottenTomatoes = RottenTomatoes(96, RottenTomatoesState.CertifiedFresh)).scores
        assertEquals("8.1", scores.imdbText)
        assertEquals(RottenTomatoes(96, RottenTomatoesState.CertifiedFresh), scores.rottenTomatoes)
        assertEquals("Rotten Tomatoes 96%, Certified Fresh", scores.rottenTomatoesLabel)
        assertEquals("IMDb 8.1 out of 10", scores.imdbLabel)
    }

    @Test
    fun anOlderServersCriticsScore_GetsATomatoFromTheNumber() {
        val item = movie // the fixture has criticRating 96 and no rottenTomatoes
        assertEquals(RottenTomatoes(96, RottenTomatoesState.Fresh), item.scores.rottenTomatoes)
        assertNull(item.scores.imdb)
        assertFalse("7.4" in item.facts()) // the plain score gives way to the tomato
    }

    @Test
    fun anUnknownTomato_FollowsTheScore() {
        val rt = FlexarrClient.json.decodeFromString(RottenTomatoes.serializer(), """{"score": 41, "state": "SomethingNew"}""")
        assertEquals(RottenTomatoesState.Unknown, rt.state)
        assertEquals(RottenTomatoesState.Rotten, Scores.of(null, rt).rottenTomatoes?.state)
        assertEquals(RottenTomatoesState.Fresh, Scores.of(null, RottenTomatoes(60)).rottenTomatoes?.state)
    }

    @Test
    fun noScores_KeepsThePlainOne() {
        val item = movie.copy(criticRating = null)
        assertTrue(item.scores.isEmpty)
        assertTrue("7.4" in item.facts())
        assertFalse("7.4" in item.facts(scores = false))
    }

    @Test
    fun discoverHasTheSameScores() {
        val title = fixture<ExternalDetail>("requests-title").copy(rottenTomatoesState = RottenTomatoesState.CertifiedFresh)
        assertEquals(RottenTomatoes(title.criticScore!!, RottenTomatoesState.CertifiedFresh), title.scores.rottenTomatoes)
        assertEquals(title.imdbRating, title.scores.imdb)
    }
}
