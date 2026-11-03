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

    fun loadControl(): LoadControl = DefaultLoadControl.Builder()
        .setBufferDurationsMs(
            DefaultLoadControl.DEFAULT_MIN_BUFFER_MS,
            DefaultLoadControl.DEFAULT_MAX_BUFFER_MS,
            START_MS,
            AFTER_STALL_MS,
        )
        .build()
}
