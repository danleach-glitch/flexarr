package io.github.flexarr

import android.app.Application
import kotlinx.coroutines.launch
import android.app.UiModeManager
import android.content.Context
import android.content.res.Configuration
import coil3.ImageLoader
import coil3.PlatformContext
import coil3.SingletonImageLoader
import coil3.network.okhttp.OkHttpNetworkFetcherFactory
import androidx.lifecycle.DefaultLifecycleObserver
import androidx.lifecycle.LifecycleOwner
import androidx.lifecycle.ProcessLifecycleOwner
import io.github.flexarr.cast.Casting
import io.github.flexarr.core.FlexarrClient
import io.github.flexarr.data.CredentialStore
import io.github.flexarr.logging.ErrorReports
import io.github.flexarr.logging.LogHub
import io.github.flexarr.notifications.FlexarrMessagingService
import io.github.flexarr.player.DeviceCapabilities
import io.github.flexarr.ui.tv.TvGuideCache

class FlexarrApplication : Application(), SingletonImageLoader.Factory {
    lateinit var model: AppModel
        private set

    override fun onCreate() {
        super.onCreate()
        // The language shown last, before anything draws, for the app and its widgets and notifications alike; its
        // dates and numbers in this device's own variant (docs/languages.md).
        io.github.flexarr.core.i18n.Localization.cacheDir = filesDir
        io.github.flexarr.core.i18n.Localization.deviceLanguages = android.os.LocaleList.getDefault().toLanguageTags().split(',')
        io.github.flexarr.core.i18n.Localization.startWithLastShown()
        FlexarrClient.deviceType = deviceType()
        DeviceCapabilities.warm()
        LogHub.start(this)
        // Read once (decrypting it takes a moment) for both.
        val signIn = CredentialStore(this).load()
        // Before anything else can crash: on if this server had reports on last time.
        ErrorReports.resume(this, signIn?.address)
        model = AppModel(this, signIn)
        // A TV is what's cast to, not from; and on a slow box Cast's route scan at launch stalls the app into an ANR.
        if (!isTelevision) Casting.start(this, model)
        // The TV home screen's Watch Next row: kept fresh in the background, and put right each time the app comes to the front.
        io.github.flexarr.tv.WatchNext.schedule(this)
        FlexarrMessagingService.createChannels(this)
        ReviewPrompt.start(this)
        // Commands from other devices are taken while the app is in front; a guest's tunnel is checked on coming back, and
        // stops a while after going.
        ProcessLifecycleOwner.get().lifecycle.addObserver(object : DefaultLifecycleObserver {
            override fun onStart(owner: LifecycleOwner) {
                model.cameToFront()
                model.listenForCommands()
                model.scope.launch { io.github.flexarr.tv.WatchNext.refresh(this@FlexarrApplication) }
            }

            override fun onStop(owner: LifecycleOwner) {
                model.commands.stop()
                model.wentToBackground()
            }
        })
    }

    /**
     * Android wants memory back, or the app has left the screen: the guide the TV keeps between Live TV and the guide
     * over the picture goes (it's fetched again if it's wanted).
     */
    override fun onTrimMemory(level: Int) {
        super.onTrimMemory(level)
        TvGuideCache.clear()
    }

    /** A TV by its UI mode, a tablet by its narrowest side (600 dp and over, as Android's own layouts count it). */
    private fun deviceType(): String = when {
        isTelevision -> "AndroidTv"
        resources.configuration.smallestScreenWidthDp >= 600 -> "AndroidTablet"
        else -> "AndroidPhone"
    }

    /** Artwork comes over the same connections as the API, in a queue of its own so it never holds the API up. */
    override fun newImageLoader(context: PlatformContext): ImageLoader =
        ImageLoader.Builder(context)
            .components { add(OkHttpNetworkFetcherFactory(callFactory = { FlexarrClient.artworkHttp })) }
            .build()
}

val Context.appModel: AppModel get() = (applicationContext as FlexarrApplication).model

/** A TV (Android TV, Google TV), by its UI mode. */
val Context.isTelevision: Boolean
    get() = getSystemService(UiModeManager::class.java).currentModeType == Configuration.UI_MODE_TYPE_TELEVISION

/** The TV interface (ui/tv) rather than the phone's: on a TV. */
val Context.usesTvInterface: Boolean get() = isTelevision
