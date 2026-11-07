package io.github.flexarr

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class SignOutRefusalTest {
    @Test
    fun `a device with child lock on keeps its sign-in and says the lock must go first`() {
        // The server answers 409 kidsLock and keeps the sign-in; forgetting it here would leave the device disconnected.
        assertEquals("Child lock is on for this device. Turn it off first.", signOutRefusal(kidsLock = true))
        assertNull(signOutRefusal(kidsLock = false))
    }
}
