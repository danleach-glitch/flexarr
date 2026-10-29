package io.github.flexarr

import android.app.Application
import android.app.UiModeManager
import android.content.Context
import android.content.res.Configuration
import coil3.ImageLoader
import coil3.PlatformContext
import coil3.SingletonImageLoader
import coil3.network.okhttp.OkHttpNetworkFetcherFactory
import io.github.flexarr.core.FlexarrClient

class FlexarrApplication : Application(), SingletonImageLoader.Factory {
    lateinit var model: AppModel
        private set

    override fun onCreate() {
        super.onCreate()
        FlexarrClient.deviceType = deviceType()
        model = AppModel(this)
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
