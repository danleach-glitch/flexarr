package io.github.flexarr.core

import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test
import java.util.Base64

/** The server's name: from the server's status and from invites, and the "Dan's Flexarr" it falls back to. */
class ServerNameTest {
    private fun status(extra: String) = FlexarrClient.json.decodeFromString(
        SystemStatus.serializer(),
        """{"appName":"Flexarr","version":"0.61.0","osDescription":"Linux","startTime":"2026-11-04T04:10:44Z"$extra}""",
    )

    @Test
    fun `the status has the name only from servers that send it`() {
        assertEquals("Smith House", status(""","serverName":"Smith House"""").serverName)
        assertNull(status("").serverName)
    }

    private fun invite(vararg changes: Pair<String, Any?>): GuestInvite {
        val fields = linkedMapOf<String, Any?>(
            "v" to 1, "n" to "Dan", "s" to "server-1", "a" to "100.101.102.103:8383", "k" to "tskey-auth-abc",
            "t" to "token-123", "g" to "Gran’s phone", "p" to "Gran", "e" to "2099-01-01T00:00:00Z",
        )
        changes.forEach { (key, value) -> fields[key] = value }
        val json = JsonObject(fields.mapValues { (_, value) -> if (value is Number) JsonPrimitive(value) else JsonPrimitive(value as String?) }).toString()
        return GuestInvite.parse(Base64.getUrlEncoder().withoutPadding().encodeToString(json.toByteArray()))!!
    }

    @Test
    fun `an invite calls the server by its name, or by whose it is`() {
        assertEquals("Smith House", invite("m" to "Smith House").serverTitle)
        assertEquals("Dan’s Flexarr", invite("m" to "  ").serverTitle)
        assertEquals("Dan’s Flexarr", invite().serverTitle)
        assertNull(invite().serverName)
    }

    @Test
    fun `with no name and no owner it's this Flexarr`() {
        assertEquals("this Flexarr", serverTitle(null, " "))
        assertEquals("Smith House", serverTitle("Smith House", "Dan"))
    }

    @Test
    fun `joining problems name the server`() {
        assertEquals(
            GuestJoinProblem("Smith House isn’t taking guests just now.", canRetry = false),
            guestJoinProblem(FlexarrException.Http(503, null), "Dan", server = ServerNaming("Smith House", "Dan")),
        )
        assertEquals(
            GuestJoinProblem("Can’t reach Smith House. Check this device’s internet.", canRetry = true),
            guestJoinProblem(FlexarrException.Unreachable(java.io.IOException()), "Dan", server = ServerNaming("Smith House", "Dan")),
        )
    }

    @Test
    fun `sentences pick a whole wording for each kind of name`() {
        val leave = "{kind, select, named {Leave {server}?} owner {Leave {owner}’s Flexarr?} other {Leave this Flexarr?}}"
        assertEquals("Leave Smith House?", io.github.flexarr.core.i18n.t(leave, *ServerNaming("Smith House", "Dan").values))
        assertEquals("Leave Dan’s Flexarr?", io.github.flexarr.core.i18n.t(leave, *ServerNaming(" ", "Dan").values))
        assertEquals("Leave this Flexarr?", io.github.flexarr.core.i18n.t(leave, *ServerNaming(null, null).values))
    }

    @Test
    fun `a guest only sees a name someone chose`() {
        val unnamed = status(""","serverName":"Dans-Mac-Mini","serverNamed":false""")
        assertNull(unnamed.serverName(forGuest = true))
        assertEquals("Dans-Mac-Mini", unnamed.serverName(forGuest = false))
        assertEquals("Smith House", status(""","serverName":"Smith House","serverNamed":true""").serverName(forGuest = true))
    }
}
