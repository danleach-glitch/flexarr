package io.github.flexarr.data

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import io.github.flexarr.core.FlexarrClient
import okhttp3.HttpUrl.Companion.toHttpUrlOrNull
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/** A saved server connection: the address, this device's token and who's watching. */
data class ServerCredentials(
    val address: String,
    /** This device's own token (or, from an older sign-in, the server's API key). */
    val apiKey: String,
    /** Who's watching; null until someone chooses (the server then acts as the main profile). */
    val profileToken: String? = null,
) {
    fun client(onUnauthorized: (() -> Unit)? = null) = FlexarrClient(
        baseUrl = address.toHttpUrlOrNull()!!,
        apiKey = apiKey.ifEmpty { null },
        profileToken = profileToken,
        onUnauthorized = onUnauthorized,
    )
}

/**
 * Keeps the sign-in in the app's private preferences, with the token encrypted by a key in the Android Keystore
 * (which never leaves the device, so a backup or copied file is no use elsewhere).
 */
class CredentialStore(context: Context) {
    private val prefs = context.getSharedPreferences("server", Context.MODE_PRIVATE)

    fun load(): ServerCredentials? {
        val address = prefs.getString(ADDRESS, null) ?: return null
        // A token that can't be read back (the Keystore key was reset) leaves the address, so connecting again is quick.
        val key = prefs.getString(API_KEY, null)?.let { runCatching { decrypt(it) }.getOrNull() } ?: return null
        return ServerCredentials(address, key, prefs.getString(PROFILE, null))
    }

    /** The last address used, kept after sign-out so the connect screen can prefill it. */
    val lastAddress: String? get() = prefs.getString(LAST_ADDRESS, null)

    fun save(credentials: ServerCredentials) {
        prefs.edit()
            .putString(ADDRESS, credentials.address)
            .putString(LAST_ADDRESS, credentials.address)
            .putString(API_KEY, encrypt(credentials.apiKey))
            .putString(PROFILE, credentials.profileToken)
            .apply()
    }

    /** Remembers who's watching. The token only works alongside the device's own, so it isn't encrypted. */
    fun saveProfile(token: String?) {
        prefs.edit().putString(PROFILE, token).apply()
    }

    fun clear() {
        prefs.edit().remove(ADDRESS).remove(API_KEY).remove(PROFILE).apply()
    }

    private fun secretKey(): SecretKey {
        val keyStore = KeyStore.getInstance(KEYSTORE).apply { load(null) }
        (keyStore.getKey(KEY_ALIAS, null) as? SecretKey)?.let { return it }
        val generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, KEYSTORE)
        generator.init(
            KeyGenParameterSpec.Builder(KEY_ALIAS, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .build(),
        )
        return generator.generateKey()
    }

    /** The IV, then the ciphertext, as base64. */
    private fun encrypt(text: String): String {
        val cipher = Cipher.getInstance(TRANSFORMATION).apply { init(Cipher.ENCRYPT_MODE, secretKey()) }
        return Base64.encodeToString(cipher.iv + cipher.doFinal(text.toByteArray()), Base64.NO_WRAP)
    }

    private fun decrypt(stored: String): String {
        val bytes = Base64.decode(stored, Base64.NO_WRAP)
        val cipher = Cipher.getInstance(TRANSFORMATION).apply {
            init(Cipher.DECRYPT_MODE, secretKey(), GCMParameterSpec(128, bytes, 0, IV_LENGTH))
        }
        return String(cipher.doFinal(bytes, IV_LENGTH, bytes.size - IV_LENGTH))
    }

    private companion object {
        const val ADDRESS = "address"
        const val LAST_ADDRESS = "address.last"
        const val API_KEY = "apiKey"
        const val PROFILE = "profile"
        const val KEYSTORE = "AndroidKeyStore"
        const val KEY_ALIAS = "flexarr.server"
        const val TRANSFORMATION = "AES/GCM/NoPadding"
        const val IV_LENGTH = 12
    }
}
