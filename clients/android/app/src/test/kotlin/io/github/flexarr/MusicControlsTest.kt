package io.github.flexarr

import android.app.Application
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.hasClickAction
import androidx.compose.ui.test.hasText
import androidx.compose.ui.test.onNodeWithContentDescription
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performSemanticsAction
import androidx.navigation.compose.rememberNavController
import io.github.flexarr.core.Availability
import io.github.flexarr.core.MusicExternal
import io.github.flexarr.core.followArtist
import io.github.flexarr.core.i18n.Words
import io.github.flexarr.core.unfollowArtist
import io.github.flexarr.ui.Navigator
import io.github.flexarr.ui.components.LocalNavigator
import io.github.flexarr.ui.music.MusicRequestRoundButton
import io.github.flexarr.ui.music.followState
import io.github.flexarr.ui.music.requestMusic
import io.github.flexarr.ui.music.requestState
import io.github.flexarr.ui.music.unfollowMusic
import io.github.flexarr.ui.music.unrequestMusic
import io.github.flexarr.ui.tv.TvMusicRequestRound
import io.github.flexarr.ui.tv.TvRequestableAlbum
import kotlinx.coroutines.launch
import mockwebserver3.MockResponse
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit

/**
 * The music request buttons pressed for real, on phone and TV, against a server that answers when it's told to: two quick
 * presses, a press during the send, a failure, and Follow right after Stop following.
 */
@RunWith(RobolectricTestRunner::class)
@Config(application = Application::class, sdk = [34])
class MusicControlsTest {
    @get:Rule val rule = createComposeRule()

    /** Holds the music requests until opened; [code] is how they're then answered. */
    private var gate = CountDownLatch(0)
    private var code = 204
    private val server = TestServer { request ->
        if (request.url.encodedPath.contains("/requests/music")) {
            gate.await(30, TimeUnit.SECONDS)
            if (code >= 400) TestServer.json("""{"title":"Lidarr said no"}""", code) else MockResponse.Builder().code(code).build()
        } else {
            null
        }
    }
    private val model get() = server.model
    private val album = MusicExternal("album", deezerId = 7, title = "Discovery", availability = Availability.None)
    private val artist = MusicExternal("artist", deezerId = 9, title = "Air", availability = Availability.None)
    private lateinit var navigator: Navigator

    private fun music(path: String) = server.requestsTo(path).size
    private val sent get() = server.requests.count { it.url.encodedPath.contains("/requests/music") }

    private fun settle(expected: Int) {
        server.waitUntil("$expected sent") { sent >= expected }
        gate.countDown()
        server.waitUntil("the answer") { !model.isMusicOnItsWay(listOf(album.id, artist.id)) }
        rule.waitForIdle()
    }

    private fun show(content: @androidx.compose.runtime.Composable () -> Unit) = rule.setContent {
        val context = LocalContext.current
        val controller = rememberNavController()
        navigator = androidx.compose.runtime.remember { Navigator(controller, model, context) }
        CompositionLocalProvider(LocalNavigator provides navigator, io.github.flexarr.ui.components.LocalClient provides model.client!!) { content() }
    }

    private fun phoneAlbum() = show {
        MusicRequestRoundButton(
            model, model.requestState(album), isArtist = false, name = album.title, ambient = androidx.compose.ui.graphics.Color.White,
            keys = listOf(album.id), labelled = true,
            onRequest = { navigator.requestMusic(model, album) },
            onUndo = { navigator.unrequestMusic(model, album) },
        )
    }

    private fun phoneArtist(followed: Boolean = true) = show {
        val keys = listOf(artist.id)
        MusicRequestRoundButton(
            model, model.followState(keys, followed), isArtist = true, name = artist.title, ambient = androidx.compose.ui.graphics.Color.White,
            keys = keys, labelled = true,
            onRequest = { navigator.requestMusic(model, artist, keys) },
            onUndo = { navigator.unfollowMusic(model, keys) { it.unfollowArtist(deezerId = artist.deezerId) } },
        )
    }

    private val unrequest = "Unrequest Discovery?"

    @After fun close() {
        gate.countDown()
        server.close()
    }

    @Test fun phone_aSecondPressDuringTheSend_opensNothingAndSendsNothing() {
        phoneAlbum()
        gate = CountDownLatch(1)
        rule.onNodeWithText(Words.requestAlbum).performClick()
        rule.onNodeWithText(Words.requested).assertExists().performClick()
        rule.onNodeWithText(unrequest).assertDoesNotExist()
        settle(1)
        assertEquals(1, sent)
        // Sent: a press now is someone wanting it back.
        rule.onNodeWithText(Words.requested).performClick()
        rule.onNodeWithText(unrequest).assertExists()
    }

    @Test fun phone_aRequestThatFails_goesBackAndCantBeUnrequested() {
        phoneAlbum()
        gate = CountDownLatch(1)
        code = 500
        rule.onNodeWithText(Words.requestAlbum).performClick()
        rule.onNodeWithText(Words.requested).performClick()
        settle(1)
        rule.onNodeWithText(Words.requestAlbum).assertExists()
        rule.onNodeWithText(unrequest).assertDoesNotExist()
        assertEquals(0, music("/unrequest"))
        assertNotNull("The snackbar says so", navigator.notices.currentSnackbarData)
    }

    @Test fun phone_theQuestionClosesWhenThereIsNothingLeftToTakeBack() {
        var requested by mutableStateOf(true)
        show {
            val keys = listOf(artist.id)
            MusicRequestRoundButton(
                model, model.followState(keys, requested), isArtist = true, name = artist.title, ambient = androidx.compose.ui.graphics.Color.White,
                keys = keys, labelled = true, onRequest = {}, onUndo = { error("Nothing to undo") },
            )
        }
        rule.onNodeWithText(Words.following).performClick()
        rule.onNodeWithText("Stop following Air?").assertExists()
        requested = false
        rule.waitForIdle()
        rule.onNodeWithText("Stop following Air?").assertDoesNotExist()
    }

    @Test fun phone_followDuringAnUnfollow_isIgnored_thenWorks() {
        phoneArtist()
        gate = CountDownLatch(1)
        rule.onNodeWithText(Words.following).performClick()
        rule.onNodeWithText(Words.stopFollowing).performClick()
        rule.onNodeWithText(Words.follow).performClick()
        settle(1)
        assertEquals(1, music("/unfollow"))
        assertEquals(1, sent)
        rule.onNodeWithText(Words.follow).performClick()
        server.waitUntil("the follow") { sent == 2 }
    }

    @Test fun theModel_sendsOneThingAtATimeForAKey_andPutsItBackOnFailure() {
        val keys = listOf(album.id)
        val gate = kotlinx.coroutines.CompletableDeferred<Unit>()
        var second: Boolean? = null
        var undo: Boolean? = null
        val first = model.scope.launch { model.askForMusic(keys) { gate.await() } }
        server.waitUntil { model.isMusicOnItsWay(keys) }
        assertEquals(true, album.id in model.musicAsked)
        model.scope.launch {
            second = model.askForMusic(keys) { error("Sent twice") }
            undo = model.undoMusic(keys) { error("Sent during the send") }
        }
        server.waitUntil { undo != null }
        assertEquals(false, second)
        assertEquals(false, undo)
        gate.complete(Unit)
        server.waitUntil { first.isCompleted }
        assertEquals(false, model.isMusicOnItsWay(keys))
        // Asked already: not again.
        var again: Boolean? = null
        model.scope.launch { again = model.askForMusic(keys) { error("Asked already") } }
        server.waitUntil { again != null }
        assertEquals(false, again)
        // Taken back, failing: back as it was, and free to try again.
        var failure: Throwable? = null
        model.scope.launch {
            try {
                model.undoMusic(keys) { throw IllegalStateException("No") }
            } catch (e: IllegalStateException) {
                failure = e
            }
        }
        server.waitUntil { failure != null }
        assertEquals(true, album.id in model.musicAsked)
        assertEquals(false, album.id in model.musicUndone)
        assertEquals(false, model.isMusicOnItsWay(keys))
    }

    /** A TV's buttons take the remote's select, not touch. */
    private fun androidx.compose.ui.test.SemanticsNodeInteraction.press() = performSemanticsAction(androidx.compose.ui.semantics.SemanticsActions.OnClick)

    @Test fun tv_aSecondPressDuringTheSend_opensNothing_andAFailureGoesBack() {
        show { TvRequestableAlbum(model, album, focus = null) }
        gate = CountDownLatch(1)
        code = 500
        rule.onNodeWithContentDescription(Words.requestAlbum).press()
        rule.onNode(hasText(Words.requested) and hasClickAction()).press()
        rule.onNodeWithText(unrequest).assertDoesNotExist()
        settle(1)
        assertEquals(1, sent)
        rule.onNodeWithText("Try again").assertExists()
        rule.onNodeWithText("Lidarr said no").assertExists()
    }

    @Test fun tv_aFailureAfterTheControlHasGone_isSaidInTheSnackbar() {
        var here by mutableStateOf(true)
        show { if (here) TvRequestableAlbum(model, album, focus = null) }
        gate = CountDownLatch(1)
        code = 500
        rule.onNodeWithContentDescription(Words.requestAlbum).press()
        rule.waitForIdle()
        here = false
        rule.waitForIdle()
        settle(1)
        server.waitUntil("the snackbar") { navigator.notices.currentSnackbarData != null }
        assertEquals("Couldn’t request this album. Lidarr said no", navigator.notices.currentSnackbarData!!.visuals.message)
    }

    @Test fun tv_followDuringAnUnfollow_isIgnored() {
        val keys = listOf(artist.id)
        show {
            TvMusicRequestRound(
                model, artist, model.followState(keys, followed = true), keys, onFocusName = {},
                request = { it.followArtist(artist.deezerId) },
                undo = { it.unfollowArtist(deezerId = artist.deezerId) },
            )
        }
        rule.mainClock.advanceTimeBy(500)
        server.waitUntil { android.os.SystemClock.uptimeMillis() > 1_000 }
        gate = CountDownLatch(1)
        rule.onNodeWithContentDescription(Words.following).press()
        rule.onNodeWithText(Words.stopFollowing).press()
        rule.onNodeWithContentDescription(Words.follow).press()
        settle(1)
        assertEquals(1, music("/unfollow"))
        assertEquals(1, sent)
    }
}
