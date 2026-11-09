package io.github.flexarr.ui.tv

import android.app.Application
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.remember
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.semantics.SemanticsActions
import androidx.compose.ui.input.key.Key
import androidx.compose.ui.test.assertIsFocused
import androidx.compose.ui.test.getUnclippedBoundsInRoot
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onAllNodesWithText
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performKeyInput
import androidx.compose.ui.test.performSemanticsAction
import androidx.compose.ui.test.pressKey
import androidx.navigation.compose.rememberNavController
import io.github.flexarr.TestServer
import io.github.flexarr.core.i18n.Words
import io.github.flexarr.ui.Navigator
import io.github.flexarr.ui.components.LocalClient
import io.github.flexarr.ui.components.LocalNavigator
import kotlinx.coroutines.launch
import mockwebserver3.MockResponse
import mockwebserver3.SocketEffect
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Before
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

/** Android TV's Live TV page itself, on a stand-in server: where focus lands, a group that goes, and where a refresh says how it went. */
@RunWith(RobolectricTestRunner::class)
@Config(application = Application::class, sdk = [34], qualifiers = "w960dp-h540dp")
class TvLiveTvScreenTest {
    @get:Rule val rule = createComposeRule()

    private fun channel(id: Int, group: String, favourite: Boolean = false) =
        """{"id":"$id","number":$id,"name":"Channel $id","group":"$group","isFavourite":$favourite}"""

    @Volatile private var channels = listOf(channel(1, "News"), channel(2, "Sport"))
    @Volatile private var finished = 4
    @Volatile private var extra = ""
    @Volatile private var unreachable = false
    @Volatile private var refusal: MockResponse? = null
    @Volatile private var guideFails = false
    private val guideHeld = java.util.concurrent.atomic.AtomicReference<java.util.concurrent.CountDownLatch?>()
    private val server = TestServer { request ->
        val path = request.url.encodedPath
        when {
            path.endsWith("/me") -> TestServer.json("""{"profileId":"main","method":"device","isAdmin":true}""")
            path.endsWith("/livetv/channels") -> TestServer.json(channels.joinToString(",", "[", "]"))
            path.endsWith("/livetv/status") && unreachable -> MockResponse.Builder().onResponseStart(SocketEffect.CloseSocket()).build()
            path.endsWith("/livetv/status") -> TestServer.json("""{"configured":true,"channels":2,"refreshing":false,"refreshesFinished":$finished,"channelsWithoutListings":3$extra}""")
            path.endsWith("/livetv/guide") && guideHeld.get() != null -> {
                guideHeld.get()?.await(20, java.util.concurrent.TimeUnit.SECONDS)
                TestServer.json("""{"title":"Too late"}""", 500)
            }
            path.endsWith("/livetv/guide") && guideFails -> TestServer.json("""{"title":"Guide broke"}""", 500)
            path.endsWith("/livetv/guide") -> TestServer.json(object {}.javaClass.classLoader!!.getResource("live-guide.json")!!.readText())
            path.endsWith("/livetv/refresh") && refusal != null -> refusal
            path.endsWith("/livetv/refresh") -> MockResponse.Builder().code(202).build().also { finished++ }
            else -> null
        }
    }
    private val model get() = server.model

    @Before fun forgetTheGuide() = TvGuideCache.clear()

    @After fun close() {
        rule.mainClock.autoAdvance = true
        guideHeld.getAndSet(null)?.countDown()
        server.close()
    }

    private fun show(takeFocus: Boolean = true, pump: () -> Unit = {}) {
        var signedIn = false
        model.scope.launch {
            model.loadSignIn()
            model.reloadLiveTvStatus()
            signedIn = true
        }
        server.waitUntil("the sign-in") { signedIn && model.isAdmin }
        rule.setContent {
            val context = LocalContext.current
            val controller = rememberNavController()
            val navigator = remember { Navigator(controller, model, context) }
            CompositionLocalProvider(LocalNavigator provides navigator, LocalClient provides model.client!!) {
                TvLiveTvScreen(model, takeFocus = takeFocus, onFocusTaken = {})
            }
        }
        pump()
        settle()
    }

    /** The real network and both clocks, until the page has caught up. */
    private fun settle() {
        repeat(30) {
            rule.mainClock.advanceTimeBy(100)
            server.waitUntil { true }
            org.robolectric.Shadows.shadowOf(android.os.Looper.getMainLooper()).idleFor(java.time.Duration.ofMillis(50))
            Thread.sleep(10)
            rule.waitForIdle()
        }
    }

    /** A slow machine's network may need longer than one [settle]. */
    private fun settleUntilGone(words: String) {
        var tries = 0
        while (tries++ < 20 && rule.onAllNodesWithText(words).fetchSemanticsNodes().isNotEmpty()) settle()
    }

    /** Focus into the list (where it comes back to the row it left) and on to [category], as the remote does it. */
    private fun moveTo(category: String) {
        rule.onNodeWithText("All channels").performSemanticsAction(SemanticsActions.RequestFocus)
        rule.onNodeWithText(category).performSemanticsAction(SemanticsActions.RequestFocus)
        settle()
    }

    private fun press(words: String) = rule.onNodeWithText(words).performSemanticsAction(SemanticsActions.OnClick)

    private fun refresh() {
        val ended = model.liveTvRefreshesEnded
        press(Words.refreshGuide)
        server.waitUntil("the refresh") { model.liveTvRefreshesEnded == ended + 1 }
        settle()
    }

    /** Long enough for a problem to have been read, and the page's next look at the status. */
    private fun nextLook() {
        org.robolectric.Shadows.shadowOf(android.os.Looper.getMainLooper()).idleFor(java.time.Duration.ofSeconds(31))
        rule.mainClock.advanceTimeBy(60_000)
        settle()
    }

    private val cantReach = "Couldn’t reach the server. Make sure Flexarr is running and this device is on the same network."

    @Test fun aPressThatNeverGotThere_staysThroughAFailedLook_andGoesWithAGoodOneThatFindsNothingChanged() {
        show()
        unreachable = true
        refresh()
        rule.onNodeWithText(cantReach).assertExists()
        nextLook()
        rule.onNodeWithText(cantReach).assertExists()
        unreachable = false
        nextLook()
        rule.onNodeWithText(cantReach).assertDoesNotExist()
    }

    @Test fun aGoodLookStraightAfter_leavesTheProblemToBeRead() {
        show()
        unreachable = true
        refresh()
        unreachable = false
        rule.mainClock.advanceTimeBy(60_000)
        settle()
        rule.onNodeWithText(cantReach).assertExists()
        nextLook()
        rule.onNodeWithText(cantReach).assertDoesNotExist()
    }

    @Test fun aRefusedPress_staysThroughGoodLooks_untilTheNextPress() {
        show()
        refusal = TestServer.json("""{"title":"Unlock first"}""", 403)
        refresh()
        rule.onNodeWithText("Unlock first").assertExists()
        nextLook()
        rule.onNodeWithText("Unlock first").assertExists()
        // A scheduled refresh has been through: the status moves, and says nothing about the press.
        finished++
        nextLook()
        rule.onNodeWithText("Unlock first").assertExists()
        refusal = null
        refresh()
        rule.onNodeWithText("Unlock first").assertDoesNotExist()
        rule.onNodeWithText(Words.guideRefreshed).assertExists()
    }

    private val guideFailure = "Couldn’t load the guide: Guide broke"

    @Test fun aFirstGuideThatFails_saysSo_withTryAgainFocused_andTryAgainBringsIt() {
        guideFails = true
        show()
        rule.onNodeWithText(guideFailure).assertExists()
        rule.onNodeWithText("Try again").assertIsFocused()
        // Still failing: still there, and still focused.
        val asked = server.requestsTo("/livetv/guide").size
        press("Try again")
        settle()
        assertEquals(asked + 1, server.requestsTo("/livetv/guide").size)
        rule.onNodeWithText(guideFailure).assertExists()
        rule.onNodeWithText("Try again").assertIsFocused()

        guideFails = false
        press("Try again")
        settleUntilGone(guideFailure)
        settle()
        rule.onNodeWithText(guideFailure).assertDoesNotExist()
        rule.onNodeWithText("Try again").assertDoesNotExist()
        rule.onNodeWithText("Channel 1").assertExists()
        // Its button gone, focus goes back to the category shown rather than anywhere.
        rule.onNodeWithText("All channels").assertIsFocused()
    }

    @Test fun aCategorysGuideFailing_leavesFocusOnTheCategory_andRightReachesTryAgain() {
        guideFails = true
        show()
        rule.onNodeWithText("Try again").assertIsFocused()
        moveTo("News")
        rule.onNodeWithText(guideFailure).assertExists()
        rule.onNodeWithText("News").assertIsFocused()
        moveTo("Sport")
        rule.onNodeWithText("Sport").assertIsFocused()
        rule.onNodeWithText("Sport").performKeyInput { pressKey(Key.DirectionRight) }
        settle()
        rule.onNodeWithText("Try again").assertIsFocused()
    }

    @Test fun tryAgainThatFails_showsTheSpinnerWhileItTries_thenTheFailureWithFocusOnTryAgain() {
        guideFails = true
        show()
        moveTo("News")
        rule.onNodeWithText("News").assertIsFocused()
        val latch = java.util.concurrent.CountDownLatch(1)
        guideHeld.set(latch)
        press("Try again")
        settle()
        rule.onNodeWithText(guideFailure).assertDoesNotExist()
        rule.onNodeWithText("Try again").assertDoesNotExist()
        guideHeld.set(null)
        guideFails = false
        latch.countDown()
        var tries = 0
        while (tries++ < 20 && rule.onAllNodesWithText("Try again").fetchSemanticsNodes().isEmpty()) settle()
        rule.onNodeWithText("Couldn’t load the guide: Too late").assertExists()
        rule.onNodeWithText("Try again").assertIsFocused()
    }

    @Test fun aGuideThatFails_thenComesAtTheNextLook_replacesTheWords() {
        guideFails = true
        show()
        rule.onNodeWithText(guideFailure).assertExists()
        guideFails = false
        org.robolectric.Shadows.shadowOf(android.os.Looper.getMainLooper()).idleFor(java.time.Duration.ofMinutes(5))
        rule.mainClock.advanceTimeBy(300_000)
        settleUntilGone(guideFailure)
        rule.onNodeWithText(guideFailure).assertDoesNotExist()
        rule.onNodeWithText("Channel 1").assertExists()
    }

    @Test fun aReloadThatFails_withAGuideShowing_changesNothing() {
        show()
        rule.onNodeWithText("Channel 1").assertExists()
        val asked = server.requestsTo("/livetv/guide").size
        guideFails = true
        refresh()
        assertEquals("The refresh asked for the guide again", asked + 1, server.requestsTo("/livetv/guide").size)
        rule.onNodeWithText(guideFailure).assertDoesNotExist()
        rule.onNodeWithText("Try again").assertDoesNotExist()
        rule.onNodeWithText("Channel 1").assertExists()
    }

    @Test fun aFetchCalledOffByChoosingAnotherCategory_saysNothing() {
        guideHeld.set(java.util.concurrent.CountDownLatch(1))
        show()
        // Nothing asked for in Recordings, so what was on its way is called off, and asked for again on coming back.
        press("Recordings")
        settle()
        press("All channels")
        settle()
        rule.onNodeWithText("Couldn’t load the guide", substring = true).assertDoesNotExist()
        rule.onNodeWithText("Try again").assertDoesNotExist()
    }

    @Test fun aGuideThatFailsBeforeTheCategoryTakesFocus_leavesItOnTryAgain_thenOnTheCategoryNotJustAnywhere() {
        guideFails = true
        // The clock barely moving, so the answer comes inside the wait before a category takes focus, as on a quick network.
        rule.mainClock.autoAdvance = false
        show(pump = {
            var frames = 0
            while (frames++ < 40 && rule.onAllNodesWithText(guideFailure).fetchSemanticsNodes().isEmpty()) {
                rule.mainClock.advanceTimeByFrame()
                org.robolectric.Shadows.shadowOf(android.os.Looper.getMainLooper()).idle()
                Thread.sleep(40)
            }
        })
        rule.onNodeWithText("Try again").assertIsFocused()
        guideFails = false
        press("Try again")
        settleUntilGone(guideFailure)
        settle()
        rule.onNodeWithText("All channels").assertIsFocused()
        rule.onNodeWithText("No favourites yet").assertDoesNotExist()
    }

    @Test fun noFavourites_opensOnAllChannels_notTheEmptyPage() {
        show()
        rule.onNodeWithText("All channels").assertIsFocused()
        rule.onNodeWithText("No favourites yet").assertDoesNotExist()
        rule.mainClock.advanceTimeBy(1_000)
        rule.onNodeWithText("No favourites yet").assertDoesNotExist()
    }

    @Test fun someFavourites_opensOnFavourites() {
        channels = listOf(channel(1, "News", favourite = true), channel(2, "Sport"))
        show()
        rule.onNodeWithText("Favourites").assertIsFocused()
    }

    @Test fun refreshOnTheEmptyPage_thenChannels_focusGoesToAllChannels() {
        channels = emptyList()
        show()
        rule.onNodeWithText(Words.refreshGuide).assertExists()
        channels = listOf(channel(1, "News"), channel(2, "Sport"))
        // The page gets the channels however it comes to look.
        refresh()
        rule.onNodeWithText("All channels").assertExists()
    }

    @Test fun theGroupShownGoing_fallsBackToAllChannels() {
        show()
        press("News")
        settle()
        rule.onNodeWithText("Channel 2").assertDoesNotExist()
        channels = listOf(channel(1, "Sport"), channel(2, "Sport"))
        refresh()
        rule.onNodeWithText("News").assertDoesNotExist()
        rule.onNodeWithText("Channel 2").assertExists()
    }

    @Test fun aRefreshThatWorks_saysSoInTheButton_andTheRowsDontMove() {
        show()
        val before = rule.onNodeWithText("News").getUnclippedBoundsInRoot().top
        press(Words.refreshGuide)
        server.waitUntil("the refresh") { model.liveTvRefreshesEnded == 1 }
        settle()
        rule.onNodeWithText(Words.guideRefreshed).assertExists()
        assertEquals(before, rule.onNodeWithText("News").getUnclippedBoundsInRoot().top)
        rule.mainClock.advanceTimeBy(6_100)
        settle()
        rule.onNodeWithText(Words.refreshGuide).assertExists()
        assertEquals(before, rule.onNodeWithText("News").getUnclippedBoundsInRoot().top)
    }

    @Test fun aRefreshThatFails_keepsItsWordsUnderTheButton_withTheSourcesNote() {
        show()
        extra = ""","downloadError":"The source failed","downloadSkipped":true"""
        refresh()
        rule.onNodeWithText("The source failed").assertExists()
        rule.onNodeWithText(Words.guideSourcesAskedRecently).assertExists()
        rule.onNodeWithText(Words.refreshGuide).assertExists()
    }
}
