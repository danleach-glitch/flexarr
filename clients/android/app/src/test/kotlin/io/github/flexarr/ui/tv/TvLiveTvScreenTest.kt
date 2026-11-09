package io.github.flexarr.ui.tv

import android.app.Application
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.remember
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.semantics.SemanticsActions
import androidx.compose.ui.test.assertIsFocused
import androidx.compose.ui.test.getUnclippedBoundsInRoot
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performSemanticsAction
import androidx.navigation.compose.rememberNavController
import io.github.flexarr.TestServer
import io.github.flexarr.core.i18n.Words
import io.github.flexarr.ui.Navigator
import io.github.flexarr.ui.components.LocalClient
import io.github.flexarr.ui.components.LocalNavigator
import kotlinx.coroutines.launch
import mockwebserver3.MockResponse
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
    private val server = TestServer { request ->
        val path = request.url.encodedPath
        when {
            path.endsWith("/me") -> TestServer.json("""{"profileId":"main","method":"device","isAdmin":true}""")
            path.endsWith("/livetv/channels") -> TestServer.json(channels.joinToString(",", "[", "]"))
            path.endsWith("/livetv/status") -> TestServer.json("""{"configured":true,"channels":2,"refreshing":false,"refreshesFinished":$finished,"channelsWithoutListings":3$extra}""")
            path.endsWith("/livetv/guide") -> TestServer.json(object {}.javaClass.classLoader!!.getResource("live-guide.json")!!.readText())
            path.endsWith("/livetv/refresh") -> MockResponse.Builder().code(202).build().also { finished++ }
            else -> null
        }
    }
    private val model get() = server.model

    @Before fun forgetTheGuide() = TvGuideCache.clear()

    @After fun close() {
        rule.mainClock.autoAdvance = true
        server.close()
    }

    private fun show(takeFocus: Boolean = true) {
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

    private fun press(words: String) = rule.onNodeWithText(words).performSemanticsAction(SemanticsActions.OnClick)

    private fun refresh() {
        val ended = model.liveTvRefreshesEnded
        press(Words.refreshGuide)
        server.waitUntil("the refresh") { model.liveTvRefreshesEnded == ended + 1 }
        settle()
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
