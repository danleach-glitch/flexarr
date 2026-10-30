package io.github.flexarr.core

import org.junit.Test
import org.junit.Assert.assertEquals

class AlphabetTest {
    @Test
    fun lettersIgnoreArticles() {
        assertEquals("M", alphabetLetter("The Matrix"))
        assertEquals("Q", alphabetLetter("A Quiet Place"))
        assertEquals("I", alphabetLetter("An Inconvenient Truth"))
        assertEquals("A", alphabetLetter("Alien"))
        assertEquals("T", alphabetLetter("Theory of Everything"))
    }

    @Test
    fun numbersSymbolsAndAccentsAreHash() {
        assertEquals("#", alphabetLetter("1917"))
        assertEquals("#", alphabetLetter("Élite"))
        assertEquals("#", alphabetLetter(""))
    }

    @Test
    fun emptyLettersBeforeTheLastLoaded() {
        // Loaded up to D, more to come: B and C are passed and empty, E onwards may still come, # is only known at the end.
        val empty = emptyAlphabetLetters(listOf("A", "A", "D"), complete = false)
        assertEquals(setOf("B", "C"), empty)
        val all = emptyAlphabetLetters(listOf("A", "D"), complete = true)
        assertEquals(alphabetLetters.toSet() - setOf("A", "D"), all)
    }
}
