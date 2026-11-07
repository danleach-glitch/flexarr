package io.github.flexarr.audio

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/** Android Auto, the Assistant and System UI are trusted by what they are, not just the name they give. */
class TrustedCallersTest {
    private val names = setOf("com.google.android.projection.gearhead", "com.google.android.carassistant")

    @Test
    fun `the built-in app or one signed by Google passes`() {
        assertTrue(TrustedCallers.isGenuine("com.google.android.carassistant", names, isSystem = { true }, signedByGoogle = { false }))
        // Android Auto from the Play Store, on Android 9 and older.
        assertTrue(TrustedCallers.isGenuine("com.google.android.projection.gearhead", names, isSystem = { false }, signedByGoogle = { true }))
    }

    @Test
    fun `an app installed under a trusted name it isn't entitled to is turned away`() {
        // A phone without the car Assistant: a sideloaded app took its name.
        assertFalse(TrustedCallers.isGenuine("com.google.android.carassistant", names, isSystem = { false }, signedByGoogle = { false }))
    }

    @Test
    fun `a name not on the list is turned away, however it's signed`() {
        assertFalse(TrustedCallers.isGenuine("com.example.player", names, isSystem = { true }, signedByGoogle = { true }))
        assertFalse(TrustedCallers.isGenuine(null, names, isSystem = { true }, signedByGoogle = { true }))
    }

    @Test
    fun `Google's certificates are written as SHA-256 in lower-case hex`() {
        assertTrue(TrustedCallers.GOOGLE_CERTIFICATES.all { it.matches(Regex("[0-9a-f]{64}")) })
        assertTrue(TrustedCallers.sha256(byteArrayOf()) == "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")
    }
}
