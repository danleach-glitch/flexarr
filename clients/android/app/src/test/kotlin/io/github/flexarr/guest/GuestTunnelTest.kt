package io.github.flexarr.guest

import android.app.Application
import io.github.flexarr.core.FlexarrClient
import io.github.flexarr.data.GuestTunnelInfo
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertThrows
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config

/** The guest tunnel in a build without family invites, which leaves the Go library out (app/build.gradle.kts). */
@RunWith(RobolectricTestRunner::class)
@Config(application = Application::class)
class GuestTunnelTest {
    private val guest = GuestTunnelInfo(target = "100.101.102.103:8383", hostname = "flexarr-guest-gran", ownerName = "Dan")

    @After
    fun standAside() {
        FlexarrClient.loopbackTunnel = null
    }

    @Test
    fun `a build without family invites says the tunnel didn't start, and never loads its library`() {
        val tunnel = GuestTunnel(RuntimeEnvironment.getApplication(), available = false)
        tunnel.use(guest)

        // Loading the library where there is none (here, as in such a build) would be an UnsatisfiedLinkError, not this.
        val problem = assertThrows(TunnelException::class.java) { tunnel.start() }
        // Not a refused key: the sign-in stays, and the problem screen offers Try again, Leave and Downloads.
        assertFalse(problem.needsLogin)
        assertThrows(TunnelException::class.java) { tunnel.port() }
        assertThrows(TunnelException::class.java) { tunnel.restart(0) }
        assertEquals("stopped", tunnel.state())
        assertEquals("stopped", tunnel.status().state)
        // None of these touch the library either.
        tunnel.ensureRunning()
        tunnel.stop()
        tunnel.forget()
    }
}
