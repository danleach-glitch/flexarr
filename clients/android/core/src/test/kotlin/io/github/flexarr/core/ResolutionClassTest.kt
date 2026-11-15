package io.github.flexarr.core

import kotlinx.serialization.json.Json
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/** A file's Video fact names its picture's class as the server does, so it agrees with the line under the title. */
class ResolutionClassTest {
    @Test
    fun aWideFilmIsTheClassOfItsWidth() {
        assertEquals("1080p", resolutionClass(1920, 818))
        assertEquals("1080p", resolutionClass(1920, 800))
        assertEquals("4K", resolutionClass(3840, 1608))
        assertEquals("720p", resolutionClass(1280, 534))
        assertEquals("SD", resolutionClass(720, 480))
    }

    @Test
    fun heightAloneStillCounts() {
        assertEquals("1080p", resolutionClass(null, 1080))
        assertNull(resolutionClass(null, null))
    }

    @Test
    fun readsTheWidthTheServerSends() {
        val file = Json { ignoreUnknownKeys = true }.decodeFromString<MediaFile>("""{"id":"f","size":1,"videoCodec":"h264","width":1920,"height":818}""")
        assertEquals("1080p", file.resolutionLabel)
    }
}
