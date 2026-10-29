package io.github.flexarr.logging

import android.content.Context
import android.os.Build
import android.util.Log
import io.github.flexarr.BuildConfig
import io.github.flexarr.core.ErrorReportFilter
import io.github.flexarr.core.ErrorReportScrubber
import io.github.flexarr.core.ErrorReportSetting
import io.github.flexarr.core.FlexarrClient
import io.github.flexarr.core.LogRedactor
import io.sentry.Sentry
import io.sentry.SentryEvent
import io.sentry.android.core.SentryAndroid
import io.sentry.protocol.App
import io.sentry.protocol.Device
import io.sentry.protocol.Message
import io.sentry.protocol.OperatingSystem
import io.sentry.protocol.User
import okhttp3.HttpUrl.Companion.toHttpUrlOrNull

/**
 * Crash and error reports to Sentry, only while the signed-in server's owner has them on (web Settings › Logs); off
 * otherwise, and there's no switch in the app. Crash handlers have to be in place before the server can be asked, so the
 * last answer for each server is kept and reporting starts at launch from that; the server's current answer then starts
 * or stops it. As the Apple apps' ErrorReports.
 *
 * Only errors, crashes and ANRs go: no performance data, sessions, breadcrumbs, screenshots or requests, and every event
 * is scrubbed ([ErrorReportScrubber]) on its way out. The "user" is the server's random install id.
 */
object ErrorReports {
    private const val DSN = "https://d104d99d6f2b82f13467a9a9eb2562cf@o4512326167232512.ingest.de.sentry.io/4512338449793104"
    private const val TAG = "Flexarr.errorreports"

    /** What Settings › Server says under the error-reports row. */
    const val PRIVACY_NOTE = "Set on your server, in Settings › Logs on the web. When on, crashes and errors go to Sentry, the error tracker Flexarr uses: what went wrong and where in the code, the app version, the Android version and phone model, and a random id. Never titles, file names, what you watch, names, addresses or keys."

    private lateinit var context: Context

    /** The install id reports carry while on (an empty string for none); null while off. */
    @Volatile private var running: String? = null

    /** Whether reports are being sent now. */
    val isOn: Boolean get() = running != null

    private val cache get() = context.getSharedPreferences("errorReports", Context.MODE_PRIVATE)

    private fun key(server: String): String {
        val url = server.toHttpUrlOrNull() ?: return server
        return "${url.host.lowercase()}:${url.port}"
    }

    /** At launch: starts if the server signed in to had reports on last time it was asked. */
    fun resume(context: Context, server: String?) {
        this.context = context.applicationContext
        val id = server?.let { cache.getString(key(it), null) } ?: return
        start(id)
    }

    /** The server's answer (null: it didn't say, so off): kept for next launch, and reports started or stopped to match. */
    fun apply(setting: ErrorReportSetting?, server: String) {
        if (setting?.enabled == true) {
            cache.edit().putString(key(server), setting.installId.orEmpty()).apply()
            start(setting.installId.orEmpty())
        } else {
            cache.edit().remove(key(server)).apply()
            stop()
        }
    }

    /** Asks the server for its setting and follows it; best effort. */
    suspend fun refresh(client: FlexarrClient, server: String) {
        val status = runCatching { client.systemStatus() }.getOrNull() ?: return
        apply(status.errorReports, server)
    }

    /** Signed out, or on to another server: stops until that server says otherwise. */
    fun stop() {
        if (running == null) return
        running = null
        Sentry.close()
        Log.i(TAG, "Error reports off")
    }

    /** An error logged as an error: only its type goes, with the log's category, never its message. */
    fun capture(error: Throwable, category: String) {
        if (!isOn || !ErrorReportFilter.shouldReport(error)) return
        // A bare error of the same kind and from the same place: its message (which can hold titles and addresses) stays here.
        val bare = Exception(error.javaClass.name).apply { stackTrace = error.stackTrace }
        Sentry.captureException(bare) { scope -> scope.setTag("category", category) }
    }

    private fun start(id: String) {
        val already = running
        running = id
        if (already != null) {
            // Already on; a new install id only needs the user changed.
            if (already != id) Sentry.setUser(user(id))
            return
        }
        SentryAndroid.init(context) { options ->
            options.dsn = DSN
            options.release = "flexarr-android@${BuildConfig.VERSION_NAME}+${BuildConfig.VERSION_CODE}"
            options.environment = if (BuildConfig.DEBUG || isEmulator) "development" else "production"
            options.isSendDefaultPii = false
            options.tracesSampleRate = null
            options.isEnableAutoSessionTracking = false
            options.maxBreadcrumbs = 0
            options.isEnableActivityLifecycleBreadcrumbs = false
            options.isEnableAppLifecycleBreadcrumbs = false
            options.isEnableSystemEventBreadcrumbs = false
            options.isEnableAppComponentBreadcrumbs = false
            options.isEnableNetworkEventBreadcrumbs = false
            options.isEnableUserInteractionBreadcrumbs = false
            options.isEnableUserInteractionTracing = false
            options.isEnableAutoActivityLifecycleTracing = false
            options.isAttachScreenshot = false
            options.isAttachViewHierarchy = false
            options.isSendClientReports = false
            options.isCollectAdditionalContext = false
            options.isAnrEnabled = true // A hang is an error too.
            options.setBeforeSend { event, _ -> scrub(event) }
        }
        Sentry.setUser(user(id))
        Log.i(TAG, "Error reports on")
    }

    private fun user(id: String): User? = id.takeIf { it.isNotEmpty() }?.let { User().apply { this.id = it } }

    private val isEmulator: Boolean
        get() = Build.FINGERPRINT.startsWith("generic") || Build.FINGERPRINT.contains("emulator") || Build.MODEL.contains("sdk_gphone")

    /** The event with nothing that could say who, where or what was being watched. */
    private fun scrub(event: SentryEvent): SentryEvent {
        val scrubber = ErrorReportScrubber(LogRedactor(secrets = LogHub.secrets, maxLength = 2000))
        event.breadcrumbs = null
        event.request = null
        event.extras = null
        event.serverName = null
        event.setModules(null)
        event.transaction = null
        event.tags = event.tags?.filterKeys { it in ErrorReportScrubber.tagKeys }?.toMutableMap()
        event.user = running?.let(::user)
        event.message?.let { message ->
            event.message = Message().apply { formatted = scrubber.scrub(message.formatted ?: message.message.orEmpty()) }
        }
        event.exceptions?.forEach { exception ->
            exception.value = exception.value?.let(scrubber::scrub)
            exception.mechanism?.let { it.description = it.description?.let(scrubber::scrub); it.data = null; it.helpLink = null }
            exception.stacktrace?.frames?.forEach { it.vars = null; it.absPath = null; it.contextLine = null; it.preContext = null; it.postContext = null }
        }
        event.threads?.forEach { thread ->
            thread.name = null
            thread.stacktrace?.frames?.forEach { it.vars = null; it.absPath = null }
        }
        // Only the device's kind, the OS and the app's version.
        val contexts = event.contexts
        val device = contexts.device
        val os = contexts.operatingSystem
        val app = contexts.app
        contexts.keys().toList().forEach { contexts.remove(it) }
        device?.let { old ->
            contexts.setDevice(Device().apply {
                model = old.model; modelId = old.modelId; family = old.family; manufacturer = old.manufacturer; brand = old.brand
                archs = old.archs; isSimulator = old.isSimulator
            })
        }
        os?.let { old -> contexts.setOperatingSystem(OperatingSystem().apply { name = old.name; version = old.version; build = old.build }) }
        app?.let { old -> contexts.setApp(App().apply { appVersion = old.appVersion; appBuild = old.appBuild }) }
        return event
    }
}
