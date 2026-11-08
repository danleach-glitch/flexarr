package io.github.flexarr

import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import io.github.flexarr.audio.TrustedCallers
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Assume.assumeTrue
import org.junit.Test
import org.junit.runner.RunWith

/**
 * On a device (an emulator with Google Play will do): the Google apps there are signed with the certificates the app
 * trusts, and nothing else is.
 */
@RunWith(AndroidJUnit4::class)
class TrustedCallersDeviceTest {
    private val context = InstrumentationRegistry.getInstrumentation().targetContext

    private fun installed(name: String) = runCatching { context.packageManager.getPackageInfo(name, 0) }.isSuccess

    @Test
    fun googleAppsAreSignedByGoogle() {
        val google = listOf("com.google.android.googlequicksearchbox", "com.google.android.projection.gearhead", "com.google.android.gms").filter(::installed)
        assumeTrue("No Google apps on this device", google.isNotEmpty())
        for (name in google) assertTrue(name, TrustedCallers.signedByGoogle(context, name))
        assertTrue(TrustedCallers.isGenuine(context, google.first(), setOf(google.first())))
    }

    @Test
    fun otherAppsAreNot() {
        assertFalse(TrustedCallers.signedByGoogle(context, context.packageName))
        // This app, under a trusted name: neither built in nor signed by Google.
        assertFalse(TrustedCallers.isGenuine(context, context.packageName, setOf(context.packageName)))
        assertFalse(TrustedCallers.isGenuine(context, "com.google.android.not.installed", setOf("com.google.android.not.installed")))
        // System UI is built in.
        assertTrue(TrustedCallers.isGenuine(context, "com.android.systemui", setOf("com.android.systemui")))
    }
}
