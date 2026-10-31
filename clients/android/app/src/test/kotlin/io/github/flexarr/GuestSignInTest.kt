package io.github.flexarr

import android.app.Application
import android.content.Intent
import android.net.Uri
import io.github.flexarr.data.CredentialStore
import io.github.flexarr.data.GuestTunnelInfo
import io.github.flexarr.data.ServerCredentials
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config

/** A family guest's sign-in, as it's kept, and the invite links that start one. */
@RunWith(RobolectricTestRunner::class)
@Config(application = Application::class)
class GuestSignInTest {
    private val context get() = RuntimeEnvironment.getApplication()

    /** Robolectric has no Keystore: the token is kept as it is. */
    private val store get() = CredentialStore(context, object : CredentialStore.Secrets {
        override fun encrypt(text: String) = "plain:$text"
        override fun decrypt(stored: String) = stored.removePrefix("plain:")
    })

    private val tunnel = GuestTunnelInfo(target = "100.101.102.103:8383", hostname = "flexarr-guest-gran", ownerName = "Dan")

    @After
    fun clear() = store.clear()

    @Test
    fun `a guest sign-in keeps its tunnel and never an address for away from home`() {
        val credentials = ServerCredentials("http://127.0.0.1:52000/", "guest-key", remoteAddress = "https://mini.tail.ts.net:8443/", guest = tunnel)
        // Even handed one, its client only ever goes through the tunnel.
        assertNull(credentials.client().addresses.remote)

        store.save(credentials)
        store.saveRemoteAddress("https://mini.tail.ts.net:8443/")
        val loaded = store.load()!!

        assertEquals(tunnel, loaded.guest)
        assertTrue(loaded.isGuest)
        assertNull(loaded.remoteAddress)
        assertEquals("guest-key", loaded.apiKey)
        assertEquals("http://127.0.0.1:52000/", loaded.address)
        // The loopback address isn't one to offer on the connect screen later.
        assertNull(store.lastAddress)
    }

    @Test
    fun `the guest's address follows the tunnel's port, and signing out forgets the tunnel`() {
        store.save(ServerCredentials("http://127.0.0.1:52000/", "guest-key", guest = tunnel))
        store.saveGuestAddress("http://127.0.0.1:53000/")
        assertEquals("http://127.0.0.1:53000/", store.load()!!.address)

        store.clear()
        store.save(ServerCredentials("http://192.168.1.106:8383/", "home-key"))
        val home = store.load()!!
        assertFalse(home.isGuest)
        // Not a guest: the address doesn't move.
        store.saveGuestAddress("http://127.0.0.1:53000/")
        assertEquals("http://192.168.1.106:8383/", store.load()!!.address)
        assertEquals("http://192.168.1.106:8383/", store.lastAddress)
    }

    @Test
    fun `invite links open from flexarr io and the app's own scheme, from any app`() {
        val payload = "eyJ2IjoxfQ"
        assertEquals(AppLink.Join(payload), AppLink.parse(Uri.parse("https://flexarr.io/join#$payload")))
        assertEquals(AppLink.Join(payload), AppLink.parse(Uri.parse("flexarr://join#$payload")))
        assertEquals(AppLink.Join(payload), AppLink.parse(AppLink.Join(payload).uri))
        // Another app's link is still an invite: it's asked about before anything happens.
        assertEquals(AppLink.Join(payload), AppLink.received(context, Intent(Intent.ACTION_VIEW, Uri.parse("https://flexarr.io/join#$payload"))))
        assertNull(AppLink.parse(Uri.parse("https://flexarr.io/join")))
        assertNull(AppLink.parse(Uri.parse("https://example.com/join#$payload")))
        assertNull(AppLink.parse(Uri.parse("https://flexarr.io/apps#$payload")))
    }
}
