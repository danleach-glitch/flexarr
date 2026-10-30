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
        assertEquals(AppLink.Watchlist, parse("flexarr://watchlist"))
        assertEquals(AppLink.AsProfile("Kids", AppLink.Watchlist), parse("flexarr://watchlist?profile=Kids"))
        assertEquals(AppLink.Live(id), parse("flexarr://live/$id"))
        assertEquals(AppLink.LiveTv, parse("flexarr://livetv"))
        assertEquals(AppLink.Pairing("ABC-DEF"), parse("flexarr://pairing/ABC-DEF"))
    }

    @Test
    fun `reads the iPhone app's links`() {
        assertEquals(AppLink.Play(id, startAt = 754), parse("flexarr://play/$id?t=754.5"))
        assertEquals(AppLink.Play(id, shuffle = true), parse("flexarr://play/$id?shuffle=1"))
        // Its starting subtitles are let be; a position that isn't one starts where it was left.
        assertEquals(AppLink.Play(id), parse("flexarr://play/$id?s=embedded-3&t=soon"))
        assertEquals(AppLink.Play(id), parse("flexarr://play/$id?t=-5&shuffle=0"))
        assertEquals(AppLink.AsProfile("Kids", AppLink.Live(id)), parse("flexarr://live/$id?profile=Kids"))
        assertEquals(AppLink.AsProfile("Mum and Dad", AppLink.Play(id, startAt = 60)), parse("flexarr://play/$id?t=60&profile=Mum%20and%20Dad"))
        assertEquals(AppLink.Item(id), parse("flexarr://item/$id?profile="))
    }

    @Test
    fun `a link reads back as itself`() {
        val links = listOf(
            AppLink.Item(id), AppLink.Play("r1"), AppLink.Play(id, startAt = 90, shuffle = true), AppLink.Live(id), AppLink.LiveTv,
            AppLink.Podcast(id), AppLink.PodcastEpisode(id), AppLink.Search, AppLink.Downloads, AppLink.Home, AppLink.Watchlist,
            AppLink.AsProfile("Kids & co", AppLink.Play(id, startAt = 5)), AppLink.Pairing("ABC-DEF"),
        )
        for (link in links) assertEquals(link, AppLink.parse(link.uri))
    }

    @Test
    fun `only the server's kind of id is taken`() {
        for (bad in listOf("..", "%2e%2e", "a%2Fb", "a%3Fb", "a%20b", "x%3Bdrop")) {
            assertNull(bad, parse("flexarr://item/$bad"))
            assertNull(bad, parse("flexarr://play/$bad"))
            assertNull(bad, parse("flexarr://live/$bad"))
            assertNull(bad, parse("flexarr://play/$bad?profile=Kids"))
        }
        assertNull(parse("flexarr://live"))
        // A profile name no profile could have is no profile.
        assertEquals(AppLink.Item(id), parse("flexarr://item/$id?profile=${"x".repeat(101)}"))
        assertNull(parse("flexarr://item"))
        assertNull(parse("flexarr://play/"))
        // A pairing code is letters, digits and a dash.
        for (bad in listOf("..", "a%2Fb", "ABC DEF", "x".repeat(33))) assertNull(bad, parse("flexarr://pairing/$bad"))
        assertNull(parse("flexarr://pairing"))
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
        val links = listOf(
            AppLink.Play(id), AppLink.Play(id, startAt = 30, shuffle = true), AppLink.Live(id), AppLink.PodcastEpisode(id),
            AppLink.Item(id), AppLink.Home, AppLink.AsProfile("Kids", AppLink.Play(id)), AppLink.Pairing("ABC-DEF"),
        )
        for (link in links) {
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
        assertEquals(AppLink.Item(id), fromOutside(AppLink.Play(id, startAt = 600, shuffle = true)))
        // A channel opens the guide rather than putting it on.
        assertEquals(AppLink.LiveTv, fromOutside(AppLink.Live(id)))
        // A profile is chosen in "Who's watching?" (with its PIN), and what follows plays nothing either.
        assertEquals(AppLink.AsProfile("Kids", AppLink.Item(id), askFirst = true), fromOutside(AppLink.AsProfile("Kids", AppLink.Play(id))))
        assertEquals(AppLink.AsProfile("Kids", AppLink.LiveTv, askFirst = true), fromOutside(AppLink.AsProfile("Kids", AppLink.Live(id))))
        assertNull(fromOutside(AppLink.AsProfile("Kids", AppLink.PodcastEpisode(id))))
        // Only the app's own notification asks whether to let a device in.
        assertNull(fromOutside(AppLink.Pairing("ABC-DEF")))
        // As a browser sends them.
        val browsed = Intent(Intent.ACTION_VIEW, Uri.parse("flexarr://play/$id?t=754&s=en&shuffle=1&profile=Kids")).addCategory(Intent.CATEGORY_BROWSABLE)
        assertEquals(AppLink.AsProfile("Kids", AppLink.Item(id), askFirst = true), AppLink.received(context, browsed))
        // A guess at the app's mark is no better than none.
        val forged = Intent(Intent.ACTION_VIEW, AppLink.Play(id).uri).putExtra("io.github.flexarr.fromApp", "guess")
        assertEquals(AppLink.Item(id), AppLink.received(context, forged))
    }

    @Test
    fun `an intent without a link is nothing`() {
        assertNull(AppLink.received(context, Intent(Intent.ACTION_MAIN)))
    }
}
