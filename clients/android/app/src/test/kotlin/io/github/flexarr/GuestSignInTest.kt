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
        store.saveRemoteAddress(io.github.flexarr.data.Away("https://mini.tail.ts.net:8443/"))
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
    fun `an unconfirmed away address is kept where older versions don't look`() {
        val away = "https://mini.tail.ts.net:8443/"
        val prefs = context.getSharedPreferences("server", android.content.Context.MODE_PRIVATE)
        store.save(ServerCredentials("http://192.168.1.2:8383/", "key", remoteAddress = away, serverId = "abc", remoteConfirmed = false))
        assertNull(prefs.getString("address.remote", null))
        assertEquals(io.github.flexarr.data.Away(away, confirmed = false), store.load()!!.away)

        store.saveRemoteAddress(io.github.flexarr.data.Away(away))
        assertNull(prefs.getString("address.remote.pending", null))
        assertEquals(io.github.flexarr.data.Away(away), store.load()!!.away)

        store.saveRemoteAddress(null)
        assertNull(store.load()!!.away)
    }

    @Test
    fun `the server's name is kept for the server it came from`() {
        val named = tunnel.copy(serverName = "Smith House")
        store.save(ServerCredentials("http://127.0.0.1:52000/", "guest-key", guest = named))
        assertEquals("Smith House", store.load()!!.guest!!.serverName)
        assertEquals("Smith House", store.load()!!.guest!!.serverTitle)

        store.saveServerName("Smith House")
        store.save(ServerCredentials("http://127.0.0.1:52000/", "guest-key", guest = named))
        assertEquals("Smith House", store.savedServerName)
        // Another server's sign-in doesn't inherit it.
        store.save(ServerCredentials("http://192.168.1.20:8383/", "key"))
        assertNull(store.savedServerName)

        store.saveServerName(" ")
        assertNull(store.savedServerName)
        store.saveServerName("Cabin")
        store.clear()
        assertNull(store.savedServerName)
    }

    @Test
    fun `a guest saved before names still reads, as Dan's Flexarr`() {
        store.save(ServerCredentials("http://127.0.0.1:52000/", "guest-key", guest = tunnel))
        assertNull(store.load()!!.guest!!.serverName)
        assertEquals("Dan’s Flexarr", store.load()!!.guest!!.serverTitle)
    }

    @Test
    fun `the guest's address follows the tunnel's port, and signing out forgets the tunnel`() {
        store.save(ServerCredentials("http://127.0.0.1:52000/", "guest-key", guest = tunnel))
        store.saveGuestAddress("http://127.0.0.1:53000/")
        assertEquals("http://127.0.0.1:53000/", store.load()!!.address)

        store.clear()
        store.save(ServerCredentials("http://192.168.1.50:8383/", "home-key"))
        val home = store.load()!!
        assertFalse(home.isGuest)
        // Not a guest: the address doesn't move.
        store.saveGuestAddress("http://127.0.0.1:53000/")
        assertEquals("http://192.168.1.50:8383/", store.load()!!.address)
        assertEquals("http://192.168.1.50:8383/", store.lastAddress)
    }

    @Test
    fun `a household device away from home uses the tunnel without being a guest`() {
        val away = ServerCredentials("http://127.0.0.1:52000/", "away-key", remoteAddress = "https://mini.tail.ts.net:8443/", guest = tunnel, household = true)
        assertTrue(away.usesTunnel)
        assertFalse(away.isGuest)
        // Still only through the tunnel.
        assertNull(away.client().addresses.remote)

        store.save(away)
        val loaded = store.load()!!
        assertTrue(loaded.household)
        assertTrue(loaded.usesTunnel)
        assertFalse(loaded.isGuest)
        assertNull(loaded.remoteAddress)
        assertNull(store.lastAddress)

        // The server's word is kept for next time: a guest after all, then a household device again.
        store.saveHousehold(false)
        assertTrue(store.load()!!.isGuest)
        store.saveHousehold(true)
        assertFalse(store.load()!!.isGuest)

        // Signing out forgets it, so the next tunnel sign-in starts as a guest's.
        store.clear()
        store.save(ServerCredentials("http://127.0.0.1:52000/", "guest-key", guest = tunnel))
        assertFalse(store.load()!!.household)
        assertTrue(store.load()!!.isGuest)
    }

    @Test
    fun `a household device that joined away from home has its profile chosen, so Who's watching doesn't follow`() {
        val away = ServerCredentials("http://127.0.0.1:52000/", "key", guest = tunnel, household = true)
        assertTrue(away.profileChosen)
        assertFalse(needsProfile(chosenHere = away.profileChosen, profiles = 3, serverAsks = false))
        // The server asking still asks.
        assertTrue(needsProfile(chosenHere = away.profileChosen, profiles = 3, serverAsks = true))
        // A home sign-in with nobody picked, and a guest, are as before.
        assertFalse(ServerCredentials("http://192.168.1.50:8383/", "key").profileChosen)
        assertFalse(ServerCredentials("http://127.0.0.1:52000/", "key", guest = tunnel).profileChosen)
        assertTrue(ServerCredentials("http://192.168.1.50:8383/", "key", profileToken = "p.t").profileChosen)
    }

    @Test
    fun `Use away from home is offered at home on someone's own profile, from servers with household devices`() {
        val home = ServerCredentials("http://192.168.1.50:8383/", "key")
        val claire = io.github.flexarr.core.Profile(id = "claire", name = "Claire", color = "#fff", isCurrent = true)
        val main = io.github.flexarr.core.Profile(id = "main", name = "Dan", color = "#fff", isMain = true, isCurrent = true)
        assertTrue(offersUseAwayFromHome(home, claire, "0.67.0", invitesBuilt = true))
        assertTrue(offersUseAwayFromHome(home, claire, "0.67.0-dev.1a2b3c4", invitesBuilt = true))
        // Not the main profile, not on the tunnel already, not before 0.67 or while the version isn't known, and not in
        // a build without family invites.
        assertFalse(offersUseAwayFromHome(home, main, "0.67.0", invitesBuilt = true))
        assertFalse(offersUseAwayFromHome(home.copy(guest = tunnel, household = true), claire, "0.67.0", invitesBuilt = true))
        assertFalse(offersUseAwayFromHome(home.copy(guest = tunnel), claire, "0.67.0", invitesBuilt = true))
        assertFalse(offersUseAwayFromHome(home, claire, "0.66.0", invitesBuilt = true))
        assertFalse(offersUseAwayFromHome(home, claire, null, invitesBuilt = true))
        assertFalse(offersUseAwayFromHome(home, null, "0.67.0", invitesBuilt = true))
        assertFalse(offersUseAwayFromHome(home, claire, "0.67.0", invitesBuilt = false))
    }

    @Test
    fun `Use away from home keeps the downloads unless it joined another server`() {
        assertTrue(sameServer("s1", "s1"))
        assertFalse(sameServer("s1", "s2"))
        // Not known on either side: nothing says it's another server.
        assertTrue(sameServer(null, "s1"))
        assertTrue(sameServer("s1", ""))
    }

    @Test
    fun `what the server says about a tunnel sign-in changes it only when it differs`() {
        val guest = ServerCredentials("http://127.0.0.1:52000/", "key", guest = tunnel)
        // The server says it's a household device: the sign-in in use becomes one, as saved.
        val household = guest.withServerSaid(guest = false)!!
        assertTrue(household.household)
        assertFalse(household.isGuest)
        assertEquals(guest.apiKey, household.apiKey)
        // Said again: nothing to change, nothing to save.
        assertNull(household.withServerSaid(guest = false))
        assertNull(guest.withServerSaid(guest = true))
        // A guest after all.
        assertTrue(household.withServerSaid(guest = true)!!.isGuest)
        // A home sign-in is never a household device away from home, whatever's said.
        assertNull(ServerCredentials("http://192.168.1.50:8383/", "key").withServerSaid(guest = false))
    }

    @Test
    fun `a guest saved before household devices stays a guest, and a home sign-in is neither`() {
        // As an older version saved it: no household key at all.
        store.save(ServerCredentials("http://127.0.0.1:52000/", "guest-key", guest = tunnel))
        context.getSharedPreferences("server", android.content.Context.MODE_PRIVATE).edit().remove("guest.household").commit()
        val guest = store.load()!!
        assertFalse(guest.household)
        assertTrue(guest.usesTunnel)
        assertTrue(guest.isGuest)

        store.clear()
        // Household only goes with the tunnel; at home it's dropped, and the server's word is never kept.
        store.save(ServerCredentials("http://192.168.1.50:8383/", "home-key", household = true))
        store.saveHousehold(true)
        val home = store.load()!!
        assertFalse(home.household)
        assertFalse(home.usesTunnel)
        assertFalse(home.isGuest)
    }

    @Test
    fun `through the tunnel the main profile isn't offered`() {
        val main = io.github.flexarr.core.Profile(id = "main", name = "Dan", color = "#fff", isMain = true)
        val claire = io.github.flexarr.core.Profile(id = "claire", name = "Claire", color = "#fff", isCurrent = true)
        val kids = io.github.flexarr.core.Profile(id = "kids", name = "Kids", color = "#fff", isKids = true)
        assertEquals(listOf(claire, kids), withoutMainProfile(listOf(main, claire, kids)))
        // One already watching stays, so who's watching still shows.
        val watching = main.copy(isCurrent = true)
        assertEquals(listOf(watching, kids), withoutMainProfile(listOf(watching, kids)))
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
