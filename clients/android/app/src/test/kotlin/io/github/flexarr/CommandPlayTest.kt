package io.github.flexarr

import io.github.flexarr.core.Profile
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/** A play command from another device: who it plays as here, and never as whoever was here last while nobody's chosen. */
class CommandPlayTest {
    private val dan = Profile(id = "dan", name = "Dan", color = "#a9c2ec", isMain = true, hasPin = true, asksPin = true)
    private val sam = Profile(id = "sam", name = "Sam", color = "#c2eca9")
    private val profiles = listOf(dan, sam)

    @Test
    fun `a command for the one watching, or naming nobody, plays as things are`() {
        assertEquals(CommandPlay.AsThingsAre to null, commandPlays("sam", "sam", profiles, awaitsProfile = false))
        assertEquals(CommandPlay.AsThingsAre to null, commandPlays(null, "sam", profiles, awaitsProfile = false))
        // A profile this device hasn't heard of.
        assertEquals(CommandPlay.AsThingsAre to null, commandPlays("gone", "sam", profiles, awaitsProfile = false))
    }

    @Test
    fun `a command for another profile switches to it, through Who's watching if it has a PIN`() {
        assertEquals(CommandPlay.Switch to sam, commandPlays("sam", "dan", profiles, awaitsProfile = false))
        assertEquals(CommandPlay.AskWho to dan, commandPlays("dan", "sam", profiles, awaitsProfile = false))
    }

    @Test
    fun `with Who's watching up, a command naming nobody waits for someone to be chosen`() {
        // "As things are" would be as whoever was here last, PIN or not.
        val (how, profile) = commandPlays(null, null, profiles, awaitsProfile = true)
        assertEquals(CommandPlay.OnceChosen, how)
        assertNull(profile)
        assertEquals(CommandPlay.OnceChosen, commandPlays("gone", null, profiles, awaitsProfile = true).first)
    }

    @Test
    fun `with Who's watching up, a command for a profile still goes to it`() {
        assertEquals(CommandPlay.Switch to sam, commandPlays("sam", null, profiles, awaitsProfile = true))
        assertEquals(CommandPlay.AskWho to dan, commandPlays("dan", null, profiles, awaitsProfile = true))
    }

    @Test
    fun `with Who's watching up, whoever the server's list still marks isn't taken as watching`() {
        // The app opened at "Who's watching?": the list marks whoever was here last, though nobody's been chosen. A
        // command naming them chooses them like anyone else (their PIN asked for), rather than wait for a press here.
        assertEquals(CommandPlay.Switch to sam, commandPlays("sam", "sam", profiles, awaitsProfile = true))
        assertEquals(CommandPlay.AskWho to dan, commandPlays("dan", "dan", profiles, awaitsProfile = true))
        // And one naming nobody still waits, never playing as the one marked.
        assertEquals(CommandPlay.OnceChosen to null, commandPlays(null, "dan", profiles, awaitsProfile = true))
    }
}
