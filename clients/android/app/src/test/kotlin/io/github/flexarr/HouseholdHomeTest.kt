package io.github.flexarr

import android.app.Application
import android.content.Context
import io.github.flexarr.data.CredentialStore
import io.github.flexarr.data.GuestTunnelInfo
import io.github.flexarr.data.ServerCredentials
import okhttp3.HttpUrl.Companion.toHttpUrl
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

/** A household device's home address, as it's kept and paired with the tunnel (docs/remote-guests.md). */
@RunWith(RobolectricTestRunner::class)
@Config(application = Application::class)
class HouseholdHomeTest {
    private val context get() = RuntimeEnvironment.getApplication()
    private val prefs get() = context.getSharedPreferences("server", Context.MODE_PRIVATE)

    private val store get() = CredentialStore(context, object : CredentialStore.Secrets {
        override fun encrypt(text: String) = "plain:$text"
        override fun decrypt(stored: String) = stored.removePrefix("plain:")
    })

    private val tunnel = GuestTunnelInfo(target = "100.101.102.103:8383", hostname = "flexarr-guest-kitchen", ownerName = "Dan")
    private val loopback = "http://127.0.0.1:52001/"
    private val home = "http://192.168.1.20:8383/"

    private fun household() = ServerCredentials(loopback, "key", guest = tunnel, household = true)

    @After
    fun clear() = store.clear()

    @Test
    fun `a household sign-in keeps its home address and server id, in keys of their own`() {
        store.save(household().withHome(home, "abc").copy(lastAway = true))
        val loaded = store.load()!!

        assertEquals(home, loaded.homeAddress)
        assertEquals("abc", loaded.serverId)
        assertTrue(loaded.lastAway)
        assertTrue(loaded.pairsHome)
        assertEquals(loopback, loaded.address)
        // Versions before read these as an address at home to trust: never written for a tunnel sign-in.
        assertNull(prefs.getString("serverId", null))
        assertNull(prefs.getString("address.remote", null))
        assertNull(store.lastAddress)

        store.saveHome(null, "abc")
        assertNull(store.load()!!.homeAddress)
        assertFalse(store.load()!!.pairsHome)
        store.saveLastAway(false)
        assertFalse(store.load()!!.lastAway)
    }

    @Test
    fun `a guest never gets a home address`() {
        val guest = ServerCredentials(loopback, "key", guest = tunnel)
        assertNull(guest.withHome(home, "abc").homeAddress)
        assertFalse(guest.copy(homeAddress = home, serverId = "abc").pairsHome)

        store.save(guest.copy(homeAddress = home, serverId = "abc"))
        store.saveHome(home, "abc")
        val loaded = store.load()!!
        assertNull(loaded.homeAddress)
        assertNull(loaded.serverId)
        assertEquals(loopback.toHttpUrl(), loaded.client().addresses.home)
        assertNull(loaded.client().addresses.remote)

        // A household sign-in the server says is a guest's loses it too.
        val told = household().withHome(home, "abc").withServerSaid(guest = true)!!
        assertNull(told.homeAddress)
        assertFalse(told.pairsHome)
        store.save(household().withHome(home, "abc"))
        store.saveHousehold(false)
        assertNull(store.load()!!.homeAddress)
    }

    @Test
    fun `a household sign-in saved before home addresses loads as it was, on the tunnel`() {
        prefs.edit()
            .putString("address", loopback)
            .putString("apiKey", "plain:key")
            .putString("guest.target", tunnel.target)
            .putString("guest.hostname", tunnel.hostname)
            .putString("guest.owner", tunnel.ownerName)
            .putBoolean("guest.household", true)
            .apply()
        val loaded = store.load()!!

        assertTrue(loaded.isHouseholdAway)
        assertNull(loaded.homeAddress)
        assertFalse(loaded.pairsHome)
        assertFalse(loaded.lastAway)
        val addresses = loaded.client().addresses
        assertEquals(loopback.toHttpUrl(), addresses.home)
        assertNull(addresses.remote)
        assertFalse(addresses.awayIsTunnel)
    }

    @Test
    fun `household keys left behind for another server's tunnel are ignored`() {
        store.save(household().withHome(home, "old-server").copy(lastAway = true))
        // An older version signs out (leaving the household keys) and joins another server's tunnel.
        prefs.edit()
            .putString("guest.target", "100.64.0.9:8383")
            .putBoolean("guest.household", true)
            .apply()
        val loaded = store.load()!!

        assertTrue(loaded.isHouseholdAway)
        assertNull(loaded.homeAddress)
        assertNull(loaded.serverId)
        assertFalse(loaded.lastAway)
        assertFalse(loaded.pairsHome)

        // Told where home is now, it's kept for this tunnel.
        store.saveHome(home, "new-server")
        assertEquals("new-server", store.load()!!.serverId)
        assertTrue(store.load()!!.pairsHome)
    }

    @Test
    fun `with a home address, home is home and the tunnel is away, with the id home must prove`() {
        val addresses = household().withHome(home, "pair-test").client().addresses
        assertEquals(home.toHttpUrl(), addresses.home)
        assertEquals(loopback.toHttpUrl(), addresses.remote)
        assertTrue(addresses.awayIsTunnel)
        assertEquals("pair-test", addresses.serverId)
        // Not sent the sign-in until it has answered with that id on this network.
        assertFalse(addresses.homeTrustedNow)
        // Without the server's id there's nothing for home to prove: it stays on the tunnel.
        val noId = household().withHome(home, null)
        assertFalse(noId.pairsHome)
        assertNull(noId.client().addresses.remote)
    }

    @Test
    fun `a household device last away starts on the tunnel`() {
        val addresses = household().withHome(home, "start-away").copy(lastAway = true).client().addresses
        assertTrue(addresses.onTunnel)
        val atHome = household().withHome(home, "start-home").client().addresses
        assertFalse(atHome.isAway)
    }

    @Test
    fun `Watch Next's client only ever asks home`() {
        val client = household().withHome(home, "abc").homeClient()!!
        assertEquals(home.toHttpUrl(), client.addresses.home)
        assertNull(client.addresses.remote)
        assertEquals("abc", client.addresses.serverId)
        assertNull(ServerCredentials(loopback, "key", guest = tunnel).homeClient())
        assertNull(household().homeClient())
    }
}
