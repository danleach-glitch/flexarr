package io.github.flexarr.core

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import okhttp3.HttpUrl
import okhttp3.HttpUrl.Companion.toHttpUrl
import okhttp3.Interceptor
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import java.io.IOException
import java.math.BigInteger
import java.net.ConnectException
import java.security.KeyFactory
import java.security.KeyPairGenerator
import java.security.SecureRandom
import java.security.interfaces.ECPrivateKey
import java.security.interfaces.ECPublicKey
import java.security.spec.ECGenParameterSpec
import java.security.spec.ECParameterSpec
import java.security.spec.ECPoint
import java.security.spec.ECPrivateKeySpec
import java.security.spec.ECPublicKeySpec
import java.text.Normalizer
import java.time.Instant
import java.util.Base64
import javax.crypto.Cipher
import javax.crypto.KeyAgreement
import javax.crypto.Mac
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

/**
 * A family invite: what a guest's app needs to join its host's tailnet (with Tailscale built in) and sign in to Flexarr
 * there, carried in the link `https://flexarr.io/join#<this, as base64url JSON>` (or `flexarr://join#…`), or sealed for a
 * TV that showed a code. See docs/remote-guests.md. As FlexarrKit's GuestInvite.
 */
@Serializable
data class GuestInvite(
    /** Format version (1). */
    val v: Int,
    /** The host's name, for "Join Dan's Flexarr?". */
    val n: String,
    /** The server's id. */
    val s: String,
    /** The server's tailnet address and Flexarr's port ("100.101.102.103:8383"). */
    val a: String,
    /** The one-use Tailscale auth key. */
    val k: String,
    /** The one-time Flexarr token, swapped for a sign-in ([FlexarrClient.redeemInvite]). */
    val t: String,
    /** What this device is called in the host's list. */
    val g: String,
    /** The profile the guest watches as. */
    val p: String,
    /** When it stops working. */
    @Serializable(with = InstantSerializer::class) val e: Instant? = null,
) {
    val ownerName: String get() = n.trim().ifEmpty { "Someone" }
    val profileName: String get() = p
    val deviceName: String get() = g
    val tailnetTarget: String get() = a
    val authKey: String get() = k
    val token: String get() = t
    val serverId: String get() = s

    /** The node's name on the host's tailnet: "flexarr-guest-grans-phone". */
    val hostname: String get() = hostname(g)

    fun isExpired(now: Instant = Instant.now()): Boolean = e?.let { !it.isAfter(now) } ?: false

    /** Version 1, a Tailscale key and token, and a tailnet address (100.64.0.0/10) with a port: never anywhere else. */
    internal val isWellFormed: Boolean
        get() {
            if (v != 1 || !k.startsWith("tskey-") || t.isEmpty() || t.length > 128) return false
            val colon = a.lastIndexOf(':').takeIf { it > 0 } ?: return false
            if (a.substring(colon + 1).toIntOrNull() !in 1..65535) return false
            return FlexarrClient.isTailscaleHost(a.substring(0, colon))
        }

    companion object {
        /** A link this app opens invites from: flexarr.io/join (as typed or pasted, with or without https) or flexarr://join. */
        private val linkPrefix = Regex("^((https?://)?(www\\.)?flexarr\\.io/join/?|flexarr://join/?)$")

        /**
         * The invite in [text]: a link (https://flexarr.io/join#…, flexarr://join#…), or the part after the # by itself
         * (what Paste invite finds). Null when it isn't a whole, well-formed invite.
         */
        fun parse(text: String): GuestInvite? {
            val trimmed = text.trim()
            val hash = trimmed.indexOf('#')
            val payload = if (hash >= 0) {
                if (!linkPrefix.matches(trimmed.substring(0, hash).lowercase())) return null
                trimmed.substring(hash + 1)
            } else {
                trimmed
            }
            return decode(payload)
        }

        /** The invite from its base64url JSON (what's after the # in a link, or what a TV opens). */
        fun decode(payload: String): GuestInvite? {
            if (payload.isEmpty() || payload.length >= 4096) return null
            val base64 = payload.trim().replace('+', '-').replace('/', '_').trimEnd('=')
            val json = runCatching { String(Base64.getUrlDecoder().decode(base64), Charsets.UTF_8) }.getOrNull() ?: return null
            val invite = runCatching { FlexarrClient.json.decodeFromString(serializer(), json) }.getOrNull() ?: return null
            return invite.takeIf { it.isWellFormed }
        }

        /**
         * A tailnet name for [deviceName]: lower case, accents dropped, spaces, dots and underscores as hyphens, anything
         * else left out, and short enough (40 in all) for every list it shows in. As FlexarrKit's.
         */
        fun hostname(deviceName: String): String {
            val folded = Normalizer.normalize(deviceName.lowercase(), Normalizer.Form.NFD).replace(Regex("\\p{M}+"), "")
            val slug = StringBuilder()
            for (character in folded) {
                if (character in 'a'..'z' || character in '0'..'9' || character == '-') {
                    slug.append(character)
                } else if ((character == ' ' || character == '_' || character == '.') && slug.lastOrNull() != '-') {
                    slug.append('-')
                }
            }
            val trimmed = slug.toString().trim('-').take(26)
            return if (trimmed.isEmpty()) "flexarr-guest" else "flexarr-guest-$trimmed"
        }
    }
}

/** What redeeming an invite gives this device: its own sign-in, locked to the invite's profile. */
@Serializable
data class RedeemedInvite(val apiKey: String, val profileId: String, val serverId: String = "", val name: String = "")

@Serializable
internal data class RedeemInviteRequest(val token: String)

/**
 * Swaps an invite's one-time token for this device's sign-in. Sent through the tunnel (the server only accepts it from
 * a guest device on its tailnet), with no sign-in of its own. 403: not from a guest device; 410: used, cancelled or
 * expired; 503: the host's invites are paused.
 */
suspend fun FlexarrClient.redeemInvite(token: String): RedeemedInvite =
    sendDecoding<RedeemInviteRequest, RedeemedInvite>("POST", "invites/redeem", RedeemInviteRequest(token))

/** Why joining with an invite didn't work, said for the guest: short, and saying whom to ask. */
fun guestJoinProblem(error: Throwable, ownerName: String, keyRefused: Boolean = false): String = when {
    keyRefused -> "This invite has run out or been used. Ask $ownerName for a new one."
    error is FlexarrException.Http && error.status == 410 -> "This invite has run out or been used. Ask $ownerName for a new one."
    error is FlexarrException.Http && error.status == 503 -> "Invites are paused on $ownerName’s Flexarr. Ask them to check it."
    error is FlexarrException.Http && (error.status == 403 || error.status == 404) -> "This invite didn’t work. Ask $ownerName for a new one."
    else -> "Couldn’t reach $ownerName’s Flexarr. Check your internet and try again."
}

/**
 * The family guest's tunnel, in front of every request to 127.0.0.1 (see [FlexarrClient.loopbackTunnel]). Both block,
 * so they're only called off the main thread (OkHttp's own threads).
 */
interface LoopbackTunnel {
    /** The port it's listening on, starting it first if it isn't. Throws [IOException] when it can't be started. */
    fun port(): Int

    /** Nothing was listening on [failed]: starts it again, and gives the port it listens on now. */
    fun restart(failed: Int): Int

    companion object {
        /** Sends loopback requests to [current]'s port, once it's up, and starts it again once if it had gone. */
        fun interceptor(current: () -> LoopbackTunnel?): Interceptor = Interceptor { chain ->
            val request = chain.request()
            val tunnel = current()
            if (tunnel == null || request.url.host != "127.0.0.1") return@Interceptor chain.proceed(request)
            val port = tunnel.port()
            try {
                chain.proceed(request.toPort(port))
            } catch (e: ConnectException) {
                chain.proceed(request.toPort(tunnel.restart(port)))
            }
        }

        private fun Request.toPort(port: Int): Request =
            if (url.port == port) this else newBuilder().url(url.newBuilder().port(port).build()).build()
    }
}

/**
 * Inviting a guest's TV by the code it shows: the TV's side of the meeting at Flexarr's relay (relay/api/pair.js). The
 * TV leaves its code and public key there, its host's server leaves an invite sealed to that key, and the TV collects
 * and opens it. As FlexarrKit's GuestTvCode.
 */
class GuestTvCode(
    /** "ABCDEFGH", as the relay keeps it. */
    val code: String = randomCode(),
    /** Proves to the relay that this TV is the one collecting. */
    val secret: String = randomSecret(),
    private val privateKey: ECPrivateKey,
    private val publicKeyValue: ECPublicKey,
) {
    sealed interface Answer {
        data object Waiting : Answer
        data class Invite(val invite: GuestInvite) : Answer
        /** The code ran out (ten minutes) or was never there: show a new one. */
        data object Expired : Answer
    }

    sealed class Failure(message: String) : Exception(message) {
        /** Another TV is showing this code: make another. */
        class CodeTaken : Failure("That code is taken.")
        /** The invite couldn't be opened or isn't a well-formed one. */
        class BadInvite : Failure("The invite couldn’t be opened.")
        class Relay(val status: Int) : Failure("Flexarr’s relay answered with an error (HTTP $status).")
    }

    /** "ABCD-EFGH", as the TV shows it. */
    val display: String get() = "${code.take(4)}-${code.takeLast(4)}"

    /** [display] for a screen reader, a character at a time ("H 4 2 K, F M 5 W"), which it would read as one word. */
    val spoken: String get() = "${code.take(4).toList().joinToString(" ")}, ${code.takeLast(4).toList().joinToString(" ")}"

    /** The public key as the relay and server take it: X9.63 uncompressed (65 bytes), base64. */
    val publicKey: String get() = Base64.getEncoder().encodeToString(encode(publicKeyValue))

    /** Leaves the code and public key at the relay for ten minutes. */
    suspend fun register(http: OkHttpClient = FlexarrClient.sharedHttp, relay: HttpUrl = RELAY) = withContext(Dispatchers.IO) {
        val body = FlexarrClient.json.encodeToString(mapOf("code" to code, "publicKey" to publicKey, "secret" to secret))
        val request = Request.Builder().url(relay).post(body.toRequestBody(jsonType)).build()
        http.newCall(request).execute().use { response ->
            when (response.code) {
                201 -> Unit
                409 -> throw Failure.CodeTaken()
                else -> throw Failure.Relay(response.code)
            }
        }
    }

    /** Asks the relay whether the invite has arrived. */
    suspend fun poll(http: OkHttpClient = FlexarrClient.sharedHttp, relay: HttpUrl = RELAY): Answer = withContext(Dispatchers.IO) {
        val url = relay.newBuilder().addQueryParameter("code", code).addQueryParameter("secret", secret).build()
        http.newCall(Request.Builder().url(url).build()).execute().use { response ->
            when (response.code) {
                204 -> Answer.Waiting
                404 -> Answer.Expired
                200 -> {
                    val fields = runCatching { FlexarrClient.json.parseToJsonElement(response.body.string()).jsonObject }.getOrNull()
                    val serverKey = fields?.get("serverKey")?.jsonPrimitive?.content?.let(::base64OrNull)
                    val sealed = fields?.get("sealed")?.jsonPrimitive?.content?.let(::base64OrNull)
                    if (serverKey == null || sealed == null) throw Failure.BadInvite()
                    val payload = runCatching { String(open(serverKey, sealed), Charsets.UTF_8) }.getOrNull()
                    Answer.Invite(payload?.let(GuestInvite::decode) ?: throw Failure.BadInvite())
                }
                else -> throw Failure.Relay(response.code)
            }
        }
    }

    /**
     * Opens what the server sealed to this TV's key: P-256 ECDH, HKDF-SHA256 (salt: this TV's key then the server's,
     * info "flexarr-tv-invite"), AES-256-GCM as nonce (12 bytes), ciphertext, tag (16).
     */
    fun open(serverKey: ByteArray, sealed: ByteArray): ByteArray {
        require(sealed.size > NONCE + TAG) { "Too short to be sealed" }
        val agreement = KeyAgreement.getInstance("ECDH").apply {
            init(privateKey)
            doPhase(decodePublicKey(serverKey), true)
        }
        val shared = agreement.generateSecret()
        val key = hkdf(shared, salt = encode(publicKeyValue) + serverKey, info = INFO.toByteArray(), length = 32)
        val cipher = Cipher.getInstance("AES/GCM/NoPadding").apply {
            init(Cipher.DECRYPT_MODE, SecretKeySpec(key, "AES"), GCMParameterSpec(TAG * 8, sealed, 0, NONCE))
        }
        return cipher.doFinal(sealed, NONCE, sealed.size - NONCE)
    }

    companion object {
        /** Letters and digits that can't be mistaken for each other when read out over the phone. */
        const val ALPHABET = "ABCDEFGHJKMNPQRSTUVWXYZ23456789"
        val RELAY: HttpUrl = "https://push.flexarr.io/api/pair".toHttpUrl()
        private const val INFO = "flexarr-tv-invite"
        private const val NONCE = 12
        private const val TAG = 16
        private val jsonType = "application/json".toMediaType()
        private val random = SecureRandom()

        /** A new code with a fresh key pair. */
        fun create(): GuestTvCode {
            val pair = KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1"), random) }.generateKeyPair()
            return GuestTvCode(privateKey = pair.private as ECPrivateKey, publicKeyValue = pair.public as ECPublicKey)
        }

        /** A code from a key pair given as raw bytes (the private scalar, 32 bytes, and the X9.63 public key): tests. */
        fun fromRawKeys(privateKey: ByteArray, publicKey: ByteArray, code: String = randomCode(), secret: String = randomSecret()): GuestTvCode {
            val private = KeyFactory.getInstance("EC").generatePrivate(ECPrivateKeySpec(BigInteger(1, privateKey), p256)) as ECPrivateKey
            return GuestTvCode(code, secret, private, decodePublicKey(publicKey))
        }

        fun randomCode(): String = String(CharArray(8) { ALPHABET[random.nextInt(ALPHABET.length)] })

        /** 32 random bytes, base64url without padding (43 characters). */
        fun randomSecret(): String = Base64.getUrlEncoder().withoutPadding().encodeToString(ByteArray(32).also(random::nextBytes))

        /** P-256's parameters, from a key made once (the platform has no lighter way that every Android has). */
        private val p256: ECParameterSpec by lazy {
            (KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1")) }.generateKeyPair().public as ECPublicKey).params
        }

        private fun decodePublicKey(x963: ByteArray): ECPublicKey {
            require(x963.size == 65 && x963[0] == 4.toByte()) { "Not an uncompressed P-256 key" }
            val point = ECPoint(BigInteger(1, x963.copyOfRange(1, 33)), BigInteger(1, x963.copyOfRange(33, 65)))
            return KeyFactory.getInstance("EC").generatePublic(ECPublicKeySpec(point, p256)) as ECPublicKey
        }

        private fun encode(key: ECPublicKey): ByteArray = byteArrayOf(4) + fixed(key.w.affineX) + fixed(key.w.affineY)

        /** [value] as exactly 32 big-endian bytes. */
        private fun fixed(value: BigInteger): ByteArray {
            val bytes = value.toByteArray().let { if (it.size > 32) it.copyOfRange(it.size - 32, it.size) else it }
            return ByteArray(32 - bytes.size) + bytes
        }

        /** HKDF-SHA256 (RFC 5869), for up to 32 bytes. */
        private fun hkdf(ikm: ByteArray, salt: ByteArray, info: ByteArray, length: Int): ByteArray {
            val extract = Mac.getInstance("HmacSHA256").apply { init(SecretKeySpec(salt, "HmacSHA256")) }.doFinal(ikm)
            val expand = Mac.getInstance("HmacSHA256").apply { init(SecretKeySpec(extract, "HmacSHA256")) }.doFinal(info + byteArrayOf(1))
            return expand.copyOf(length)
        }

        private fun base64OrNull(text: String): ByteArray? = runCatching { Base64.getDecoder().decode(text) }.getOrNull()
    }
}
