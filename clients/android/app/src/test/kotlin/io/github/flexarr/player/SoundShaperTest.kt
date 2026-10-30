package io.github.flexarr.player

import androidx.media3.common.C
import androidx.media3.common.audio.AudioProcessor.AudioFormat
import io.github.flexarr.data.SoundSettings
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.time.LocalTime

/** The shaper against the web's and the Apple apps' numbers: dialogue lift, fold-down, night mode. */
class SoundShaperTest {
    private fun run(shaping: SoundSettings.Shaping, channels: Int, frames: List<List<Float>>): List<List<Float>> {
        val shaper = SoundShaper()
        shaper.shaping = shaping
        shaper.configure(AudioFormat(48_000, channels, C.ENCODING_PCM_16BIT))
        shaper.flush()
        val input = ByteBuffer.allocateDirect(frames.size * channels * 2).order(ByteOrder.nativeOrder())
        frames.flatten().forEach { input.putShort((it * 32767).toInt().toShort()) }
        input.flip()
        shaper.queueInput(input)
        val output = shaper.output.order(ByteOrder.nativeOrder())
        return List(frames.size) { List(channels) { output.getShort() / 32767f } }
    }

    @Test
    fun `leaves the sound alone when nothing is on`() {
        val frame = listOf(0.5f, -0.25f)
        assertEquals(frame[0], run(SoundSettings.Shaping(), 2, listOf(frame))[0][0], 1e-4f)
    }

    @Test
    fun `folds 5_1 to the front pair with the centre at -3 dB`() {
        val out = run(SoundSettings.Shaping(channels = 2), 6, listOf(listOf(0.1f, 0.1f, 0.4f, 0.9f, 0f, 0f)))[0]
        assertEquals(0.1f + 0.7071f * 0.4f, out[0], 1e-3f)
        assertEquals(0f, out[2], 1e-4f) // The centre and the LFE are silent once folded.
        assertEquals(0f, out[3], 1e-4f)
    }

    @Test
    fun `lifts the centre by the boost`() {
        val out = run(SoundSettings.Shaping(dialogueDb = 6.0), 6, List(2_000) { listOf(0f, 0f, 0.1f, 0f, 0f, 0f) })
        assertEquals(0.1995f, out.last()[2], 0.01f) // +6 dB, the speech band's filter being flat at a constant level.
    }

    @Test
    fun `night mode brings loud and quiet closer, and nothing clips`() {
        val loud = run(SoundSettings.Shaping(night = true), 2, List(4_800) { listOf(1f, -1f) }).last()[0]
        val quiet = run(SoundSettings.Shaping(night = true), 2, List(4_800) { listOf(0.01f, -0.01f) }).last()[0]
        assertTrue("loud $loud", loud <= 0.892f)
        assertTrue("quiet $quiet", quiet > 0.03f) // -40 dB lifted 10.
    }

    @Test
    fun `a scheduled night mode runs past midnight`() {
        val s = SoundSettings(nightMode = SoundSettings.NightMode.Scheduled)
        assertTrue(s.shaping(LocalTime.of(23, 0)).night)
        assertTrue(s.shaping(LocalTime.of(3, 0)).night)
        assertTrue(!s.shaping(LocalTime.of(7, 0)).night)
    }
}
