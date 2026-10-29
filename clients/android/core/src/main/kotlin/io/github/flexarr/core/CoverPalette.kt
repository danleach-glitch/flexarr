package io.github.flexarr.core

import kotlin.math.abs
import kotlin.math.max
import kotlin.math.min

/**
 * The colours of a cover, for the visualiser: its few strongest hues, each lifted to a level that glows on a dark room.
 * A grey or black-and-white cover has none to speak of, so the track's own glow colour stands in. As the Apple apps'
 * CoverPalette and the web app's palette.ts pick them.
 */
object CoverPalette {
    /** A colour with each channel 0 to 255. */
    data class Rgb(val r: Double, val g: Double, val b: Double)

    /** Up to four colours from [pixels] (ARGB, a small drawing of the cover), filled out with neighbouring hues to [count]. */
    fun colours(pixels: IntArray?, fallback: Rgb, count: Int = 4): List<Rgb> {
        val read = pixels?.let { strongest(it, count) }.orEmpty()
        return fill(read.ifEmpty { listOf(glow(fallback)) }, count)
    }

    fun strongest(pixels: IntArray, count: Int): List<Rgb> {
        val weights = DoubleArray(12)
        val sums = Array(12) { DoubleArray(3) }
        for (pixel in pixels) {
            if ((pixel ushr 24) < 128) continue
            val rgb = Rgb(((pixel shr 16) and 0xFF).toDouble(), ((pixel shr 8) and 0xFF).toDouble(), (pixel and 0xFF).toDouble())
            val (h, s, l) = hsl(rgb)
            // Near-black, near-white and grey say nothing about the cover's colour.
            if (s < 0.18 || l < 0.08 || l > 0.94) continue
            val weight = s * (1 - abs(l - 0.5))
            val bin = (h / 30).toInt() % 12
            weights[bin] += weight
            sums[bin][0] += rgb.r * weight
            sums[bin][1] += rgb.g * weight
            sums[bin][2] += rgb.b * weight
        }
        val total = weights.sum()
        if (total <= 0) return emptyList()
        return weights.indices
            .filter { weights[it] / total > 0.04 }
            .sortedByDescending { weights[it] }
            .take(count)
            .map { glow(Rgb(sums[it][0] / weights[it], sums[it][1] / weights[it], sums[it][2] / weights[it])) }
    }

    /** Brings a colour into the range that reads as light in a dark room: not muddy, not white. */
    fun glow(rgb: Rgb): Rgb {
        val (h, s, l) = hsl(rgb)
        return fromHsl(h, s.coerceIn(0.5, 0.9), l.coerceIn(0.42, 0.66))
    }

    private fun fill(colours: List<Rgb>, count: Int): List<Rgb> {
        val out = colours.toMutableList()
        var i = 0
        while (out.size < count) {
            val (h, s, l) = hsl(colours[i % colours.size])
            val turn = (if (i % 2 == 0) 1 else -1) * (28.0 + 14 * (i / 2))
            out += glow(fromHsl((h + turn + 360) % 360, s, l))
            i++
        }
        return out
    }

    private fun hsl(c: Rgb): Triple<Double, Double, Double> {
        val r = c.r / 255
        val g = c.g / 255
        val b = c.b / 255
        val high = max(r, max(g, b))
        val low = min(r, min(g, b))
        val l = (high + low) / 2
        if (high == low) return Triple(0.0, 0.0, l)
        val d = high - low
        val s = if (l > 0.5) d / (2 - high - low) else d / (high + low)
        val h = when (high) {
            r -> (g - b) / d + (if (g < b) 6 else 0)
            g -> (b - r) / d + 2
            else -> (r - g) / d + 4
        }
        return Triple(h * 60, s, l)
    }

    private fun fromHsl(h: Double, s: Double, l: Double): Rgb {
        val a = s * min(l, 1 - l)
        fun f(n: Double): Double {
            val k = (n + h / 30) % 12
            return (l - a * max(-1.0, min(k - 3, min(9 - k, 1.0)))) * 255
        }
        return Rgb(f(0.0), f(8.0), f(4.0))
    }
}
