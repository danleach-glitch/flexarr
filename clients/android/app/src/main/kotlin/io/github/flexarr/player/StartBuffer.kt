package io.github.flexarr.player

import androidx.annotation.OptIn
import androidx.media3.common.util.UnstableApi
import androidx.media3.exoplayer.DefaultLoadControl
import androidx.media3.exoplayer.LoadControl

/**
 * The players start once a second is buffered (ExoPlayer waits for 2.5 s by default), and after a stall wait two
 * seconds before carrying on (docs/fast-start.md).
 */
@OptIn(UnstableApi::class)
object StartBuffer {
    const val START_MS = 1000

    /** After a stall, two seconds (ExoPlayer waits five by default, which feels worse than starting a little later). */
    const val AFTER_STALL_MS = 2000

    /**
     * The heap left to the rest of the app: what it has beside ExoPlayer's full buffer on a device with the usual
     * 256 MB, where that buffer is known to fit.
     */
    private const val APP_HEAP_BYTES = 112L * 1024 * 1024

    /** However little memory there is, this much is buffered (about 13 seconds at 20 Mbit/s). */
    private const val SMALLEST_BUFFER_BYTES = 32L * 1024 * 1024

    /**
     * The most the buffer may hold on a device that gives the app [maxHeapBytes] of memory, or null to leave
     * ExoPlayer's own limit: 50 seconds or about 144 MB, whichever comes first. That 144 MB is the app's own memory,
     * and a high-bitrate file played directly fills it, so on a TV stick that gives an app 128–192 MB the buffer gets
     * what's left once the app has its share. With 256 MB or more nothing changes.
     */
    fun targetBufferBytes(maxHeapBytes: Long): Int? {
        val room = maxHeapBytes - APP_HEAP_BYTES
        if (room >= DefaultLoadControl.DEFAULT_MUXED_BUFFER_SIZE) return null
        return room.coerceAtLeast(SMALLEST_BUFFER_BYTES).toInt()
    }

    /** [maxHeapBytes]: the most memory the app may use (the device's limit for one app). */
    fun loadControl(maxHeapBytes: Long = Runtime.getRuntime().maxMemory()): LoadControl = DefaultLoadControl.Builder()
        .setBufferDurationsMs(
            DefaultLoadControl.DEFAULT_MIN_BUFFER_MS,
            DefaultLoadControl.DEFAULT_MAX_BUFFER_MS,
            START_MS,
            AFTER_STALL_MS,
        )
        // Sound alone (a book) never gets near either size: its 50 seconds come first.
        .apply { targetBufferBytes(maxHeapBytes)?.let { setTargetBufferBytes(it) } }
        .build()
}
