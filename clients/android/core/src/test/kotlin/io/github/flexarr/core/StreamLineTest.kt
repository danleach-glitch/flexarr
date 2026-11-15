package io.github.flexarr.core

import kotlinx.serialization.json.Json
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class StreamLineTest {
    private fun info(summary: String? = null, stream: StreamSummary? = null) = PlaybackInfo(
        sessionId = "s", method = PlaybackMethod.Transcode, url = "/stream", durationSeconds = 60.0, startPositionSeconds = 0.0,
        item = ItemSummary(id = "ep", libraryId = "tv", kind = MediaKind.Episode, title = "Pilot", childCount = 0, artwork = Artwork(), userData = UserData(0.0, false, 0)),
        transcodeSummary = summary, stream = stream,
    )

    @Test
    fun `a file played as it is has no line`() {
        assertNull(streamLine(info()))
    }

    @Test
    fun `an older server's English summary is used as it is`() {
        assertEquals("Original H.264, repackaged", streamLine(info("Original H.264, repackaged")))
        assertEquals("Converting: 720p H.264 at 4 Mbps", streamLine(info("720p H.264 at 4 Mbps")))
    }

    @Test
    fun `a copied picture is named with its range`() {
        val stream = StreamSummary(videoCopied = true, videoCodec = "hevc", dynamicRange = "DolbyVision", hdr10Plus = true)
        assertEquals("Original HEVC (Dolby Vision, HDR10+), repackaged", streamLine(info("ignored", stream)))
        assertEquals("Original H.264, repackaged", streamLine(info(stream = StreamSummary(videoCopied = true, videoCodec = "h264", dynamicRange = "SDR"))))
    }

    @Test
    fun `a converted picture says its size, rate and tone mapping, and lossless sound its channels`() {
        val stream = StreamSummary(height = 1080, videoBitrate = 8_000_000, toneMapped = true, losslessAudio = true, audioChannels = 8)
        assertEquals("Converting: 1080p H.264 at 8 Mbps, HDR converted to SDR, lossless FLAC 7.1 sound", streamLine(info(stream = stream)))
        assertEquals("Converting: 720p H.264 at 3.5 Mbps", streamLine(info(stream = StreamSummary(height = 720, videoBitrate = 3_500_000))))
    }

    @Test
    fun `reads the server's stream field`() {
        val stream = Json { ignoreUnknownKeys = true }.decodeFromString(StreamSummary.serializer(),
            """{"videoCopied":false,"videoCodec":"h264","dynamicRange":null,"hdr10Plus":false,"height":1080,"videoBitrate":8000000,"toneMapped":false,"losslessAudio":false,"audioChannels":null}""")
        assertEquals(StreamSummary(height = 1080, videoBitrate = 8_000_000), stream)
    }
}
