package io.github.flexarr.player

import android.app.Activity
import android.app.Application
import android.content.Context
import android.hardware.display.DisplayManager
import android.os.Looper
import android.view.Display
import android.view.Surface
import android.view.SurfaceHolder
import android.view.SurfaceView
import androidx.media3.common.C
import androidx.media3.common.Format
import androidx.media3.common.MimeTypes
import androidx.media3.common.TrackGroup
import androidx.media3.common.Tracks
import io.github.flexarr.TestServer
import mockwebserver3.MockResponse
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.Robolectric
import org.robolectric.RobolectricTestRunner
import org.robolectric.Shadows.shadowOf
import org.robolectric.android.internal.DisplayConfig
import org.robolectric.annotation.Config
import org.robolectric.shadows.ShadowDisplayManager
import org.robolectric.util.ReflectionHelpers
import java.lang.reflect.Proxy
import java.time.Duration

/** The real player and the real matcher on a TV that blanks to switch: what a pause does during the hold, and who's told. */
@RunWith(RobolectricTestRunner::class)
@Config(application = Application::class, sdk = [34], shadows = [MatchContentSetting::class])
class PlaybackHoldTest {
    private val playback = object {}.javaClass.classLoader!!.getResource("playback.json")!!.readText()
    private val server = TestServer { request ->
        when {
            request.method == "POST" && request.url.encodedPath.endsWith("/playback") -> TestServer.json(org.json.JSONObject(playback).put("canSearchSubtitles", true).put("fileId", "file").toString())
            request.url.encodedPath.contains("/progress") -> MockResponse.Builder().code(204).build()
            // The stream: the title never gets past loading, which is all a hold needs.
            request.url.encodedPath.contains("/stream") || request.url.encodedPath.contains("/hls") -> MockResponse.Builder().headersDelay(60, java.util.concurrent.TimeUnit.SECONDS).build()
            else -> null
        }
    }
    private lateinit var controller: PlaybackController
    private lateinit var matcher: FrameRateMatcher
    private lateinit var activity: Activity

    private class ShownSurfaceView(context: Context) : SurfaceView(context) {
        private val surface = object : Surface(android.graphics.SurfaceTexture(0)) {
            override fun isValid() = true
        }
        private val fake = Proxy.newProxyInstance(SurfaceHolder::class.java.classLoader, arrayOf(SurfaceHolder::class.java)) { _, method, _ ->
            if (method.name == "getSurface") surface else null
        } as SurfaceHolder

        override fun getHolder(): SurfaceHolder = fake
    }

    private fun changeDisplay(change: (DisplayConfig) -> Unit) = ReflectionHelpers.callStaticMethod<Unit>(
        ShadowDisplayManager::class.java,
        "changeDisplay",
        ReflectionHelpers.ClassParameter.from(Int::class.javaPrimitiveType, Display.DEFAULT_DISPLAY),
        ReflectionHelpers.ClassParameter.from(java.util.function.Consumer::class.java, java.util.function.Consumer<DisplayConfig> { change(it) }),
    )

    @Before fun aTitleOpensOnATv() {
        activity = Robolectric.buildActivity(Activity::class.java).setup().get()
        fun mode(id: Int, hz: Float) = ReflectionHelpers.callConstructor(
            Display.Mode::class.java,
            ReflectionHelpers.ClassParameter.from(Int::class.javaPrimitiveType, id),
            ReflectionHelpers.ClassParameter.from(Int::class.javaPrimitiveType, 3840),
            ReflectionHelpers.ClassParameter.from(Int::class.javaPrimitiveType, 2160),
            ReflectionHelpers.ClassParameter.from(Float::class.javaPrimitiveType, hz),
        )
        changeDisplay { it.supportedModes = arrayOf(mode(1, 60f), mode(2, 24f)); it.modeId = 1; it.defaultModeId = 1 }
        MatchContentSetting.setting = DisplayManager.MATCH_CONTENT_FRAMERATE_ALWAYS
        controller = PlaybackController(activity, server.model, server.model.client!!, onClose = {})
        controller.start("item")
        server.waitUntil("the session") { controller.info != null }
        matcher = FrameRateMatcher(controller.player, ShownSurfaceView(activity).also(activity::setContentView), controller.screenHolds)
        matcher.start()
        assertTrue("It's asked to play", controller.player.playWhenReady)
        matcher.onTracksChanged(tracks(24f))
        assertTrue("Held for the screen", controller.screenSwitching)
        assertFalse(controller.player.playWhenReady)
        server.requests.clear()
    }

    @After fun close() {
        server.close()
    }

    private fun tracks(rate: Float): Tracks {
        val format = Format.Builder().setSampleMimeType(MimeTypes.VIDEO_H264).setFrameRate(rate).build()
        return Tracks(listOf(Tracks.Group(TrackGroup(format), false, intArrayOf(C.FORMAT_HANDLED), booleanArrayOf(true))))
    }

    private fun pass(millis: Long) = shadowOf(Looper.getMainLooper()).idleFor(Duration.ofMillis(millis))

    private fun reportedPaused(): Boolean {
        server.waitUntil("a report") { server.requests.any { it.url.encodedPath.contains("/progress") } }
        val body = server.requests.first { it.url.encodedPath.contains("/progress") }.body!!.utf8()
        return body.contains("\"paused\":true") || body.contains("\"isPaused\":true")
    }

    @Test fun aPauseMeantDuringTheHold_staysPausedAndIsReported() {
        controller.pause()
        assertFalse(controller.screenSwitching)
        assertTrue(reportedPaused())
        pass(FrameRateMatcher.SWITCH_WAIT_MS * 2)
        assertFalse("Doesn't start by itself", controller.player.playWhenReady)
    }

    @Test fun anotherDevicesPause_goesTheSameWay_soDoesHome() {
        // The activity's onStop and another device's pause both call this.
        controller.pause()
        controller.pause()
        assertTrue(reportedPaused())
        assertEquals("Told once", 1, server.requests.count { it.url.encodedPath.contains("/progress") })
    }

    @Test fun theSurfaceGoingAway_reportsPaused() {
        matcher.surfaceDestroyed(ShownSurfaceView(activity).holder)
        assertFalse(controller.screenSwitching)
        assertTrue(reportedPaused())
        pass(FrameRateMatcher.SWITCH_WAIT_MS * 2)
        assertFalse(controller.player.playWhenReady)
    }

    @Test fun aHoldThatEndsAsUsual_playsOnAndNeverSaysPaused() {
        pass(FrameRateMatcher.SWITCH_WAIT_MS)
        assertTrue(controller.player.playWhenReady)
        assertFalse(controller.screenSwitching)
        server.waitUntil { true }
        assertTrue(server.requests.none { it.url.encodedPath.contains("/progress") && it.body!!.utf8().contains("aused\":true") })
    }

    @Test fun playPressedDuringTheHold_playsOn() {
        controller.player.play()
        assertFalse(controller.screenSwitching)
        assertTrue(controller.player.playWhenReady)
        pass(FrameRateMatcher.SWITCH_WAIT_MS)
        assertTrue(controller.player.playWhenReady)
        assertTrue(server.requests.none { it.url.encodedPath.contains("/progress") && it.body!!.utf8().contains("aused\":true") })
    }

    @Test fun subtitleSearchDuringTheHold_keepsItPaused_andClosingItPlays() {
        controller.searchSubtitles()
        assertFalse(controller.screenSwitching)
        pass(FrameRateMatcher.SWITCH_WAIT_MS * 2)
        assertFalse("Not behind the panel", controller.player.playWhenReady)
        controller.closeSubtitleSearch()
        assertTrue("It was about to play", controller.player.playWhenReady)
    }

    @Test fun closingDuringTheHold_isntReportedAfterItsOver() {
        controller.close()
        server.waitUntil("the stop") { server.requests.any { it.url.encodedPath.contains("stop") || it.method == "DELETE" } }
        server.requests.clear()
        matcher.stop()
        pass(1_000)
        assertTrue(server.requests.none { it.url.encodedPath.contains("/progress") })
    }
}
