package io.github.flexarr

import android.app.Application
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
import io.github.flexarr.core.FlexarrClient
import io.github.flexarr.data.CredentialStore
import io.github.flexarr.logging.ErrorReports
import io.github.flexarr.logging.LogHub

class FlexarrApplication : Application(), SingletonImageLoader.Factory {
    lateinit var model: AppModel
        private set

    override fun onCreate() {
        super.onCreate()
        FlexarrClient.deviceType = deviceType()
        LogHub.start(this)
        // Before anything else can crash: on if this server had reports on last time.
        ErrorReports.resume(this, CredentialStore(this).load()?.address)
        model = AppModel(this)
        // Commands from other devices are taken while the app is in front.
        ProcessLifecycleOwner.get().lifecycle.addObserver(object : DefaultLifecycleObserver {
            override fun onStart(owner: LifecycleOwner) = model.listenForCommands()
            override fun onStop(owner: LifecycleOwner) = model.commands.stop()
        })
    }

    /** A TV by its UI mode, a tablet by its narrowest side (600 dp and over, as Android's own layouts count it). */
    private fun deviceType(): String = when {
        getSystemService(UiModeManager::class.java).currentModeType == Configuration.UI_MODE_TYPE_TELEVISION -> "AndroidTv"
        resources.configuration.smallestScreenWidthDp >= 600 -> "AndroidTablet"
        else -> "AndroidPhone"
    }

    /** Artwork comes over the same connections as the API. */
    override fun newImageLoader(context: PlatformContext): ImageLoader =
        ImageLoader.Builder(context)
            .components { add(OkHttpNetworkFetcherFactory(callFactory = { FlexarrClient.sharedHttp })) }
            .build()
}

val Context.appModel: AppModel get() = (applicationContext as FlexarrApplication).model
