package io.github.flexarr.ui.music

import android.provider.Settings
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.runtime.withFrameNanos
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.BlendMode
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.drawscope.DrawScope
import androidx.compose.ui.platform.LocalContext
import io.github.flexarr.audio.AudioSpectrum
import io.github.flexarr.core.CoverPalette
import kotlin.math.cos
import kotlin.math.exp
import kotlin.math.max
import kotlin.math.min
import kotlin.math.sin

/**
 * Light in the cover's colours, drifting behind Now Playing. Large soft fields wander on slow, never-repeating paths;
 * the bass makes them swell, a beat makes them flare, the middle hurries them along and the treble brings out small
 * bright glints. With nothing to hear (paused) they drift on their own. The Apple TV's MusicAurora and the web app's
 * Aurora.tsx draw the same way.
 */
@androidx.annotation.OptIn(androidx.media3.common.util.UnstableApi::class)
@Composable
fun MusicAurora(colours: List<CoverPalette.Rgb>, spectrum: AudioSpectrum, playing: Boolean, modifier: Modifier = Modifier) {
    val motion = remember { AuroraMotion() }
    // Animations off in the system's settings: the light holds still.
    val context = LocalContext.current
    val still = remember { Settings.Global.getFloat(context.contentResolver, Settings.Global.ANIMATOR_DURATION_SCALE, 1f) == 0f }
    var frameTime by remember { mutableLongStateOf(0L) }
    LaunchedEffect(still) {
        if (still) return@LaunchedEffect
        var last = 0L
        while (true) {
            withFrameNanos { now ->
                // About 30 times a second is plenty for light this soft.
                if (now - last >= 33_000_000L) {
                    last = now
                    frameTime = now
                }
            }
        }
    }
    Box(modifier.fillMaxSize()) {
        Canvas(Modifier.fillMaxSize()) {
            val frame = motion.step(frameTime, if (playing) spectrum.bands else null, playing && !still)
            draw(frame, colours)
        }
        // Keeps the words and controls legible on the brightest light.
        Box(Modifier.fillMaxSize().background(Brush.radialGradient(listOf(Color.Transparent, Color.Black.copy(alpha = 0.55f)))))
        Box(Modifier.fillMaxSize().background(Brush.verticalGradient(0f to Color.Black.copy(alpha = 0.15f), 0.4f to Color.Transparent, 1f to Color.Black.copy(alpha = 0.35f))))
        Box(Modifier.fillMaxSize().background(Color.Black.copy(alpha = 0.24f)))
    }
}

private fun CoverPalette.Rgb.color(alpha: Double) =
    Color((r / 255).toFloat().coerceIn(0f, 1f), (g / 255).toFloat().coerceIn(0f, 1f), (b / 255).toFloat().coerceIn(0f, 1f), alpha.toFloat().coerceIn(0f, 1f))

private fun DrawScope.draw(frame: AuroraMotion.Frame, colours: List<CoverPalette.Rgb>) {
    val base = colours.firstOrNull() ?: CoverPalette.Rgb(120.0, 100.0, 170.0)
    drawRect(CoverPalette.Rgb(base.r * 0.08 + 6, base.g * 0.08 + 5, base.b * 0.08 + 10).color(1.0))
    val w = size.width.toDouble()
    val h = size.height.toDouble()
    val span = max(w, h)
    val clock = frame.clock

    // Five large fields, each on its own Lissajous path, the cover's colours shared out between them.
    for (i in 0 until 5) {
        val c = if (colours.isEmpty()) base else colours[i % colours.size]
        val n = i.toDouble()
        val t = clock * (0.55 + n * 0.13) + n * 1.7
        val x = w * (0.5 + 0.42 * sin(t * 0.71 + n) * cos(t * 0.23))
        val y = h * (0.5 + 0.38 * cos(t * 0.53 + n * 2.1) * sin(t * 0.31 + 0.6))
        // Low fields (the first two) answer the bass; the rest the middle.
        val drive = if (i < 2) frame.bass else frame.mid
        val radius = span * (0.36 + 0.06 * sin(t * 0.9)) * (1 + 0.22 * drive + 0.18 * frame.pulse)
        val alpha = 0.28 + 0.2 * drive + 0.16 * frame.pulse
        field(x, y, radius, arrayOf(0f to c.color(alpha), 0.55f to c.color(alpha * 0.35), 1f to c.color(0.0)))
    }

    // Glints: small, quicker, nearly white versions of the colours, there only while the treble is.
    if (frame.high <= 0.05) return
    for (i in 0 until 4) {
        val c = if (colours.isEmpty()) base else colours[(i + 1) % colours.size]
        val n = i.toDouble()
        val t = clock * (1.6 + n * 0.37) + n * 2.3
        val x = w * (0.5 + 0.45 * sin(t * 0.83 + n * 1.3))
        val y = h * (0.5 + 0.4 * sin(t * 0.61 + n * 0.7) * cos(t * 0.29))
        val alpha = 0.5 * frame.high * (0.6 + 0.4 * sin(t * 3.1 + n))
        val bright = CoverPalette.Rgb((c.r + 510) / 3, (c.g + 510) / 3, (c.b + 510) / 3)
        field(x, y, span * (0.09 + 0.05 * frame.high), arrayOf(0f to bright.color(alpha), 1f to c.color(0.0)))
    }
}

private fun DrawScope.field(x: Double, y: Double, radius: Double, stops: Array<Pair<Float, Color>>) {
    if (radius <= 0) return
    drawRect(Brush.radialGradient(*stops, center = Offset(x.toFloat(), y.toFloat()), radius = radius.toFloat()), blendMode = BlendMode.Screen)
}

/**
 * The light's state from frame to frame: its clock, how fast it's going, and the music's levels, smoothed. Each band
 * keeps a slowly sinking peak to measure against, so a quiet recording moves as much as a loud one; levels rise
 * quickly and fall slowly, so the light breathes rather than flickers.
 */
@androidx.annotation.OptIn(androidx.media3.common.util.UnstableApi::class)
private class AuroraMotion {
    data class Frame(val clock: Double, val bass: Double, val mid: Double, val high: Double, val pulse: Double)

    private var last: Long? = null
    private var clock = 0.0
    private var pace = 0.02
    private val peak = doubleArrayOf(0.2, 0.2, 0.2)
    private val level = doubleArrayOf(0.0, 0.0, 0.0)
    private var slowBass = 0.0
    private var pulse = 0.0

    fun step(nanos: Long, bands: AudioSpectrum.Bands?, playing: Boolean): Frame {
        // A long gap (the screen was away) counts as one ordinary frame, so nothing leaps when it comes back.
        val dt = min(0.1, max(0.0, (nanos - (last ?: nanos)) / 1e9))
        last = nanos
        fun ease(from: Double, to: Double, rate: Double) = from + (to - from) * (1 - exp(-rate * dt))
        fun follow(raw: Double, band: Int) {
            peak[band] = max(raw, max(0.08, peak[band] * exp(-dt * 0.09)))
            val target = min(1.0, raw / peak[band])
            level[band] = ease(level[band], target, if (target > level[band]) 14.0 else 2.5)
        }
        follow((bands?.bass ?: 0f).toDouble(), 0)
        follow((bands?.mid ?: 0f).toDouble(), 1)
        follow((bands?.high ?: 0f).toDouble(), 2)
        slowBass = ease(slowBass, level[0], 1.2)
        val kick = min(1.0, max(0.0, level[0] - slowBass) * 2.4)
        pulse = ease(pulse, kick, if (kick > pulse) 20.0 else 3.0)
        // Playing, it drifts and the middle quickens it; paused, it slows almost to a stop.
        val target = if (playing) 0.05 + 0.12 * level[1] + 0.05 * pulse else 0.008
        pace = ease(pace, target, 1.5)
        clock += pace * dt * 4
        return Frame(clock, level[0], level[1], level[2], pulse)
    }
}
