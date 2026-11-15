package io.github.flexarr.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/** An address shows it holds the app's key before it's sent it over plain HTTP (see [ServerProof]). */
class ServerProofTest {
    private val challenge = "00112233445566778899aabbccddeeff"

    private fun memory() = object : ServerProof.Memory {
        val seen = mutableSetOf<String>()
        override fun hasAnswered(serverId: String) = serverId in seen
        override fun answered(serverId: String) {
            seen += serverId
        }
    }

    /** The same numbers as the server's own test (ServerProofTests in Flexarr.Api.Tests), so both work it out alike. */
    @Test
    fun worksOutTheSameAnswersAsTheServer() {
        val hash = ServerProof.hash("test-key")

        assertEquals("62af8704764faf8ea82fc61ce9c4c3908b6cb97d463a634e9e587d7c885db0ef", hash)
        assertEquals("3de99eba31a42ce3d62608d3d0dc14263f03df662a95478c87e6f6bba7dcba2f", ServerProof.keyId(hash))
        assertEquals("7991230abbbdddd2888885e97b6dad0651507d6da3f6f3467d198caaa5378e01", ServerProof.answer(hash, "server-1", challenge))
    }

    @Test
    fun challengesAreFreshEachTime() {
        val first = ServerProof.challenge()

        assertEquals(32, first.length)
        assertNotEquals(first, ServerProof.challenge())
    }

    @Test
    fun theRightAnswerIsBelieved_AndFromThenOnNoAnswerIsnt() {
        val memory = memory()
        val right = ServerProof.answer(ServerProof.hash("test-key"), "server-1", challenge)

        // A server from before gives none: its id is all there is to go on.
        assertTrue(ServerProof.accepts("server-1", null, "server-1", "test-key", challenge, memory))
        assertTrue(ServerProof.accepts("server-1", right, "server-1", "test-key", challenge, memory))
        // Something at the same address elsewhere that gives the id, but no answer, once the server has given one.
        assertFalse(ServerProof.accepts("server-1", null, "server-1", "test-key", challenge, memory))
    }

    @Test
    fun anAnswerToAnotherChallenge_OrFromAnotherServer_OrForAnotherKey_IsntBelieved() {
        val memory = memory()
        val hash = ServerProof.hash("test-key")
        val wrong = listOf(
            ServerProof.answer(hash, "server-1", "ffeeddccbbaa99887766554433221100"),
            ServerProof.answer(hash, "server-2", challenge),
            ServerProof.answer(ServerProof.hash("another-key"), "server-1", challenge),
            "nonsense",
        )

        wrong.forEach { assertFalse(ServerProof.accepts("server-1", it, "server-1", "test-key", challenge, memory)) }
        assertFalse(ServerProof.accepts("server-2", null, "server-1", "test-key", challenge, memory))
    }

    @Test
    fun withNoKeyToSend_TheIdIsEnough() {
        assertTrue(ServerProof.accepts("server-1", null, "server-1", null, challenge, memory()))
    }
}
