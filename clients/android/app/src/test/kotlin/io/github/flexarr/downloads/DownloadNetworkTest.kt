package io.github.flexarr.downloads

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/** When downloads the phone starts by itself are asked for and fetched, as on iPhone. */
class DownloadNetworkTest {
    @Test
    fun wiFiIsUnmeteredWithDataSaverOff() {
        assertTrue(DownloadNetwork.isWiFi(online = true, unmetered = true, dataSaver = false))
        // Data Saver is Low Data Mode: not Wi-Fi, even on an unmetered network.
        assertFalse(DownloadNetwork.isWiFi(online = true, unmetered = true, dataSaver = true))
        assertFalse(DownloadNetwork.isWiFi(online = true, unmetered = false, dataSaver = false))
        assertFalse(DownloadNetwork.isWiFi(online = false, unmetered = true, dataSaver = false))
    }

    @Test
    fun automaticDownloadsKeptToWiFiAreNotAskedForOffIt() {
        assertTrue(DownloadNetwork.mayStartAutomatic(online = true, onWiFi = true, automaticOnWiFiOnly = true))
        assertFalse(DownloadNetwork.mayStartAutomatic(online = true, onWiFi = false, automaticOnWiFiOnly = true))
        assertFalse(DownloadNetwork.mayStartAutomatic(online = false, onWiFi = false, automaticOnWiFiOnly = true))
    }

    @Test
    fun automaticDownloadsAllowedMobileDataAreAskedForOnAnyConnection() {
        assertTrue(DownloadNetwork.mayStartAutomatic(online = true, onWiFi = false, automaticOnWiFiOnly = false))
        assertFalse(DownloadNetwork.mayStartAutomatic(online = false, onWiFi = false, automaticOnWiFiOnly = false))
    }

    @Test
    fun transfersWaitForUnmeteredWhenKeptToWiFiOrComingByThemselvesUnderDataSaver() {
        assertTrue(DownloadNetwork.needsUnmetered(wifiOnly = true, automatic = true, dataSaver = false))
        assertTrue(DownloadNetwork.needsUnmetered(wifiOnly = false, automatic = true, dataSaver = true))
        assertFalse(DownloadNetwork.needsUnmetered(wifiOnly = false, automatic = true, dataSaver = false))
        // Asked for by hand: any connection, Data Saver or not.
        assertFalse(DownloadNetwork.needsUnmetered(wifiOnly = false, automatic = false, dataSaver = true))
    }
}
