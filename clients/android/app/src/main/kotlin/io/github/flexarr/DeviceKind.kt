package io.github.flexarr

import android.content.Context
import androidx.compose.runtime.Composable
import androidx.compose.runtime.ReadOnlyComposable
import androidx.compose.ui.platform.LocalConfiguration

/**
 * What this device is, for the words that name it: "this phone", "this tablet" or "this TV", each its own whole
 * sentence in the lookup (docs/languages.md), never a word dropped into one. A TV by its UI mode, a tablet by its
 * narrowest side (600 dp and over, as Android's own layouts count it), so a phone turned sideways stays a phone.
 */
enum class DeviceKind {
    Phone, Tablet, Tv;

    companion object {
        /** The narrowest side, in dp, from which a device is a tablet. */
        const val TABLET_WIDTH = 600

        fun of(isTelevision: Boolean, smallestScreenWidthDp: Int): DeviceKind = when {
            isTelevision -> Tv
            smallestScreenWidthDp >= TABLET_WIDTH -> Tablet
            else -> Phone
        }

        fun of(context: Context): DeviceKind = of(context.isTelevision, context.resources.configuration.smallestScreenWidthDp)

        /**
         * This device, for words said away from a screen (the player's errors): set at launch and again when the
         * configuration changes (a foldable opening), by [FlexarrApplication].
         */
        @Volatile
        var current: DeviceKind = Phone
            internal set
    }
}

/** This phone or tablet is a tablet (the phone screens never run on a TV), so its words say "this tablet". */
val onTablet: Boolean
    @Composable @ReadOnlyComposable get() = LocalConfiguration.current.smallestScreenWidthDp >= DeviceKind.TABLET_WIDTH
