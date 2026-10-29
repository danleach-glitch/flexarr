package io.github.flexarr

import android.app.Application
import android.content.Context
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
        model = AppModel(this)
    }

    /** Artwork comes over the same connections as the API. */
    override fun newImageLoader(context: PlatformContext): ImageLoader =
        ImageLoader.Builder(context)
            .components { add(OkHttpNetworkFetcherFactory(callFactory = { FlexarrClient.sharedHttp })) }
            .build()
}

val Context.appModel: AppModel get() = (applicationContext as FlexarrApplication).model
