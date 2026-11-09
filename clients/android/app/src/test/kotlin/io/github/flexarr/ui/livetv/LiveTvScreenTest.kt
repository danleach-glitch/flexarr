package io.github.flexarr.ui.livetv

import android.app.Application
import android.content.Context
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.semantics.SemanticsActions
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onAllNodesWithText
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performSemanticsAction
import androidx.navigation.compose.rememberNavController
import io.github.flexarr.TestServer
import io.github.flexarr.ui.Navigator
import io.github.flexarr.ui.components.LocalClient
import io.github.flexarr.ui.components.LocalNavigator
import kotlinx.coroutines.launch
import mockwebserver3.MockResponse
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.Shadows.shadowOf
import org.robolectric.annotation.Config
import java.time.Duration
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicReference

/** The phone's Live TV page, its guide showing, on a stand-in server: what a guide that can't be fetched looks like. */
@RunWith(RobolectricTestRunner::class)
@Config(application = Application::class, sdk = [34])
class LiveTvScreenTest {
    @get:Rule val rule = createComposeRule()

    @Volatile private var guideFails = false
    private val guideHeld = AtomicReference<CountDownLatch?>()
    private val server = TestServer { request ->
        val path = request.url.encodedPath
        when {
            path.endsWith("/me") -> TestServer.json("""{"profileId":"main","method":"device","isAdmin":true}""")
            path.endsWith("/livetv/channels") -> TestServer.json("""[{"id":"1","number":1,"name":"Channel 1","group":"News","isFavourite":false},{"id":"2","number":2,"name":"Channel 2","group":"Sport","isFavourite":false}]""")
            path.endsWith("/livetv/status") -> TestServer.json("""{"configured":true,"channels":2,"refreshing":false,"refreshesFinished":$finished}""")
            path.endsWith("/livetv/guide") && guideHeld.get() != null -> {
                guideHeld.get()?.await(20, TimeUnit.SECONDS)
                TestServer.json("""{"title":"Too late"}""", 500)
            }
            path.endsWith("/livetv/guide") && guideFails -> TestServer.json("""{"title":"Guide broke"}""", 500)
            path.endsWith("/livetv/guide") -> TestServer.json(object {}.javaClass.classLoader!!.getResource("live-guide.json")!!.readText())
            path.endsWith("/livetv/refresh") -> MockResponse.Builder().code(202).build().also { finished++ }
            else -> null
        }
    }
    @Volatile private var finished = 4
    private val model get() = server.model
    private var showing by mutableStateOf(true)
    private val failure = "Couldn’t load the guide: Guide broke"

    @After fun close() {
        rule.mainClock.autoAdvance = true
        guideHeld.getAndSet(null)?.countDown()
        server.close()
    }

    private fun show() {
        RuntimeEnvironment.getApplication().getSharedPreferences("settings", Context.MODE_PRIVATE).edit().putBoolean("liveTvGuide", true).commit()
        var signedIn = false
        model.scope.launch {
            model.loadSignIn()
            signedIn = true
        }
        server.waitUntil("the sign-in") { signedIn && model.isAdmin }
        rule.setContent {
            val context = LocalContext.current
            val controller = rememberNavController()
            val navigator = remember { Navigator(controller, model, context) }
            CompositionLocalProvider(LocalNavigator provides navigator, LocalClient provides model.client!!) {
                if (showing) LiveTvScreen(model)
            }
        }
        settle()
    }

    /** The real network and both clocks, until the page has caught up. */
    private fun settle() {
        repeat(30) {
            rule.mainClock.advanceTimeBy(100)
            server.waitUntil { true }
            shadowOf(android.os.Looper.getMainLooper()).idleFor(Duration.ofMillis(50))
            Thread.sleep(10)
            rule.waitForIdle()
        }
    }

    private fun later(minutes: Long) {
        shadowOf(android.os.Looper.getMainLooper()).idleFor(Duration.ofMinutes(minutes))
        rule.mainClock.advanceTimeBy(minutes * 60_000)
        settle()
    }

    /** A slow machine's network may need longer than one [settle]. */
    private fun settleUntilGone(words: String) {
        var tries = 0
        while (tries++ < 20 && rule.onAllNodesWithText(words).fetchSemanticsNodes().isNotEmpty()) settle()
    }

    private fun press(words: String) = rule.onNodeWithText(words).performSemanticsAction(SemanticsActions.OnClick)

    private fun guideShowing() = rule.onAllNodesWithText("No listings").fetchSemanticsNodes().isNotEmpty()

    private val asked get() = server.requestsTo("/livetv/guide").size

    @Test fun aFirstGuideThatFails_saysSo_andTryAgainBringsIt() {
        guideFails = true
        show()
        rule.onNodeWithText(failure).assertExists()
        val before = asked
        press("Try again")
        var tries = 0
        while (tries++ < 20 && rule.onAllNodesWithText(failure).fetchSemanticsNodes().isEmpty()) settle()
        assertEquals(before + 1, asked)
        rule.onNodeWithText(failure).assertExists()

        guideFails = false
        press("Try again")
        // Its button goes at once, for the spinner: it's the guide that's waited for.
        tries = 0
        while (tries++ < 20 && !guideShowing()) settle()
        rule.onNodeWithText(failure).assertDoesNotExist()
        rule.onNodeWithText("Try again").assertDoesNotExist()
        assertTrue(guideShowing())
    }

    @Test fun aGuideThatFails_isStillTriedAgainOnItsOwn() {
        guideFails = true
        show()
        rule.onNodeWithText(failure).assertExists()
        guideFails = false
        later(2)
        settleUntilGone(failure)
        rule.onNodeWithText(failure).assertDoesNotExist()
        assertTrue(guideShowing())
    }

    @Test fun aReloadThatFails_withAGuideShowing_changesNothing() {
        show()
        assertTrue(guideShowing())
        val before = asked
        guideFails = true
        model.refreshLiveTv()
        server.waitUntil("the refresh") { model.liveTvRefreshesEnded == 1 }
        settle()
        assertEquals("The refresh asked for the guide again", before + 1, asked)
        rule.onNodeWithText(failure).assertDoesNotExist()
        rule.onNodeWithText("Try again").assertDoesNotExist()
        assertTrue(guideShowing())
    }

    @Test fun leavingTheScreen_callsOffTheRetry() {
        guideFails = true
        show()
        rule.onNodeWithText(failure).assertExists()
        showing = false
        settle()
        val before = asked
        later(5)
        assertEquals(before, asked)
    }

    @Test fun aFetchCalledOff_saysNothing() {
        guideHeld.set(CountDownLatch(1))
        show()
        // A refresh ending asks again, calling off what was on its way.
        val before = asked
        model.refreshLiveTv()
        server.waitUntil("the refresh") { model.liveTvRefreshesEnded == 1 }
        settle()
        assertEquals(before + 1, asked)
        rule.onNodeWithText("Couldn’t load the guide", substring = true).assertDoesNotExist()
        rule.onNodeWithText("Try again").assertDoesNotExist()
    }
}
