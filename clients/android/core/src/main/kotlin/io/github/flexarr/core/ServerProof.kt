package io.github.flexarr.core

import java.security.MessageDigest
import java.security.SecureRandom
import java.util.concurrent.ConcurrentHashMap
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec

/**
 * How an address shows it's the app's own server before it's sent the sign-in over plain HTTP. Its id alone isn't
 * enough: anyone may ask a server for its id, and something else at the same address on another network could give it
 * too. So the app sends a fresh random challenge and which key it holds ([keyId], from which the key can't be worked
 * out), and the server answers with what only a holder of that key can work out ([answer]). Servers before 0.67 give
 * no answer; once the server has answered on this device ([memory]), an address that doesn't is never trusted again.
 */
object ServerProof {
    /** Which servers have answered on this device. The app keeps it on disk ([memory]); by default it's kept only while running. */
    interface Memory {
        fun hasAnswered(serverId: String): Boolean
        fun answered(serverId: String)
    }

    /** Where it's noted which servers have answered; the app sets one that's kept between launches. */
    @Volatile
    var memory: Memory = object : Memory {
        private val seen = ConcurrentHashMap.newKeySet<String>()
        override fun hasAnswered(serverId: String) = serverId in seen
        override fun answered(serverId: String) {
            seen += serverId
        }
    }

    /** The SHA-256 of a key, as both sides start from (lowercase hex). */
    fun hash(key: String): String = hex(MessageDigest.getInstance("SHA-256").digest(key.toByteArray()))

    /** What the app calls the key it holds, given its hash. */
    fun keyId(keyHash: String): String = hash("flexarr key id|$keyHash")

    /** The answer to [challenge] from the server with id [serverId] that holds the key. */
    fun answer(keyHash: String, serverId: String, challenge: String): String {
        val mac = Mac.getInstance("HmacSHA256")
        mac.init(SecretKeySpec(keyHash.toByteArray(), "HmacSHA256"))
        return hex(mac.doFinal("flexarr proof|$serverId|$challenge".toByteArray()))
    }

    /** A fresh challenge: 16 random bytes, in hex. */
    fun challenge(): String = hex(ByteArray(16).also { SecureRandom().nextBytes(it) })

    /**
     * Whether what answered is the server expected: it gave [expected] for its id and, when the app has a key to send
     * ([key]), the right answer to [challenge], or none at all if this server has never given one here (a server from
     * before). A right answer is noted in [memory], so from then on an address without one isn't believed.
     */
    fun accepts(id: String?, proof: String?, expected: String, key: String?, challenge: String, memory: Memory = this.memory): Boolean {
        if (id != expected) return false
        if (key.isNullOrEmpty()) return true
        if (proof == null) return !memory.hasAnswered(expected)
        if (proof != answer(hash(key), expected, challenge)) return false
        memory.answered(expected)
        return true
    }

    private fun hex(bytes: ByteArray): String = bytes.joinToString("") { "%02x".format(it) }
}
