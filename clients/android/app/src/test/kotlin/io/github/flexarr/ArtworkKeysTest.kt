package io.github.flexarr

import io.github.flexarr.data.ServerCredentials
import org.junit.Assert.assertEquals
import org.junit.Test

/** Artwork is cached by its address at home, so a poster seen away isn't fetched again back home (and the other way). */
class ArtworkKeysTest {
    private val home = "http://192.168.1.50:8383/"
    private val away = "http://100.70.1.2:8383/"

    @Test
    fun `a poster from the away address is keyed as home's`() {
        var signIn: ServerCredentials? = ServerCredentials(home, "key", remoteAddress = away)
        val homeForm = ArtworkHomeForm { signIn }
        assertEquals("${home}api/v1/items/a/poster?w=300", homeForm("${away}api/v1/items/a/poster?w=300"))
        assertEquals("${home}api/v1/items/a/poster?w=300", homeForm("${home}api/v1/items/a/poster?w=300"))
        assertEquals("https://example.org/x.jpg", homeForm("https://example.org/x.jpg"))
        // A new sign-in with no away address: nothing to swap.
        signIn = ServerCredentials(home, "key")
        assertEquals("${away}api/v1/items/a/poster", homeForm("${away}api/v1/items/a/poster"))
        signIn = null
        assertEquals("${away}x", homeForm("${away}x"))
    }
}
