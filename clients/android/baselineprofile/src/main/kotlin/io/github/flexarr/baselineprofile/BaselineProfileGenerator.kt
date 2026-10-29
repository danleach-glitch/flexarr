package io.github.flexarr.baselineprofile

import android.content.ComponentName
import android.content.Intent
import androidx.benchmark.macro.junit4.BaselineProfileRule
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import androidx.test.uiautomator.By
import androidx.test.uiautomator.Direction
import androidx.test.uiautomator.Until
import org.junit.Assume.assumeTrue
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import java.util.regex.Pattern

/**
 * Records the baseline profile: start the app signed in, scroll Home, open Libraries and a library's grid and scroll
 * it, open a title, play it, scrub and close the player, then play a podcast episode and open Now Playing. Run on a
 * device or emulator with a real server (nothing is recorded there: no history):
 *
 * `./gradlew :app:generateBaselineProfile -Pandroid.testInstrumentationRunnerArguments.server=<url>
 * -Pandroid.testInstrumentationRunnerArguments.apiKey=<key> -Pandroid.testInstrumentationRunnerArguments.profile=<id>`
 *
 * The profile lands in app/src/release/generated/baselineProfiles and is committed; make it again after big UI changes.
 */
@RunWith(AndroidJUnit4::class)
class BaselineProfileGenerator {
    @get:Rule
    val rule = BaselineProfileRule()

    @Test
    fun generate() {
        val arguments = InstrumentationRegistry.getArguments()
        val server = arguments.getString("server")
        val key = arguments.getString("apiKey")
        assumeTrue("No server given", server != null && key != null)
        rule.collect(packageName = PACKAGE, includeInStartupProfile = true) {
            pressHome()
            startActivityAndWait(
                Intent(Intent.ACTION_MAIN)
                    .setComponent(ComponentName(PACKAGE, "io.github.flexarr.MainActivity"))
                    .putExtra("server", server)
                    .putExtra("apiKey", key)
                    .putExtra("noHistory", true)
                    .apply { arguments.getString("profile")?.let { putExtra("profile", it) } },
            )
            // Home: the featured titles, then its rows.
            device.wait(Until.hasObject(By.text("Libraries")), 15_000)
            device.waitForIdle()
            repeat(3) { scroll(Direction.DOWN) }
            repeat(3) { scroll(Direction.UP) }

            // Libraries, then the first one's grid.
            device.findObject(By.text("Libraries"))?.click()
            device.wait(Until.hasObject(By.text("Movies")), 10_000)
            (device.findObject(By.text("Movies")) ?: device.findObject(By.text("TV Shows")))?.click()
            device.waitForIdle()
            Thread.sleep(2_000)
            repeat(4) { scroll(Direction.DOWN) }
            repeat(4) { scroll(Direction.UP) }

            // The first title's page, then the player: show the controls, scrub along the bar, and close it.
            device.click(device.displayWidth / 4, device.displayHeight / 3)
            device.wait(Until.hasObject(By.text(PLAY)), 10_000)
            device.findObject(By.text(PLAY))?.let { play ->
                play.click()
                Thread.sleep(6_000)
                device.click(device.displayWidth / 2, device.displayHeight / 2)
                device.waitForIdle()
                device.swipe(device.displayWidth * 3 / 10, device.displayHeight * 9 / 10, device.displayWidth * 6 / 10, device.displayHeight * 9 / 10, 40)
                Thread.sleep(2_000)
                device.pressBack()
                device.wait(Until.hasObject(By.text(PLAY)), 10_000)
            }

            // Podcasts: play an episode, then Now Playing from the bar above the tabs.
            device.pressBack()
            device.findObject(By.text("Libraries"))?.click()
            device.wait(Until.hasObject(By.text("Podcasts")), 10_000)
            device.findObject(By.text("Podcasts"))?.click()
            device.wait(Until.hasObject(By.descStartsWith("Play ")), 10_000)
            device.findObject(By.descStartsWith("Play "))?.let { episode ->
                episode.click()
                device.wait(Until.hasObject(By.desc("Stop and close")), 10_000)
                device.findObject(By.desc("Stop and close"))?.let { close ->
                    val bar = close.visibleBounds
                    device.click(bar.right + device.displayWidth / 4, bar.centerY())
                    Thread.sleep(2_000)
                    repeat(2) { scroll(Direction.DOWN) }
                    device.pressBack()
                    device.waitForIdle()
                    device.findObject(By.desc("Stop and close"))?.click()
                }
            }
        }
    }

    private fun androidx.benchmark.macro.MacrobenchmarkScope.scroll(direction: Direction) {
        val width = device.displayWidth
        val height = device.displayHeight
        val (from, to) = if (direction == Direction.DOWN) height * 3 / 4 to height / 4 else height / 4 to height * 3 / 4
        device.swipe(width / 2, from, width / 2, to, 20)
        device.waitForIdle()
    }

    private companion object {
        const val PACKAGE = "com.danleach.flexarr"

        /** A title page's play button: "Play", or "Resume from 12:34". */
        val PLAY: Pattern = Pattern.compile("Play|Resume from .*")
    }
}
