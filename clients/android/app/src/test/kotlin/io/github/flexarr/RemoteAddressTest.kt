package io.github.flexarr

import io.github.flexarr.core.FlexarrException
import io.github.flexarr.core.SystemStatus
import io.github.flexarr.data.Away
import kotlinx.coroutines.test.runTest
import okhttp3.HttpUrl
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.net.ConnectException

/** Which address for away from home the app keeps, so its sign-in only ever goes to its own server. */
class RemoteAddressTest {
    private val home = "http://192.168.1.50:8383/"
    private val tailscale = "https://mini.tail1234.ts.net:8443/"

    private fun status(remoteUrl: String?, serverId: String? = "home-id", off: Boolean? = null) =
        SystemStatus(appName = "Flexarr", version = "0.55.0", remoteUrl = remoteUrl, serverId = serverId, remoteAccessOff = off)

    /** Where the app asked, without the sign-in, which server is there. */
    private val asked = mutableListOf<String>()
    private val refusals = mutableListOf<String>()

    private suspend fun remote(status: SystemStatus, kept: String? = null, idThere: () -> String?) =
        away(status, kept?.let { Away(it) }, idThere = idThere)?.url

    private suspend fun away(status: SystemStatus, kept: Away? = null, knownId: String? = null, idThere: () -> String?) =
        AppModel.remoteAddress(
            status,
            home,
            kept = kept,
            knownId = knownId,
            idAt = { url: HttpUrl -> asked += url.toString(); idThere() },
            refused = { refusals += it },
        )

    private fun unreachable(): Nothing = throw FlexarrException.Unreachable(ConnectException("no route"))

    /** The server's Mac has just restarted and its Tailscale isn't up yet: no address, but not off. */
    @Test
    fun `no address while Tailscale starts keeps the one kept`() = runTest {
        assertEquals(tailscale, remote(status(null, off = false), kept = tailscale) { error("not asked") })
        // A server before 0.67 doesn't say: kept too.
        assertEquals(tailscale, remote(status(null), kept = tailscale) { error("not asked") })
        assertEquals(Away(tailscale, confirmed = false), away(status(null, off = false), Away(tailscale, confirmed = false)) { error("not asked") })
        // Turned off: forgotten.
        assertNull(remote(status(null, off = true), kept = tailscale) { error("not asked") })
        assertTrue(asked.isEmpty())
    }

    @Test
    fun `the same server there is kept`() = runTest {
        assertEquals(tailscale, remote(status("https://mini.tail1234.ts.net:8443")) { "home-id" })
        assertEquals(listOf(tailscale), asked)
        assertTrue(refusals.isEmpty())
    }

    @Test
    fun `a different server there, or one that gives no id, isn't`() = runTest {
        assertNull(remote(status(tailscale)) { "someone-else" })
        assertNull(remote(status(tailscale)) { null })
        assertEquals(2, refusals.size)
    }

    @Test
    fun `while nothing answers there, what was kept stays until next time`() = runTest {
        // The server's address has changed since: the old one, confirmed, stays rather than an unchecked new one.
        val old = "https://old.tail1234.ts.net/"
        assertEquals(old, remote(status(tailscale), kept = old) { unreachable() })
        assertTrue(refusals.isEmpty())
    }

    @Test
    fun `signing in with Tailscale off keeps the address unconfirmed`() = runTest {
        // Signing in at home with Tailscale off: kept, but to be asked before it's sent the sign-in.
        assertEquals(Away(tailscale, confirmed = false), away(status(tailscale)) { unreachable() })
        // Next time, still nothing there: still unconfirmed, and asked again.
        val unconfirmed = Away(tailscale, confirmed = false)
        assertEquals(unconfirmed, away(status(tailscale), kept = unconfirmed, knownId = "home-id") { unreachable() })
        // Tailscale on: confirmed.
        assertEquals(Away(tailscale), away(status(tailscale), kept = unconfirmed, knownId = "home-id") { "home-id" })
        assertEquals(listOf(tailscale, tailscale, tailscale), asked)
        // Or someone else answers there: dropped.
        assertNull(away(status(tailscale), kept = unconfirmed, knownId = "home-id") { "their-id" })
        assertEquals(1, refusals.size)
    }

    @Test
    fun `a new address has to give the id kept at sign-in, not one the same answer brings`() = runTest {
        val theirs = "https://theirs.tail9999.ts.net/"
        assertNull(away(status(theirs, serverId = "their-id"), knownId = "home-id") { "their-id" })
        assertNull(away(status(theirs, serverId = null), knownId = "home-id") { "their-id" })
        assertEquals(Away(tailscale), away(status(tailscale, serverId = null), knownId = "home-id") { "home-id" })
    }

    @Test
    fun `an address already kept isn't asked again`() = runTest {
        assertEquals(tailscale, remote(status(tailscale), kept = tailscale) { error("not asked") })
        assertTrue(asked.isEmpty())
    }

    @Test
    fun `a server before 0_55 has no id, so its Tailscale address is taken without asking`() = runTest {
        assertEquals(tailscale, remote(status(tailscale, serverId = null)) { error("not asked") })
        assertEquals("http://100.101.2.3:8383/", remote(status("100.101.2.3", serverId = null)) { error("not asked") })
        assertTrue(asked.isEmpty())
    }

    @Test
    fun `only a Tailscale address is ever asked, whatever the server's id`() = runTest {
        for (url in listOf("https://flexarr.example.com", "http://mini.tail1234.ts.net:8443", "http://192.168.1.20:8383")) {
            assertNull(url, remote(status(url)) { "home-id" })
            assertNull(url, remote(status(url, serverId = null)) { null })
        }
        assertTrue(asked.isEmpty())
        assertEquals(6, refusals.size)
    }

    @Test
    fun `none, or the address already in use, is none`() = runTest {
        assertNull(remote(status(null)) { error("not asked") })
        assertNull(remote(status("not an address")) { error("not asked") })
        assertNull(AppModel.remoteAddress(status("100.101.2.3"), "http://100.101.2.3:8383/", idAt = { error("not asked") }, refused = { refusals += it }))
        assertTrue(asked.isEmpty())
        assertTrue(refusals.isEmpty())
    }
}
