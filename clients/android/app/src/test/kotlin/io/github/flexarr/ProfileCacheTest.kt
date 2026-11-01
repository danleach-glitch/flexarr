package io.github.flexarr

import android.app.Application
import io.github.flexarr.core.Profile
import io.github.flexarr.data.CredentialStore
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config

/** The profile list kept on the phone, so offline mode still knows who's watching (and whether it's a child). */
@RunWith(RobolectricTestRunner::class)
@Config(application = Application::class)
class ProfileCacheTest {
    private val store get() = CredentialStore(RuntimeEnvironment.getApplication(), object : CredentialStore.Secrets {
        override fun encrypt(text: String) = "plain:$text"
        override fun decrypt(stored: String) = stored.removePrefix("plain:")
    })

    @Test
    fun `the list is kept and forgotten on sign-out`() {
        assertTrue(store.savedProfiles().isEmpty())
        val list = listOf(
            Profile(id = "dan", name = "Dan", color = "#a9c2ec", isMain = true, isCurrent = true),
            Profile(id = "kid", name = "Ada", color = "#f0c0d0", isKids = true),
        )
        store.saveProfiles(list)
        assertEquals(list, store.savedProfiles())
        store.clear()
        assertTrue(store.savedProfiles().isEmpty())
    }
}
