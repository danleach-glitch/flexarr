package io.github.flexarr.player

import android.content.Context
import androidx.media3.common.C
import androidx.media3.common.audio.AudioProcessor.AudioFormat
import androidx.media3.common.audio.BaseAudioProcessor
import androidx.media3.exoplayer.DefaultRenderersFactory
import androidx.media3.exoplayer.RenderersFactory
import androidx.media3.exoplayer.audio.AudioSink
import androidx.media3.exoplayer.audio.DefaultAudioSink
import io.github.flexarr.data.SoundSettings
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.time.LocalTime
import kotlin.math.PI
import kotlin.math.abs
import kotlin.math.cos
import kotlin.math.exp
import kotlin.math.log10
import kotlin.math.max
import kotlin.math.min
import kotlin.math.pow
import kotlin.math.sin

/**
 * Shapes a film's sound for the room as it plays, the way the Apple apps' engine and the web's Web Audio chain do: the
 * dialogue lifted (the centre channel raised, and the speech band of every channel by half as much), the channels folded
 * down to the speakers, and night mode's compression (above -30 dB squeezed 4:1, then lifted 10 dB), with a limiter
 * just under full scale so none of it clips.
 *
 * It keeps the track's channel count, so [shaping] can change mid-film without rebuilding the sink: folding to stereo
 * puts everything in the front left and right and silences the rest, which the phone's own mixdown then leaves alone.
 */
class SoundShaper : BaseAudioProcessor() {
    /** What to do with the sound now; set from the player's thread, read on the audio one. */
    @Volatile var shaping = SoundSettings.Shaping()

    private var channels = 0
    private var rate = 48_000
    private var float = false

    private var layout = Layout.of(0)
    private var frame = FloatArray(0)
    private var out = FloatArray(0)
    // The speech band's filter, one state per channel, remade when its gain changes.
    private var eqDb = 0.0
    private var eq = Biquad.flat
    private var eqState = Array(0) { DoubleArray(4) }
    private var compressorDb = 0.0
    private var limiterGain = 1.0

    override fun onConfigure(inputAudioFormat: AudioFormat): AudioFormat {
        if (inputAudioFormat.encoding != C.ENCODING_PCM_16BIT && inputAudioFormat.encoding != C.ENCODING_PCM_FLOAT) return AudioFormat.NOT_SET
        return inputAudioFormat
    }

    override fun onFlush() {
        val format = inputAudioFormat
        channels = format.channelCount
        rate = format.sampleRate
        float = format.encoding == C.ENCODING_PCM_FLOAT
        layout = Layout.of(channels)
        frame = FloatArray(channels)
        out = FloatArray(channels)
        eqDb = Double.NaN
        eqState = Array(channels) { DoubleArray(4) }
        compressorDb = 0.0
        limiterGain = 1.0
    }

    override fun onReset() {
        channels = 0
    }

    override fun queueInput(inputBuffer: ByteBuffer) {
        val size = inputBuffer.remaining()
        if (size == 0) return
        val output = replaceOutputBuffer(size)
        val now = shaping
        // A speaker setting the track already fits changes nothing, so it's passed through untouched, limiter and all.
        val folds = now.channels != null && now.channels < channels && channels > 2
        if (channels == 0 || (!folds && now.dialogueDb <= 0 && !now.night)) {
            output.put(inputBuffer).flip()
            return
        }
        val input = inputBuffer.order(ByteOrder.nativeOrder())
        val speechDb = now.dialogueDb / 2
        if (speechDb != eqDb) {
            eqDb = speechDb
            eq = if (speechDb > 0) Biquad.peaking(SPEECH_HZ, 0.8, speechDb, rate) else Biquad.flat
        }
        val centreGain = 10.0.pow(now.dialogueDb / 20).toFloat()
        val attack = exp(-1.0 / (0.005 * rate))
        val release = exp(-1.0 / (0.25 * rate))
        val limiterRelease = exp(-1.0 / (0.1 * rate))
        val bytes = if (float) 4 else 2

        while (input.remaining() >= channels * bytes) {
            for (c in 0 until channels) frame[c] = if (float) input.getFloat() else input.getShort() / 32768f
            if (now.dialogueDb > 0) {
                if (layout.centre >= 0) frame[layout.centre] *= centreGain
                for (c in 0 until channels) frame[c] = eq.run(frame[c], eqState[c])
            }
            fold(if (folds) now.channels else null)
            var peak = 0f
            for (c in 0 until channels) peak = max(peak, abs(out[c]))
            var gain = 1.0
            if (now.night) {
                val target = reduction(20 * log10(peak + 1e-9))
                compressorDb = target + (compressorDb - target) * if (target < compressorDb) attack else release
                gain = 10.0.pow((compressorDb + NIGHT_MAKEUP_DB) / 20)
            }
            val needed = min(1.0, LIMIT / max(peak * gain, 1e-9))
            limiterGain = if (needed < limiterGain) needed else needed + (limiterGain - needed) * limiterRelease
            gain *= limiterGain
            for (c in 0 until channels) {
                val v = (out[c] * gain).toFloat().coerceIn(-1f, 1f)
                if (float) output.putFloat(v) else output.putShort((v * 32767).toInt().toShort())
            }
        }
        output.flip()
    }

    /** [frame] folded into [out] for [to] channels (null keeps them all), in place in the track's own layout. */
    private fun fold(to: Int?) {
        frame.copyInto(out)
        if (to == null) return
        var left = 0f
        for (i in layout.left) left += frame[i]
        var right = 0f
        for (i in layout.right) right += frame[i]
        val back = if (layout.backCentre >= 0) frame[layout.backCentre] else 0f
        if (to <= 2) {
            val centre = if (layout.centre >= 0) frame[layout.centre] else 0f
            out.fill(0f)
            out[0] = frame[0] + MINUS_3DB * (centre + left + back)
            out[1] = frame[1] + MINUS_3DB * (centre + right + back)
        } else if (layout.left.size > 1 || layout.backCentre >= 0) {
            // 6.1 and 7.1 to 5.1: the surrounds merge into the 5.1 pair, at channels 4 and 5.
            for (c in 4 until channels) out[c] = 0f
            out[4] = MINUS_3DB * (left + back)
            out[5] = MINUS_3DB * (right + back)
        }
    }

    /** acompressor's soft knee: the gain change, in dB, for a level [db]. */
    private fun reduction(db: Double): Double {
        val over = db - NIGHT_THRESHOLD_DB
        val slope = 1 / NIGHT_RATIO - 1
        return when {
            2 * over < -KNEE_DB -> 0.0
            2 * abs(over) <= KNEE_DB -> slope * (over + KNEE_DB / 2).pow(2) / (2 * KNEE_DB)
            else -> slope * over
        }
    }

    /** Where the centre and the surrounds are in a track of n channels (Android's order: FL FR FC LFE BL BR SL SR); -1 for none. */
    private class Layout(val centre: Int, val left: IntArray, val right: IntArray, val backCentre: Int = -1) {
        companion object {
            private val none = IntArray(0)

            fun of(n: Int) = when (n) {
                3 -> Layout(2, none, none)
                4 -> Layout(-1, intArrayOf(2), intArrayOf(3))
                5 -> Layout(2, intArrayOf(3), intArrayOf(4))
                6 -> Layout(2, intArrayOf(4), intArrayOf(5))
                7 -> Layout(2, intArrayOf(5), intArrayOf(6), backCentre = 4)
                8 -> Layout(2, intArrayOf(4, 6), intArrayOf(5, 7))
                else -> Layout(-1, none, none)
            }
        }
    }

    /** A second-order filter (the Audio EQ Cookbook's), run with its state in s: x1, x2, y1, y2. */
    private class Biquad(val b0: Double, val b1: Double, val b2: Double, val a1: Double, val a2: Double) {
        fun run(x: Float, s: DoubleArray): Float {
            val y = b0 * x + b1 * s[0] + b2 * s[1] - a1 * s[2] - a2 * s[3]
            s[1] = s[0]; s[0] = x.toDouble(); s[3] = s[2]; s[2] = y
            return y.toFloat()
        }

        companion object {
            val flat = Biquad(1.0, 0.0, 0.0, 0.0, 0.0)

            fun peaking(hz: Double, q: Double, db: Double, rate: Int): Biquad {
                val a = 10.0.pow(db / 40)
                val w = 2 * PI * hz / rate
                val alpha = sin(w) / (2 * q)
                val a0 = 1 + alpha / a
                return Biquad((1 + alpha * a) / a0, -2 * cos(w) / a0, (1 - alpha * a) / a0, -2 * cos(w) / a0, (1 - alpha / a) / a0)
            }
        }
    }

    /** Keeps this on the phone's sound settings until the job is cancelled: a change from Settings or the menu arrives straight away, and a scheduled night mode is looked at every minute. [onChange] hears each new setting. */
    fun follow(scope: CoroutineScope, context: Context, onChange: (SoundSettings) -> Unit = {}): Job = scope.launch {
        var settings = SoundSettings.current(context)
        launch {
            SoundSettings.flow(context).collect { now ->
                if (now == null) return@collect
                settings = now
                shaping = now.shaping(LocalTime.now())
                onChange(now)
            }
        }
        while (isActive) {
            delay(60_000)
            shaping = settings.shaping(LocalTime.now())
        }
    }

    /** Renderers whose sound passes through this on its way to the speaker. */
    fun renderers(context: Context): RenderersFactory = object : DefaultRenderersFactory(context) {
        override fun buildAudioSink(context: Context, enableFloatOutput: Boolean, enableAudioTrackPlaybackParams: Boolean): AudioSink =
            DefaultAudioSink.Builder(context)
                .setEnableFloatOutput(enableFloatOutput)
                .setEnableAudioTrackPlaybackParams(enableAudioTrackPlaybackParams)
                .setAudioProcessors(arrayOf(this@SoundShaper))
                .build()
    }

    companion object {
        private const val SPEECH_HZ = 2500.0
        private const val NIGHT_THRESHOLD_DB = -30.0
        private const val NIGHT_RATIO = 4.0
        private const val NIGHT_MAKEUP_DB = 10.0
        private const val KNEE_DB = 6.0
        /** -1 dBFS. */
        private const val LIMIT = 0.891
        private const val MINUS_3DB = 0.7071f
    }
}
