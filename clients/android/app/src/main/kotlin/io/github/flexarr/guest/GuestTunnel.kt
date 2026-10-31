package io.github.flexarr.guest

import android.content.Context
import androidx.core.content.edit
import io.github.flexarr.core.FlexarrClient
import io.github.flexarr.core.LoopbackTunnel
import io.github.flexarr.data.GuestTunnelInfo
import io.github.flexarr.logging.FlexarrLog
import io.github.flexarr.tunnel.Tunnel
import kotlinx.serialization.Serializable
import java.io.File
import java.io.IOException
import kotlin.random.Random

private val flexarrLog = FlexarrLog("tunnel")

/**
 * The Tailscale node inside the app that a family guest reaches its host's server through (clients/tunnel, see
 * docs/remote-guests.md). It listens on 127.0.0.1 and forwards each connection to the server on the host's tailnet, so
 * the API, artwork, ExoPlayer and downloads all just use `http://127.0.0.1:<port>`.
 *
 * It runs in the app's process, so it lives as long as the process does: the playback and download foreground services
 * keep it going with them. Requests made before it's up wait for it ([FlexarrClient.loopbackTunnel]), and one that finds
 * nothing listening starts it again. The node's identity is kept in a folder that isn't backed up; the invite's auth key
 * is used once, so losing that folder means needing a new invite.
 */
class GuestTunnel(context: Context) : LoopbackTunnel {
    private val context = context.applicationContext
    private val lock = Any()

    @Volatile
    private var info: GuestTunnelInfo? = null

    @Volatile
    private var listening: Int? = null

    /** Told the port each time the tunnel starts, so the sign-in's address can follow it if it had to move. */
    @Volatile
    var onPort: ((Int) -> Unit)? = null

    /**
     * Told when the tailnet refuses the node's saved identity (the host deleted this device there): the guest no longer
     * has access, as when the server says so. Not for a key refused while joining, which the join says itself.
     */
    @Volatile
    var onRefused: (() -> Unit)? = null

    /** The node's identity, out of backups (a restored copy on another phone would be the same node twice). */
    val stateDir: File get() = File(context.noBackupFilesDir, "tailscale")

    /**
     * The loopback port to listen on, chosen once for this install, so the sign-in's address stays the same from one
     * launch to the next (downloads and the player keep it). Another one is used only when something else has it.
     */
    val preferredPort: Int
        get() {
            prefs.getInt(PORT, 0).takeIf { it in PORTS }?.let { return it }
            return Random.nextInt(PORTS.first, PORTS.last + 1).also { prefs.edit { putInt(PORT, it) } }
        }

    private val prefs get() = context.getSharedPreferences("tunnel", Context.MODE_PRIVATE)

    /**
     * The node's saved identity joined the tailnet with an invite for [info] (the same target and name), so joining
     * with that invite again needs no key: its key was used up the first time.
     */
    fun hasIdentityFor(info: GuestTunnelInfo): Boolean =
        prefs.getString(IDENTITY, null) == identity(info) && stateDir.exists()

    private fun identity(info: GuestTunnelInfo) = "${info.target}|${info.hostname}"

    /** Tunnels to [info] from now on, putting itself in front of loopback requests; null stops it and stands aside. */
    fun use(info: GuestTunnelInfo?) {
        if (this.info != info) stop()
        this.info = info
        FlexarrClient.loopbackTunnel = if (info != null) this else null
    }

    val isInUse: Boolean get() = info != null

    /**
     * Starts the node, or gives its port if it's up already. Blocks until it's on the tailnet (up to a minute), so only
     * off the main thread. [authKey] is needed only the first time; after that the saved identity is used. Throws
     * [TunnelException] when it can't start.
     */
    fun start(authKey: String = ""): Int = synchronized(lock) {
        val info = info ?: throw TunnelException("No guest sign-in", needsLogin = false)
        listening?.let { port -> if (status().state == "running") return port }
        loadLibrary()
        Tunnel.setPreferredPort(preferredPort.toLong())
        val port = try {
            Tunnel.start(stateDir.path, info.hostname, authKey, info.target, "").toInt()
        } catch (e: Exception) {
            val status = status()
            flexarrLog.warning("The tunnel didn't start (${status.state}): ${status.error ?: e.message}")
            val message = status.error ?: e.message ?: "The tunnel didn't start"
            // Tailscale refuses a used or run-out key as "invalid key", which is the same to the guest as needing one.
            val refused = status.state == "needs-login" || message.contains("invalid key", ignoreCase = true)
            if (refused && authKey.isEmpty()) onRefused?.invoke()
            throw TunnelException(message, needsLogin = refused, e)
        }
        listening = port
        // Its key is spent now: trying the same invite again (a sign-in that didn't go through) uses this identity.
        if (authKey.isNotEmpty()) prefs.edit { putString(IDENTITY, identity(info)) }
        flexarrLog.info("The tunnel is up on $port (${status().path})")
        onPort?.invoke(port)
        port
    }

    override fun port(): Int = listening ?: start()

    override fun restart(failed: Int): Int = synchronized(lock) {
        // Another request may have started it again meanwhile.
        if (listening == failed) listening = null
        start()
    }

    /** Back in front: starts it again if it stopped while the app was away (the network changed, say). Off the main thread. */
    fun ensureRunning() {
        if (info == null) return
        if (listening == null || status().state != "running") runCatching { restart(listening ?: 0) }
    }

    /** What the node is doing and how it reaches the server. */
    fun status(): Status {
        if (!loaded) return Status("stopped")
        return runCatching { FlexarrClient.json.decodeFromString(Status.serializer(), Tunnel.status()) }.getOrElse { Status("unknown") }
    }

    /** Stops the node. Its identity stays, for starting again. */
    fun stop() {
        listening = null
        if (loaded) runCatching { Tunnel.stop() }
    }

    /** Leaving: stops it, stands aside, and forgets its identity on the tailnet, so a later invite starts afresh. */
    fun forget() {
        use(null)
        stateDir.deleteRecursively()
        prefs.edit { remove(IDENTITY) }
    }

    /** `state`: running, starting, stopped, needs-login or error. `path`: direct, peer-relay, derp or unknown. */
    @Serializable
    data class Status(val state: String, val path: String = "unknown", val error: String? = null)

    @Volatile
    private var loaded = false

    /** The Go library is large: loaded on first use, never for anyone who isn't a guest. */
    private fun loadLibrary() {
        if (loaded) return
        go.Seq.setContext(context)
        loaded = true
    }

    private companion object {
        const val PORT = "port"
        const val IDENTITY = "identity"
        /** The dynamic range, out of the way of servers people run on their phones. */
        val PORTS = 49152..65535
    }
}

/** The tunnel didn't start. [needsLogin]: its key was refused (used, or run out) and there's no identity to fall back on. */
class TunnelException(message: String, val needsLogin: Boolean, cause: Throwable? = null) : IOException(message, cause)
