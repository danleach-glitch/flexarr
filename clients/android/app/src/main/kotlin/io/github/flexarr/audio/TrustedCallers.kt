package io.github.flexarr.audio

import android.annotation.SuppressLint
import android.content.Context
import android.content.pm.ApplicationInfo
import android.content.pm.PackageManager
import android.os.Build
import java.security.MessageDigest

/**
 * Whether an app that names itself as Android Auto, the Assistant or the like really is it. A name alone isn't enough:
 * on a device without that app (no Google apps, or no car Assistant), any app installed under its name would pass. The
 * real one is built into the device, or signed by Google (Android Auto from the Play Store on Android 9 and older, say).
 * An app installed under a name it isn't entitled to is neither.
 */
internal object TrustedCallers {
    /**
     * Google's signing certificates (SHA-256) for these apps, as each has had them over time: the one Google's own apps
     * (the Google app, Play services) started with and Play services' since, and Android Auto's two.
     */
    val GOOGLE_CERTIFICATES = setOf(
        "f0fd6c5b410f25cb25c3b53346c8972fae30f8ee7411df910480ad6b2d60db83",
        "7ce83c1b71f3d572fed04c8d40c5cb10ff75e6d87d9df6fbd53f0468c2905053",
        "5f2391277b1dbd489000467e4c2fa6af802430080457dce2f618992e9dfb5402",
        "fdb00c43dbde8b51cb312aa81d3b5fa17713adb94b28f598d77f8eb89daceedf",
        "1ca8dcc0bed3cbd872d2cb791200c0292ca9975768a82d676b8b424fb65b5295",
    )

    /** [packageName] is one of [names], and is built into the device or signed by Google. */
    fun isGenuine(packageName: String?, names: Set<String>, isSystem: (String) -> Boolean, signedByGoogle: (String) -> Boolean): Boolean =
        packageName != null && packageName in names && (isSystem(packageName) || signedByGoogle(packageName))

    fun isGenuine(context: Context, packageName: String?, names: Set<String>): Boolean {
        val packages = context.packageManager
        return isGenuine(
            packageName,
            names,
            isSystem = { name ->
                runCatching { packages.getApplicationInfo(name, 0).flags and (ApplicationInfo.FLAG_SYSTEM or ApplicationInfo.FLAG_UPDATED_SYSTEM_APP) != 0 }
                    .getOrDefault(false)
            },
            signedByGoogle = { name -> signedByGoogle(context, name) },
        )
    }

    /** [name] is installed and signed (now or before its key rotated) with one of [GOOGLE_CERTIFICATES]. */
    fun signedByGoogle(context: Context, name: String): Boolean = runCatching { signedByGoogle(context.packageManager, name) }.getOrDefault(false)

    @SuppressLint("PackageManagerGetSignatures") // Only before Android 9, which has no key rotation to miss.
    @Suppress("DEPRECATION")
    private fun signedByGoogle(packages: PackageManager, name: String): Boolean =
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
            // Takes in the certificates the app was signed with before, when its key has rotated.
            GOOGLE_CERTIFICATES.any { packages.hasSigningCertificate(name, hex(it), PackageManager.CERT_INPUT_SHA256) }
        } else {
            packages.getPackageInfo(name, PackageManager.GET_SIGNATURES).signatures.orEmpty().any { sha256(it.toByteArray()) in GOOGLE_CERTIFICATES }
        }

    fun sha256(bytes: ByteArray): String = MessageDigest.getInstance("SHA-256").digest(bytes).joinToString("") { "%02x".format(it) }

    private fun hex(text: String): ByteArray = ByteArray(text.length / 2) { text.substring(it * 2, it * 2 + 2).toInt(16).toByte() }
}
