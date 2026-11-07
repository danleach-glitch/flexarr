package io.github.flexarr

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Whether nobody has been chosen on this device though someone has to be: what the widgets, the car, the Assistant and
 * commands from other devices go by, so none of them acts as whoever was watching last while "Who's watching?" is up.
 */
class AwaitsProfileTest {
    private fun awaits(chosenHere: Boolean, loaded: Boolean, needsProfile: Boolean = false, kept: Int = 0) =
        awaitsProfile(chosenHere, loaded, needsProfile) { kept }

    @Test
    fun `someone chosen on this device is never waited for`() {
        assertFalse(awaits(chosenHere = true, loaded = true, kept = 3))
        assertFalse(awaits(chosenHere = true, loaded = false, kept = 3))
    }

    @Test
    fun `with the profiles loaded, it's whether Who's watching is up`() {
        assertTrue(awaits(chosenHere = false, loaded = true, needsProfile = true))
        // One profile, or offline as the profile last chosen: the app is open as someone.
        assertFalse(awaits(chosenHere = false, loaded = true, needsProfile = false, kept = 3))
    }

    @Test
    fun `before they're loaded, it's whether the list kept on the device has a choice in it`() {
        // The app started in the background (a widget's hourly update, the headset's button) after being left at the picker.
        assertTrue(awaits(chosenHere = false, loaded = false, kept = 2))
        // A household with one profile never chooses, and is never asked to.
        assertFalse(awaits(chosenHere = false, loaded = false, kept = 1))
        assertFalse(awaits(chosenHere = false, loaded = false, kept = 0))
    }

    @Test
    fun `the kept list is only read when it's needed`() {
        val unread: () -> Int = { throw AssertionError("read") }
        assertFalse(awaitsProfile(chosenHere = true, loaded = false, needsProfile = false, keptProfiles = unread))
        assertTrue(awaitsProfile(chosenHere = false, loaded = true, needsProfile = true, keptProfiles = unread))
    }

    @Test
    fun `Who's watching is up for a choice, or when the server asks even with one profile`() {
        assertTrue(needsProfile(chosenHere = false, profiles = 2, serverAsks = false))
        assertFalse(needsProfile(chosenHere = true, profiles = 2, serverAsks = false))
        assertFalse(needsProfile(chosenHere = false, profiles = 1, serverAsks = false))
        // The main profile has a PIN of its own that wasn't given here (a newly paired device): asked, even alone.
        assertTrue(needsProfile(chosenHere = false, profiles = 1, serverAsks = true))
        assertTrue(needsProfile(chosenHere = true, profiles = 1, serverAsks = true))
        // Nobody to show: never an empty picker.
        assertFalse(needsProfile(chosenHere = false, profiles = 0, serverAsks = true))
    }
}
