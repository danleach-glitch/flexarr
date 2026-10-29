package io.github.flexarr.audio

import androidx.media3.common.C
import androidx.media3.common.util.UnstableApi
import androidx.media3.exoplayer.audio.TeeAudioProcessor
import java.nio.ByteBuffer
import java.nio.ByteOrder
import kotlin.math.PI
import kotlin.math.cos
import kotlin.math.log10
import kotlin.math.max
import kotlin.math.min
import kotlin.math.sin
import kotlin.math.sqrt

/**
 * How loud the bass, middle and treble of the music are as it plays (each 0 to 1, on a decibel scale), for the
 * visualiser. The player hands each stretch of sound to [handleBuffer] as it goes to the speaker; the newest 1024
 * samples go through an FFT on that same audio thread and the bands are left for the screen to read when it next
 * draws. As the Apple apps' AudioSpectrum, but every stream can be heard here, converted ones included.
 */
@UnstableApi
class AudioSpectrum : TeeAudioProcessor.AudioBufferSink {
    data class Bands(val bass: Float, val mid: Float, val high: Float)

    /** The bands of what was played last; null before anything has played (or after a stop). */
    @Volatile
    var bands: Bands? = null
        private set

    private var sampleRate = 44_100
    private var channels = 2
    private var encoding = C.ENCODING_PCM_16BIT
    private val samples = FloatArray(SIZE)
    private val real = DoubleArray(SIZE)
    private val imaginary = DoubleArray(SIZE)
    private val window = DoubleArray(SIZE) { 0.5 * (1 - cos(2 * PI * it / SIZE)) }
    /** Frames since the bands were last worked out: about thirty times a second is plenty for the screen. */
    private var sinceLast = 0

    override fun flush(sampleRateHz: Int, channelCount: Int, encoding: Int) {
        sampleRate = sampleRateHz
        channels = channelCount.coerceAtLeast(1)
        this.encoding = encoding
        samples.fill(0f)
        sinceLast = 0
    }

    override fun handleBuffer(buffer: ByteBuffer) {
        // Read without moving the player's own buffer along.
        val bytes = buffer.duplicate().order(ByteOrder.nativeOrder())
        val frames = when (encoding) {
            C.ENCODING_PCM_16BIT -> bytes.remaining() / (2 * channels)
            C.ENCODING_PCM_FLOAT -> bytes.remaining() / (4 * channels)
            else -> return // Anything else is left alone rather than misread.
        }
        if (frames <= 0) return
        // The newest SIZE samples of the first channel (enough for the bands), the older ones moved along.
        val fresh = min(frames, SIZE)
        val kept = SIZE - fresh
        if (kept > 0) System.arraycopy(samples, fresh, samples, 0, kept)
        val start = bytes.position()
        for (i in 0 until fresh) {
            val frame = frames - fresh + i
            samples[kept + i] = when (encoding) {
                C.ENCODING_PCM_16BIT -> bytes.getShort(start + frame * 2 * channels) / 32_768f
                else -> bytes.getFloat(start + frame * 4 * channels)
            }
        }
        sinceLast += frames
        if (sinceLast < sampleRate / 30) return
        sinceLast = 0
        analyse()
    }

    /** Nothing is playing: the visualiser drifts on its own. */
    fun reset() {
        bands = null
    }

    private fun analyse() {
        for (i in 0 until SIZE) {
            real[i] = samples[i] * window[i]
            imaginary[i] = 0.0
        }
        fft(real, imaginary)
        val hzPerBin = sampleRate.toDouble() / SIZE
        fun band(low: Double, high: Double): Float {
            val from = max(1, (low / hzPerBin).toInt())
            val to = min(SIZE / 2 - 1, Math.ceil(high / hzPerBin).toInt())
            if (to < from) return 0f
            var sum = 0f
            for (i in from..to) {
                // As a browser's analyser scales it: -100 dB is nothing, -30 dB is full. (Twice the plain transform,
                // as the Apple apps' real FFT gives it.)
                val magnitude = 2 * sqrt(real[i] * real[i] + imaginary[i] * imaginary[i]) / SIZE
                val db = 20 * log10(max(magnitude, 1e-9))
                sum += ((db + 100) / 70).toFloat().coerceIn(0f, 1f)
            }
            return sum / (to - from + 1)
        }
        bands = Bands(bass = band(30.0, 160.0), mid = band(300.0, 2000.0), high = band(3000.0, 10000.0))    }

    private companion object {
        const val SIZE = 1024

        /** An in-place radix-2 FFT. */
        fun fft(re: DoubleArray, im: DoubleArray) {
            val n = re.size
            var j = 0
            for (i in 1 until n) {
                var bit = n shr 1
                while (j and bit != 0) {
                    j = j xor bit
                    bit = bit shr 1
                }
                j = j xor bit
                if (i < j) {
                    re[i] = re[j].also { re[j] = re[i] }
                    im[i] = im[j].also { im[j] = im[i] }
                }
            }
            var length = 2
            while (length <= n) {
                val angle = -2 * PI / length
                val wRe = cos(angle)
                val wIm = sin(angle)
                var i = 0
                while (i < n) {
                    var curRe = 1.0
                    var curIm = 0.0
                    for (k in 0 until length / 2) {
                        val a = i + k
                        val b = a + length / 2
                        val tRe = re[b] * curRe - im[b] * curIm
                        val tIm = re[b] * curIm + im[b] * curRe
                        re[b] = re[a] - tRe
                        im[b] = im[a] - tIm
                        re[a] += tRe
                        im[a] += tIm
                        val nextRe = curRe * wRe - curIm * wIm
                        curIm = curRe * wIm + curIm * wRe
                        curRe = nextRe
                    }
                    i += length
                }
                length = length shl 1
            }
        }
    }
}
