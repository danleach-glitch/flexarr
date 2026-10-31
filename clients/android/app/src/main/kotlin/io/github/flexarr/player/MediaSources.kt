package io.github.flexarr.player

import android.content.Context
import androidx.annotation.OptIn
import androidx.media3.common.util.UnstableApi
import androidx.media3.datasource.DefaultDataSource
import androidx.media3.datasource.okhttp.OkHttpDataSource
import androidx.media3.exoplayer.source.DefaultMediaSourceFactory
import androidx.media3.exoplayer.source.MediaSource
import io.github.flexarr.core.FlexarrClient

/**
 * Where every player's media comes from: streams over the app's own connections ([FlexarrClient.sharedHttp]), so a
 * family guest's go through its tunnel the way the API's do (waiting for it, or starting it again), and downloads from
 * their files.
 */
@OptIn(UnstableApi::class)
fun mediaSources(context: Context): MediaSource.Factory =
    DefaultMediaSourceFactory(DefaultDataSource.Factory(context, OkHttpDataSource.Factory(FlexarrClient.sharedHttp)))
