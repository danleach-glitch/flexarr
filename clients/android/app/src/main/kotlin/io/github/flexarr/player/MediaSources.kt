package io.github.flexarr.player

import android.content.Context
import androidx.annotation.OptIn
import androidx.media3.common.util.BitmapLoader
import androidx.media3.common.util.UnstableApi
import androidx.media3.datasource.DataSource
import androidx.media3.datasource.DataSourceBitmapLoader
import androidx.media3.datasource.DataSpec
import androidx.media3.datasource.TransferListener
import androidx.media3.datasource.DefaultDataSource
import androidx.media3.datasource.okhttp.OkHttpDataSource
import androidx.media3.exoplayer.source.DefaultMediaSourceFactory
import androidx.media3.exoplayer.source.MediaSource
import androidx.media3.session.MediaSession
import io.github.flexarr.core.FlexarrClient

/**
 * Where every player's media comes from: streams over the app's own connections ([FlexarrClient.sharedHttp]), so a
 * family guest's go through its tunnel the way the API's do (waiting for it, or starting it again), and downloads from
 * their files.
 */
@OptIn(UnstableApi::class)
fun mediaSources(context: Context, transfers: TransferListener? = null): MediaSource.Factory =
    DefaultMediaSourceFactory(DefaultDataSource.Factory(context, OkHttpDataSource.Factory(FlexarrClient.sharedHttp).setTransferListener(transfers)))

/**
 * Hears every download from the server (segments, a file played as it is) and gives how fast each flows to [sample]
 * ([TransferMeter]: not the wait for the first byte, nor pauses), for the link speed (docs/remote-streaming.md).
 */
@OptIn(UnstableApi::class)
class LinkSpeedListener(private val sample: (bytes: Long, millis: Long) -> Unit) : TransferListener {
    private val meters = java.util.Collections.synchronizedMap(java.util.IdentityHashMap<DataSource, io.github.flexarr.core.TransferMeter>())

    override fun onTransferInitializing(source: DataSource, dataSpec: DataSpec, isNetwork: Boolean) = Unit

    override fun onTransferStart(source: DataSource, dataSpec: DataSpec, isNetwork: Boolean) {
        if (isNetwork) meters[source] = io.github.flexarr.core.TransferMeter(android.os.SystemClock::elapsedRealtime, sample)
    }

    override fun onBytesTransferred(source: DataSource, dataSpec: DataSpec, isNetwork: Boolean, bytesTransferred: Int) {
        meters[source]?.transferred(bytesTransferred)
    }

    override fun onTransferEnd(source: DataSource, dataSpec: DataSpec, isNetwork: Boolean) {
        meters.remove(source)?.ended()
    }
}

/**
 * How a media session loads the artwork it shows (the notification, the lock screen): over the app's own connections
 * too, so a family guest's reaches its server through the tunnel, which takes only the app's requests. Decoded no
 * bigger than the session's own loader would (a downloaded poster can be thousands of pixels high); the session adds its
 * cache and size limits around it.
 */
@OptIn(UnstableApi::class)
fun sessionBitmapLoader(context: Context): BitmapLoader =
    DataSourceBitmapLoader.Builder(context)
        .setDataSourceFactory(DefaultDataSource.Factory(context, OkHttpDataSource.Factory(FlexarrClient.sharedHttp)))
        .setMaximumOutputDimension(MediaSession.getBitmapDimensionLimit(context) * 2 - 1)
        .build()

/**
 * The video player's own bandwidth meter, starting from [measured] (the saved link speed, bits per second) when there
 * is one, so a stream of several qualities opens on the one the link carries rather than ExoPlayer's guess for the
 * network type (docs/remote-streaming.md).
 */
@OptIn(UnstableApi::class)
fun bandwidthMeter(context: Context, measured: Long?): androidx.media3.exoplayer.upstream.DefaultBandwidthMeter =
    androidx.media3.exoplayer.upstream.DefaultBandwidthMeter.Builder(context)
        .apply { if (measured != null && measured > 0) setInitialBitrateEstimate(measured) }
        .build()
