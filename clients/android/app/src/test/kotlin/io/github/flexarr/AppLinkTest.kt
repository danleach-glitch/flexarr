package io.github.flexarr

import android.app.Application
import android.content.Intent
import android.net.Uri
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config

/**
 * `flexarr://` links from notifications, shortcuts and the widget, and from other apps. A plain Application, so the
 * app's own (Firebase, Sentry, the log) doesn't start.
 */
@RunWith(RobolectricTestRunner::class)
@Config(application = Application::class)
class AppLinkTest {
    private val context get() = RuntimeEnvironment.getApplication()

    private val id = "01a16be6-5a0b-790c-8734-28f147909cd9"

    private fun parse(link: String) = AppLink.parse(Uri.parse(link))

    @Test
    fun `reads each kind of link`() {
        assertEquals(AppLink.Item(id), parse("flexarr://item/$id"))
        assertEquals(AppLink.Play(id), parse("flexarr://play/$id"))
        assertEquals(AppLink.Podcast(id), parse("flexarr://podcast/$id"))
        assertEquals(AppLink.PodcastEpisode(id), parse("flexarr://episode/$id"))
        assertEquals(AppLink.Search, parse("flexarr://search"))
        assertEquals(AppLink.Downloads, parse("flexarr://downloads"))
        assertEquals(AppLink.Home, parse("flexarr://home"))
    }

    @Test
    fun `a link reads back as itself`() {
        val links = listOf(AppLink.Item(id), AppLink.Play("r1"), AppLink.Podcast(id), AppLink.PodcastEpisode(id), AppLink.Search, AppLink.Downloads, AppLink.Home)
        for (link in links) assertEquals(link, AppLink.parse(link.uri))
    }

    @Test
    fun `only the server's kind of id is taken`() {
        for (bad in listOf("..", "%2e%2e", "a%2Fb", "a%3Fb", "a%20b", "x%3Bdrop")) {
            assertNull(bad, parse("flexarr://item/$bad"))
            assertNull(bad, parse("flexarr://play/$bad"))
        }
        assertNull(parse("flexarr://item"))
        assertNull(parse("flexarr://play/"))
    }

    @Test
    fun `anything that isn't one of the app's links is nothing`() {
        assertNull(AppLink.parse(null))
        assertNull(parse("https://item/$id"))
        assertNull(parse("flexarr://settings"))
        assertNull(parse("flexarr://"))
    }

    @Test
    fun `the app's own links do what they say`() {
        for (link in listOf(AppLink.Play(id), AppLink.PodcastEpisode(id), AppLink.Item(id), AppLink.Home)) {
            assertEquals(link, AppLink.received(context, link.intent(context)))
        }
    }

    @Test
    fun `from another app, nothing starts playing`() {
        fun fromOutside(link: AppLink) = AppLink.received(context, Intent(Intent.ACTION_VIEW, link.uri))

        assertEquals(AppLink.Item(id), fromOutside(AppLink.Play(id)))
        assertNull(fromOutside(AppLink.PodcastEpisode(id)))
        assertEquals(AppLink.Podcast(id), fromOutside(AppLink.Podcast(id)))
        assertEquals(AppLink.Search, fromOutside(AppLink.Search))
        // A guess at the app's mark is no better than none.
        val forged = Intent(Intent.ACTION_VIEW, AppLink.Play(id).uri).putExtra("io.github.flexarr.fromApp", "guess")
        assertEquals(AppLink.Item(id), AppLink.received(context, forged))
    }

    @Test
    fun `an intent without a link is nothing`() {
        assertNull(AppLink.received(context, Intent(Intent.ACTION_MAIN)))
    }
}
