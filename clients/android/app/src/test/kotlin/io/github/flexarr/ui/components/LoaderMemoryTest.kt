package io.github.flexarr.ui.components

import android.app.Application
import androidx.compose.material3.Text
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onNodeWithText
import androidx.navigation.NavHostController
import androidx.navigation.compose.NavHost
import androidx.navigation.compose.composable
import androidx.navigation.compose.rememberNavController
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

/** Back to a screen from one opened over it: its answer is shown at once, and fetched again only once it's old. */
@RunWith(RobolectricTestRunner::class)
@Config(application = Application::class, sdk = [34])
class LoaderMemoryTest {
    @get:Rule val rule = createComposeRule()

    @Test
    fun `back to a screen shows what it had without fetching it again`() {
        var loads = 0
        lateinit var nav: NavHostController
        rule.setContent {
            nav = rememberNavController()
            NavHost(nav, startDestination = "home") {
                composable("home") {
                    val loader = rememberLoader("home") { ++loads }
                    Loaded(loader) { Text("Home $it") }
                }
                composable("title") { Text("A title") }
            }
        }
        rule.onNodeWithText("Home 1").assertExists()
        rule.runOnIdle { nav.navigate("title") }
        rule.onNodeWithText("A title").assertExists()
        rule.runOnIdle { nav.popBackStack() }
        rule.onNodeWithText("Home 1").assertExists()
        assertEquals(1, loads)
    }

    @Test
    fun `loaders side by side on a screen each get their own answer back`() {
        lateinit var nav: NavHostController
        rule.setContent {
            nav = rememberNavController()
            NavHost(nav, startDestination = "library") {
                composable("library") {
                    // As the library page has them: one after another, of different types.
                    val browse = rememberLoader("lib") { "browse" }
                    val library = rememberLoader("lib") { 42 }
                    val genres = rememberLoader("lib") { listOf("Drama") }
                    Loaded(browse) { b ->
                        Loaded(library) { l ->
                            Loaded(genres) { g -> Text("$b ${l + 1} ${g.single()}") }
                        }
                    }
                }
                composable("title") { Text("A title") }
            }
        }
        rule.onNodeWithText("browse 43 Drama").assertExists()
        rule.runOnIdle { nav.navigate("title") }
        rule.onNodeWithText("A title").assertExists()
        rule.runOnIdle { nav.popBackStack() }
        rule.onNodeWithText("browse 43 Drama").assertExists()
    }

    @Test
    fun `a kept answer stands only for what it was loaded for, and while it's fresh`() {
        val now = System.currentTimeMillis()
        val kept = LoaderMemory.Kept(listOf("a", 1, null), "answer", now)
        assertTrue(LoaderMemory.isCurrent(kept, listOf("a", 1, null)))
        // Something watched meanwhile (a version moved): shown, but fetched again.
        assertFalse(LoaderMemory.isCurrent(kept, listOf("a", 2, null)))
        assertFalse(LoaderMemory.isCurrent(LoaderMemory.Kept(kept.keys, "answer", now - Freshness.MAX_AGE_MS - 1), kept.keys))
        assertFalse(LoaderMemory.isCurrent(null, kept.keys))

        val memory = LoaderMemory()
        memory.keep("a", kept)
        assertEquals("answer", memory.restore("a", listOf("a", 2, null))?.value)
        // Another profile signed in: nothing of the last one's is shown.
        assertEquals(null, memory.restore("a", listOf("a", 1, "someone else")))
        assertEquals(null, memory.restore("b", kept.keys))
    }
}
