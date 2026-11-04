package io.github.flexarr.data

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/** Which address an announced server is reached at: its own `url` only when that's on the home network. */
class ServerBrowserTest {
    @Test
    fun homeNetworkUrlIsUsed() {
        assertEquals("http://dans-mac-mini.local:8383/", ServerBrowser.address("http://Dans-Mac-Mini.local:8383", "192.168.1.5", 8383))
        assertEquals("http://192.168.1.5:8383/", ServerBrowser.address("http://192.168.1.5:8383", null, 0))
        assertEquals("http://mini:8383/", ServerBrowser.address("http://mini:8383", "10.0.0.2", 8383))
    }

    @Test
    fun urlElsewhereFallsBackToWhereTheAnnouncementCameFrom() {
        assertEquals("http://192.168.1.9:8383/", ServerBrowser.address("https://collector.example.com", "192.168.1.9", 8383))
        assertEquals("http://192.168.1.9:8383/", ServerBrowser.address("http://8.8.8.8:8383", "192.168.1.9", 8383))
        assertEquals("http://192.168.1.9:8383/", ServerBrowser.address("https://someone.tail1234.ts.net", "192.168.1.9", 8383))
    }

    @Test
    fun noUrlUsesTheResolvedHost() {
        assertEquals("http://192.168.1.9:8383/", ServerBrowser.address(null, "192.168.1.9", 8383))
        assertEquals("http://[fe80::1]:8383/", ServerBrowser.address(null, "fe80::1%wlan0", 8383))
    }

    @Test
    fun nothingUsableIsNull() {
        assertNull(ServerBrowser.address("https://collector.example.com", null, 0))
        assertNull(ServerBrowser.address(null, null, 8383))
    }
}
