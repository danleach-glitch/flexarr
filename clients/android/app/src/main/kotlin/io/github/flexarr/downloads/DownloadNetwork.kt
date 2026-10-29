package io.github.flexarr.downloads

/**
 * What the phone's connection lets downloads do, as the iPhone app reads its network. "Wi-Fi" there is a network that
 * isn't expensive and isn't in Low Data Mode; here it's one that isn't metered (or isn't for now) with Data Saver off
 * for Flexarr, Data Saver being Android's Low Data Mode.
 */
internal object DownloadNetwork {
    fun isWiFi(online: Boolean, unmetered: Boolean, dataSaver: Boolean): Boolean = online && unmetered && !dataSaver

    /**
     * A download the phone starts by itself (the next episode, a podcast's newest) may be asked of the server now: on
     * Wi-Fi, or on any connection when Settings lets automatic downloads use mobile data. Until then it isn't asked
     * for at all, so the server doesn't get ready what the phone can't fetch yet.
     */
    fun mayStartAutomatic(online: Boolean, onWiFi: Boolean, automaticOnWiFiOnly: Boolean): Boolean =
        onWiFi || (online && !automaticOnWiFiOnly)

    /**
     * The transfer waits for an unmetered network: one kept to Wi-Fi, or one that came by itself while Data Saver is on
     * (allowed mobile data in Settings, but never with Data Saver). Ones asked for by hand use any connection.
     */
    fun needsUnmetered(wifiOnly: Boolean, automatic: Boolean, dataSaver: Boolean): Boolean = wifiOnly || (automatic && dataSaver)
}
