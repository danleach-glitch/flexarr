package io.github.flexarr

import io.github.flexarr.core.Profile
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** Who's watching once the server can't be reached: the profile last chosen, but never past a PIN or out of the kids app. */
class OfflineProfilesTest {
    private val dan = Profile(id = "dan", name = "Dan", color = "#a9c2ec", isMain = true, hasPin = true, asksPin = true)
    private val sam = Profile(id = "sam", name = "Sam", color = "#c2eca9")
    private val ada = Profile(id = "ada", name = "Ada", color = "#f0c0d0", isKids = true)

    private fun watching(id: String, vararg profiles: Profile) = profiles.map { it.copy(isCurrent = it.id == id) }

    private fun OfflineProfiles.current() = profiles.firstOrNull { it.isCurrent }?.id

    @Test
    fun `offline goes on as the profile chosen on this device, PIN or not`() {
        val settled = offlineProfiles(watching("dan", dan, sam), chosenHere = true, kidsLock = false)
        assertEquals("dan", settled.current())
        assertFalse(settled.asksWho)
    }

    @Test
    fun `left at Who's watching, a profile with a PIN isn't gone back to`() {
        // The list as kept still marks whoever was watching last; nobody has been chosen since.
        val settled = offlineProfiles(watching("dan", dan, sam), chosenHere = false, kidsLock = false)
        assertNull(settled.current())
        assertTrue(settled.asksWho)
        assertEquals(listOf("dan", "sam"), settled.profiles.map { it.id })
    }

    @Test
    fun `a server before 0_56 says it with hasPin alone`() {
        val older = dan.copy(asksPin = null)
        assertTrue(offlineProfiles(watching("dan", older, sam), chosenHere = false, kidsLock = false).asksWho)
        // And the main profile where choosing it takes the admin PIN, with none of its own.
        val admin = dan.copy(hasPin = false, asksPin = true, asksAdminPin = true)
        assertTrue(offlineProfiles(watching("dan", admin, sam), chosenHere = false, kidsLock = false).asksWho)
    }

    @Test
    fun `left at Who's watching by a profile with a PIN, a child's profile takes over if there is one`() {
        val settled = offlineProfiles(watching("dan", dan, sam, ada), chosenHere = false, kidsLock = false)
        assertEquals("ada", settled.current())
        assertFalse(settled.asksWho)
    }

    @Test
    fun `a child's own PIN isn't got past either`() {
        val locked = ada.copy(hasPin = true, asksPin = true)
        val settled = offlineProfiles(watching("ada", dan, locked), chosenHere = false, kidsLock = false)
        assertNull(settled.current())
        assertTrue(settled.asksWho)
    }

    @Test
    fun `left at Who's watching by a profile without a PIN, offline goes on as it`() {
        val settled = offlineProfiles(watching("sam", dan, sam), chosenHere = false, kidsLock = false)
        assertEquals("sam", settled.current())
        assertFalse(settled.asksWho)
    }

    @Test
    fun `the only profile is never asked for`() {
        // One profile: no profile is ever chosen, and the server acts as it.
        val settled = offlineProfiles(watching("dan", dan), chosenHere = false, kidsLock = false)
        assertEquals("dan", settled.current())
        assertFalse(settled.asksWho)
    }

    @Test
    fun `a device locked to kids profiles goes to a child, as before`() {
        assertEquals("ada", offlineProfiles(watching("dan", dan, sam, ada), chosenHere = true, kidsLock = true).current())
        assertEquals("ada", offlineProfiles(watching("ada", dan, sam, ada), chosenHere = true, kidsLock = true).current())
        // No child's profile to go to: as it was.
        val none = offlineProfiles(watching("sam", dan, sam), chosenHere = true, kidsLock = true)
        assertEquals("sam", none.current())
        assertFalse(none.asksWho)
    }

    @Test
    fun `with nobody known to be watching, a child if there is one, else nobody, as before`() {
        assertEquals("ada", offlineProfiles(listOf(dan, sam, ada), chosenHere = false, kidsLock = false).current())
        val nobody = offlineProfiles(listOf(dan, sam), chosenHere = false, kidsLock = false)
        assertNull(nobody.current())
        assertFalse(nobody.asksWho)
        assertTrue(offlineProfiles(emptyList(), chosenHere = false, kidsLock = false).profiles.isEmpty())
    }
}
