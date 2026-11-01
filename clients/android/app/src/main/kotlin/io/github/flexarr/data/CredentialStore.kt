package io.github.flexarr.data

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import io.github.flexarr.core.FlexarrClient
import io.github.flexarr.core.Profile
import kotlinx.serialization.builtins.ListSerializer
import io.github.flexarr.core.ServerAddresses
import okhttp3.HttpUrl.Companion.toHttpUrlOrNull
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/**
 * A family guest's way in: the host's server on its tailnet, reached through the tunnel inside the app (see
 * [io.github.flexarr.guest.GuestTunnel]). As FlexarrKit's GuestTunnelInfo.
 */
data class GuestTunnelInfo(
    /** The server's tailnet address and Flexarr's port ("100.101.102.103:8383"). */
    val target: String,
    /** This device's name on the host's tailnet ("flexarr-guest-grans-phone"). */
    val hostname: String,
    /** The host's name, for "Connected by invite" and Leave. */
    val ownerName: String,
)

/** A saved server connection: the address, this device's token and who's watching. */
data class ServerCredentials(
    /** For a guest, the tunnel's loopback address (http://127.0.0.1:<port>/): never a home or LAN address. */
    val address: String,
    /** This device's own token (or, from an older sign-in, the server's API key). */
    val apiKey: String,
    /** Who's watching; null until someone chooses (the server then acts as the main profile). */
    val profileToken: String? = null,
    /** The server's address for away from home (over Tailscale), once it has remote access turned on. */
    val remoteAddress: String? = null,
    /**
     * Signed in as a family guest, through the tunnel. Such a sign-in never takes an address for away from home: the
     * server's could be a home address that a machine on the guest's own network also answers at.
     */
    val guest: GuestTunnelInfo? = null,
) {
    val isGuest: Boolean get() = guest != null

    fun client(onUnauthorized: (() -> Unit)? = null) = FlexarrClient(
        addresses = ServerAddresses.shared(address.toHttpUrlOrNull()!!, remoteAddress?.toHttpUrlOrNull()?.takeIf { guest == null }),
        apiKey = apiKey.ifEmpty { null },
        profileToken = profileToken,
        onUnauthorized = onUnauthorized,
    )
}

/**
 * Keeps the sign-in in the app's private preferences, with the token encrypted by a key in the Android Keystore
 * (which never leaves the device, so a backup or copied file is no use elsewhere).
 */
class CredentialStore internal constructor(context: Context, private val secrets: Secrets) {
    constructor(context: Context) : this(context, KeystoreSecrets)

    /** How the token is kept: by the Keystore's key, or (tests, where there's no Keystore) as it is. */
    internal interface Secrets {
        fun encrypt(text: String): String
        fun decrypt(stored: String): String
    }

    private val prefs = context.getSharedPreferences("server", Context.MODE_PRIVATE)

    fun load(): ServerCredentials? {
        val address = prefs.getString(ADDRESS, null) ?: return null
        // A token that can't be read back (the Keystore key was reset) leaves the address, so connecting again is quick.
        val key = prefs.getString(API_KEY, null)?.let { runCatching { secrets.decrypt(it) }.getOrNull() } ?: return null
        val guest = prefs.getString(GUEST_TARGET, null)?.let { target ->
            GuestTunnelInfo(target, prefs.getString(GUEST_HOSTNAME, null).orEmpty(), prefs.getString(GUEST_OWNER, null).orEmpty())
        }
        return ServerCredentials(
            address,
            key,
            prefs.getString(PROFILE, null),
            prefs.getString(REMOTE, null).takeIf { guest == null },
            guest,
        )
    }

    /** The last address used, kept after sign-out so the connect screen can prefill it (never a guest's loopback one). */
    val lastAddress: String? get() = prefs.getString(LAST_ADDRESS, null)

    fun save(credentials: ServerCredentials) {
        val guest = credentials.guest
        prefs.edit()
            .putString(ADDRESS, credentials.address)
            .also { if (guest == null) it.putString(LAST_ADDRESS, credentials.address) }
            .putString(API_KEY, secrets.encrypt(credentials.apiKey))
            .putString(PROFILE, credentials.profileToken)
            .putString(REMOTE, credentials.remoteAddress.takeIf { guest == null })
            .putString(GUEST_TARGET, guest?.target)
            .putString(GUEST_HOSTNAME, guest?.hostname)
            .putString(GUEST_OWNER, guest?.ownerName)
            .apply()
    }

    /** Keeps the server's address for away from home in step with what it says. Never for a guest's sign-in. */
    fun saveRemoteAddress(address: String?) {
        if (prefs.getString(GUEST_TARGET, null) != null) return
        prefs.edit().putString(REMOTE, address).apply()
    }

    /** The guest tunnel came up on another port than before: the sign-in's loopback address follows it. */
    fun saveGuestAddress(address: String) {
        if (prefs.getString(GUEST_TARGET, null) == null) return
        prefs.edit().putString(ADDRESS, address).apply()
    }

    /** Remembers who's watching. The token only works alongside the device's own, so it isn't encrypted. */
    fun saveProfile(token: String?) {
        prefs.edit().putString(PROFILE, token).apply()
    }

    /**
     * The profile list as the server last gave it (names, colours, which is watching, which are kids), so the app still
     * knows who's watching while it can't reach the server.
     */
    fun saveProfiles(profiles: List<Profile>) {
        prefs.edit().putString(PROFILES, FlexarrClient.json.encodeToString(ListSerializer(Profile.serializer()), profiles)).apply()
    }

    /** The profile list last saved for this sign-in; empty if there's none. */
    fun savedProfiles(): List<Profile> = prefs.getString(PROFILES, null)
        ?.let { runCatching { FlexarrClient.json.decodeFromString(ListSerializer(Profile.serializer()), it) }.getOrNull() }
        ?: emptyList()

    fun clear() {
        prefs.edit().remove(ADDRESS).remove(API_KEY).remove(PROFILE).remove(PROFILES).remove(REMOTE)
            .remove(GUEST_TARGET).remove(GUEST_HOSTNAME).remove(GUEST_OWNER).apply()
    }

    /** Encrypted as the token is, for other secrets kept beside it (the notification key). */
    internal fun encrypt(text: String): String = secrets.encrypt(text)

    internal fun decrypt(stored: String): String = secrets.decrypt(stored)

    private companion object {
        const val ADDRESS = "address"
        const val LAST_ADDRESS = "address.last"
        const val API_KEY = "apiKey"
        const val PROFILE = "profile"
        const val PROFILES = "profiles"
        const val REMOTE = "address.remote"
        const val GUEST_TARGET = "guest.target"
        const val GUEST_HOSTNAME = "guest.hostname"
        const val GUEST_OWNER = "guest.owner"
    }
}

/** The token encrypted by a key in the Android Keystore. */
private object KeystoreSecrets : CredentialStore.Secrets {
    private const val KEYSTORE = "AndroidKeyStore"
    private const val KEY_ALIAS = "flexarr.server"
    private const val TRANSFORMATION = "AES/GCM/NoPadding"
    private const val IV_LENGTH = 12

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
    override fun encrypt(text: String): String {
        val cipher = Cipher.getInstance(TRANSFORMATION).apply { init(Cipher.ENCRYPT_MODE, secretKey()) }
        return Base64.encodeToString(cipher.iv + cipher.doFinal(text.toByteArray()), Base64.NO_WRAP)
    }

    override fun decrypt(stored: String): String {
        val bytes = Base64.decode(stored, Base64.NO_WRAP)
        val cipher = Cipher.getInstance(TRANSFORMATION).apply {
            init(Cipher.DECRYPT_MODE, secretKey(), GCMParameterSpec(128, bytes, 0, IV_LENGTH))
        }
        return String(cipher.doFinal(bytes, IV_LENGTH, bytes.size - IV_LENGTH))
    }
}
