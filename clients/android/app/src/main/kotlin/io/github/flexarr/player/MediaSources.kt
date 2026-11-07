package io.github.flexarr.player

import android.content.Context
import androidx.annotation.OptIn
import androidx.media3.common.util.BitmapLoader
import androidx.media3.common.util.UnstableApi
import androidx.media3.datasource.DataSourceBitmapLoader
import androidx.media3.datasource.DefaultDataSource
import androidx.media3.datasource.okhttp.OkHttpDataSource
import androidx.media3.exoplayer.source.DefaultMediaSourceFactory
import androidx.media3.exoplayer.source.MediaSource
import androidx.media3.session.CacheBitmapLoader
import io.github.flexarr.core.FlexarrClient

/**
 * Where every player's media comes from: streams over the app's own connections ([FlexarrClient.sharedHttp]), so a
 * family guest's go through its tunnel the way the API's do (waiting for it, or starting it again), and downloads from
 * their files.
 */
@OptIn(UnstableApi::class)
fun mediaSources(context: Context): MediaSource.Factory =
    DefaultMediaSourceFactory(DefaultDataSource.Factory(context, OkHttpDataSource.Factory(FlexarrClient.sharedHttp)))

/**
 * How a media session loads the artwork it shows (the notification, the lock screen): over the app's own connections
 * too, so a family guest's reaches its server through the tunnel, which takes only the app's requests.
 */
@OptIn(UnstableApi::class)
fun sessionBitmapLoader(context: Context): BitmapLoader =
    CacheBitmapLoader(DataSourceBitmapLoader.Builder(context).setDataSourceFactory(DefaultDataSource.Factory(context, OkHttpDataSource.Factory(FlexarrClient.sharedHttp))).build())
