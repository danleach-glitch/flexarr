package io.github.flexarr.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class TeletextTest {
    @Test
    fun `a chosen page goes on the subtitles' address`() {
        val url = "/api/v1/playback/abc/subtitles/e3.vtt"
        assertEquals("$url?page=888", Teletext.url(url, 888))
        assertEquals("$url?hls=true&page=801", Teletext.url("$url?hls=true", 801))
        assertEquals(url, Teletext.url(url, null))
    }

    @Test
    fun `pages can be chosen from servers on 0_55`() {
        assertTrue(Teletext.canChoosePage("0.55.0"))
        assertTrue(Teletext.canChoosePage("0.55.0-dev.1a2b3c4"))
        assertFalse(Teletext.canChoosePage("0.54.0"))
        assertFalse(Teletext.canChoosePage(null))
    }

    @Test
    fun `teletext tracks are dvb teletext`() {
        assertTrue(SubtitleTrack(key = "e3", label = "English", codec = "dvb_teletext", isText = true).isTeletext)
        assertFalse(SubtitleTrack(key = "e4", label = "English", codec = "subrip", isText = true).isTeletext)
        assertEquals(listOf(888, 777, 150, 801), Teletext.pages)
    }
}
