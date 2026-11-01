package io.github.flexarr

import android.content.Context
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.os.Handler
import android.os.Looper

/**
 * Whether this device has a network at all, so the app can go straight to offline mode rather than wait out a
 * request's timeout, and try again as soon as there's one. Read at once when made; changes arrive on the main thread.
 */
class NetworkWatch(context: Context) {
    private val connectivity = context.getSystemService(ConnectivityManager::class.java)
    private val listeners = mutableListOf<(offline: Boolean) -> Unit>()

    /** No network that reaches the internet (airplane mode, Wi-Fi and mobile data off). */
    @Volatile
    var isOffline: Boolean = connectivity.activeNetwork?.let(connectivity::getNetworkCapabilities)
        ?.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET) != true
        private set

    init {
        connectivity.registerDefaultNetworkCallback(object : ConnectivityManager.NetworkCallback() {
            override fun onCapabilitiesChanged(network: Network, capabilities: NetworkCapabilities) =
                changed(!capabilities.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET))

            override fun onLost(network: Network) = changed(true)
        }, Handler(Looper.getMainLooper()))
    }

    /** Hears every change from then on: true when the network has gone, false when it's back. On the main thread. */
    fun onChange(listener: (offline: Boolean) -> Unit) {
        listeners += listener
    }

    private fun changed(offline: Boolean) {
        if (offline == isOffline) return
        isOffline = offline
        listeners.forEach { it(offline) }
    }
}
