package io.github.flexarr.audio

import android.app.Application
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config

/** What the headset's play button picks up again once the app has been closed. */
@RunWith(RobolectricTestRunner::class)
@Config(application = Application::class)
class LastHeardTest {
    private val context = RuntimeEnvironment.getApplication()

    @Test
    fun keepsTheLastStartedUntilSignOut() {
        assertNull(LastHeard.load(context))
        LastHeard.save(context, LastHeard("episode:1", "Episode one", "The Show", "/images/1"))
        LastHeard.save(context, LastHeard("book:2", "A Book", null, null))
        assertEquals(LastHeard("book:2", "A Book", null, null), LastHeard.load(context))
        LastHeard.clear(context)
        assertNull(LastHeard.load(context))
    }
}
