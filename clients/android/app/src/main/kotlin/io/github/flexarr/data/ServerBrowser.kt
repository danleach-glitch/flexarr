package io.github.flexarr.data

import android.content.Context
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.os.Handler
import android.os.Looper
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import io.github.flexarr.core.FlexarrClient

/** A Flexarr server announcing itself on the local network. */
data class DiscoveredServer(val name: String, val address: String)

/**
 * Finds Flexarr servers on the local network: each announces "_flexarr._tcp" with its address in the TXT record
 * ("url=http://Dans-Mac-Mini.local:8383"), so there's nothing to type. Android resolves one service at a time, so
 * they queue.
 */
class ServerBrowser(context: Context) {
    private val nsd = context.getSystemService(NsdManager::class.java)
    private val main = Handler(Looper.getMainLooper())
    private val waiting = ArrayDeque<NsdServiceInfo>()
    private var resolving = false
    private var listener: NsdManager.DiscoveryListener? = null

    var servers by mutableStateOf(emptyList<DiscoveredServer>())
        private set

    fun start() {
        if (listener != null) return
        val listener = object : NsdManager.DiscoveryListener {
            override fun onServiceFound(service: NsdServiceInfo) {
                main.post { enqueue(service) }
            }

            override fun onServiceLost(service: NsdServiceInfo) {
                main.post { servers = servers.filterNot { it.name == service.serviceName } }
            }

            override fun onDiscoveryStarted(serviceType: String) {}
            override fun onDiscoveryStopped(serviceType: String) {}
            override fun onStartDiscoveryFailed(serviceType: String, errorCode: Int) {}
            override fun onStopDiscoveryFailed(serviceType: String, errorCode: Int) {}
        }
        this.listener = listener
        nsd.discoverServices(SERVICE_TYPE, NsdManager.PROTOCOL_DNS_SD, listener)
    }

    fun stop() {
        listener?.let { runCatching { nsd.stopServiceDiscovery(it) } }
        listener = null
        waiting.clear()
    }

    private fun enqueue(service: NsdServiceInfo) {
        waiting.addLast(service)
        resolveNext()
    }

    @Suppress("DEPRECATION") // Its replacement (registerServiceInfoCallback) is Android 14 and later only.
    private fun resolveNext() {
        if (resolving || listener == null) return
        val service = waiting.removeFirstOrNull() ?: return
        resolving = true
        nsd.resolveService(service, object : NsdManager.ResolveListener {
            override fun onServiceResolved(info: NsdServiceInfo) {
                main.post {
                    resolving = false
                    found(info)?.let { server -> servers = (servers.filterNot { it.address == server.address } + server).sortedBy { it.name } }
                    resolveNext()
                }
            }

            override fun onResolveFailed(info: NsdServiceInfo, errorCode: Int) {
                main.post {
                    resolving = false
                    resolveNext()
                }
            }
        })
    }

    @Suppress("DEPRECATION")
    private fun found(info: NsdServiceInfo): DiscoveredServer? {
        val url = info.attributes["url"]?.let { String(it) }
            ?: info.host?.hostAddress?.let { "http://$it:${info.port}" }
            ?: return null
        val address = FlexarrClient.normalizeAddress(url) ?: return null
        return DiscoveredServer(info.serviceName, address.toString())
    }

    private companion object {
        const val SERVICE_TYPE = "_flexarr._tcp"
    }
}
