package io.github.flexarr.player

import android.app.Application
import androidx.activity.ComponentActivity
import androidx.compose.ui.semantics.SemanticsActions
import androidx.compose.ui.test.assertIsFocused
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.compose.ui.test.onAllNodesWithText
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performSemanticsAction
import io.github.flexarr.TestServer
import io.github.flexarr.ui.tv.TvGuideCache
import org.junit.After
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config
import java.time.Duration

/** Android TV's guide over the player, on a stand-in server, when it can't be fetched. */
@RunWith(RobolectricTestRunner::class)
@Config(application = Application::class, sdk = [34], qualifiers = "w960dp-h540dp")
class TvLivePlayerGuideTest {
    @get:Rule val rule = createAndroidComposeRule<ComponentActivity>()

    @Volatile private var guideFails = true
    private val server = TestServer { request ->
        val path = request.url.encodedPath
        when {
            path.endsWith("/livetv/guide") && guideFails -> TestServer.json("""{"title":"Guide broke"}""", 500)
            path.endsWith("/livetv/guide") -> TestServer.json(object {}.javaClass.classLoader!!.getResource("live-guide.json")!!.readText())
            else -> null
        }
    }
    private lateinit var controller: LiveTvController
    private val failure = "Couldn’t load the guide: Guide broke"

    @Before fun forgetTheGuide() = TvGuideCache.clear()

    @After fun close() {
        rule.runOnUiThread { controller.release() }
        server.close()
    }

    private fun settle() {
        repeat(30) {
            rule.mainClock.advanceTimeBy(100)
            org.robolectric.Shadows.shadowOf(android.os.Looper.getMainLooper()).idleFor(Duration.ofMillis(50))
            Thread.sleep(10)
            rule.waitForIdle()
        }
    }

    private fun settleUntil(done: () -> Boolean) {
        var tries = 0
        while (tries++ < 20 && !done()) settle()
    }

    private fun shows(words: String) = rule.onAllNodesWithText(words).fetchSemanticsNodes().isNotEmpty()

    private fun openGuide() {
        val model = server.model
        rule.runOnUiThread { controller = LiveTvController(rule.activity, model, model.client!!, onClose = {}) }
        rule.setContent { TvLivePlayerScreen(controller, onClose = {}) }
        settle()
        rule.onNodeWithText("Guide").performSemanticsAction(SemanticsActions.OnClick)
        settleUntil { shows(failure) }
    }

    @Test fun aGuideThatFails_saysSo_withTryAgainFocused_andTryAgainBringsIt() {
        openGuide()
        rule.onNodeWithText(failure).assertExists()
        rule.onNodeWithText("Try again").assertIsFocused()
        // Failing again: back to the words, focus on it again.
        rule.onNodeWithText("Try again").performSemanticsAction(SemanticsActions.OnClick)
        settleUntil { server.requestsTo("/livetv/guide").size == 2 && shows("Try again") }
        settle()
        rule.onNodeWithText("Try again").assertIsFocused()

        guideFails = false
        rule.onNodeWithText("Try again").performSemanticsAction(SemanticsActions.OnClick)
        settleUntil { !shows("Try again") && server.requestsTo("/livetv/guide").size == 3 }
        settle()
        rule.onNodeWithText(failure).assertDoesNotExist()
        rule.onNodeWithText("Try again").assertDoesNotExist()
    }

    @Test fun backClosesIt() {
        openGuide()
        rule.onNodeWithText(failure).assertExists()
        rule.runOnUiThread { rule.activity.onBackPressedDispatcher.onBackPressed() }
        settle()
        assertTrue(!shows(failure))
        assertTrue(!shows("Try again"))
    }
}
