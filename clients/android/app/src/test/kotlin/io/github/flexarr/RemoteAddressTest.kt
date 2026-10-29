package io.github.flexarr

import io.github.flexarr.core.FlexarrException
import io.github.flexarr.core.SystemStatus
import kotlinx.coroutines.test.runTest
import okhttp3.HttpUrl
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.net.ConnectException

/** Which address for away from home the app keeps, so its sign-in only ever goes to its own server. */
class RemoteAddressTest {
    private val home = "http://192.168.1.106:8383/"
    private val tailscale = "https://mini.tail1234.ts.net:8443/"

    private fun status(remoteUrl: String?, serverId: String? = "home-id") =
        SystemStatus(appName = "Flexarr", version = "0.55.0", remoteUrl = remoteUrl, serverId = serverId)

    /** Where the app asked, without the sign-in, which server is there. */
    private val asked = mutableListOf<String>()
    private val refusals = mutableListOf<String>()

    private suspend fun remote(status: SystemStatus, kept: String? = null, idThere: () -> String?) =
        AppModel.remoteAddress(
            status,
            home,
            kept = kept,
            idAt = { url: HttpUrl -> asked += url.toString(); idThere() },
            refused = { refusals += it },
        )

    private fun unreachable(): Nothing = throw FlexarrException.Unreachable(ConnectException("no route"))

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
        // Signing in: nothing yet.
        assertNull(remote(status(tailscale)) { unreachable() })
        // The server's address has changed since: the old one stays rather than an unchecked new one.
        val old = "https://old.tail1234.ts.net/"
        assertEquals(old, remote(status(tailscale), kept = old) { unreachable() })
        assertTrue(refusals.isEmpty())
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
