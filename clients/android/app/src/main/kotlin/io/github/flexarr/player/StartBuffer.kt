package io.github.flexarr.player

import androidx.annotation.OptIn
import androidx.media3.common.util.UnstableApi
import androidx.media3.exoplayer.DefaultLoadControl
import androidx.media3.exoplayer.LoadControl

/**
 * The players start once a second is buffered (ExoPlayer waits for 2.5 s by default), and after a stall wait for the
 * usual amount, so a weak connection doesn't stop and start (docs/fast-start.md).
 */
@OptIn(UnstableApi::class)
object StartBuffer {
    const val START_MS = 1000

    fun loadControl(): LoadControl = DefaultLoadControl.Builder()
        .setBufferDurationsMs(
            DefaultLoadControl.DEFAULT_MIN_BUFFER_MS,
            DefaultLoadControl.DEFAULT_MAX_BUFFER_MS,
            START_MS,
            DefaultLoadControl.DEFAULT_BUFFER_FOR_PLAYBACK_AFTER_REBUFFER_MS,
        )
        .build()
}
