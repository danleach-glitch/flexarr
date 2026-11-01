package io.github.flexarr

import android.accessibilityservice.AccessibilityServiceInfo
import android.app.UiModeManager
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.res.Configuration
import android.graphics.Bitmap
import android.graphics.Rect
import android.os.SystemClock
import android.util.Log
import android.view.accessibility.AccessibilityNodeInfo
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.Assume.assumeTrue
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File

/**
 * The same eight screens on every client, for a before-and-after look at the design (docs/design.md): Home, a movie
 * library, a movie, a show, the player with its controls, Search, Settings and the profiles. PNGs named
 * `android-01-home.png` (or `androidtv-…` on a TV) land in the app's files, under `parity/`. Nothing is recorded (no
 * history), and nothing is requested or changed.
 *
 * Only platform APIs (UiAutomation, shell input) and text matching, so the file runs as it is on any branch. Run with
 * the app and its tests installed (`./gradlew :app:installDebug :app:installDebugAndroidTest`):
 *
 * `adb shell am instrument -w -e class io.github.flexarr.ParityTour -e server <url> -e apiKey <key>
 * [-e profileName <name>] [-e movies <library>] [-e shows <library>] [-e query <text>]
 * com.danleach.flexarr.test/androidx.test.runner.AndroidJUnitRunner`, then
 * `adb pull /sdcard/Android/data/com.danleach.flexarr/files/parity`.
 */
@RunWith(AndroidJUnit4::class)
class ParityTour {
    private val instrumentation = InstrumentationRegistry.getInstrumentation()
    private val automation = instrumentation.uiAutomation
    private val arguments = InstrumentationRegistry.getArguments()
    private val context: Context = instrumentation.targetContext
    private val tv = (context.getSystemService(Context.UI_MODE_SERVICE) as UiModeManager).currentModeType == Configuration.UI_MODE_TYPE_TELEVISION
    private val prefix = if (tv) "androidtv" else "android"
    /** The sidebar's places, to tell when focus is in it (it opens on the tab that's showing). */
    private var sidebarPlaces = listOf<String>()
    private val folder = File(context.getExternalFilesDir(null), "parity").apply { mkdirs() }

    @Test
    fun tour() {
        // Windows other than the focused one (menus, sheets), and views that only hold text.
        automation.serviceInfo = automation.serviceInfo.apply {
            flags = flags or AccessibilityServiceInfo.FLAG_RETRIEVE_INTERACTIVE_WINDOWS or AccessibilityServiceInfo.FLAG_INCLUDE_NOT_IMPORTANT_VIEWS
        }
        val server = arguments.getString("server")
        val key = arguments.getString("apiKey")
        assumeTrue("No server given", server != null && key != null)
        val movies = arguments.getString("movies") ?: "Movies"
        val shows = arguments.getString("shows") ?: "TV Shows"
        val query = arguments.getString("query") ?: "the"
        sidebarPlaces = listOf("Home", "Search", "Settings", "Discover", "Watchlist", "Live TV", "Music", movies, shows)

        context.startActivity(
            Intent(Intent.ACTION_MAIN)
                .setComponent(ComponentName(context.packageName, "io.github.flexarr.MainActivity"))
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TASK)
                .putExtra("server", server)
                .putExtra("apiKey", key)
                .putExtra("noHistory", true),
        )
        // Android's own "Viewing full screen" note would cover the player the first time it goes full screen.
        shell("settings put secure immersive_mode_confirmations confirmed")
        chooseProfile()
        if (tv) tvTour(movies, shows, query) else phoneTour(movies, shows, query)
    }

    private fun chooseProfile() {
        // Signing in, then "Who's watching?" or (one profile, or one chosen before) Home. A slow emulator takes a while.
        val home = if (tv) "More info" else "Libraries"
        val first = waitFor(45_000) { find("Who’s watching?")?.let { "picker" } ?: find(home)?.let { "home" } }
        Log.i(TAG, "After signing in: ${first ?: "nothing recognised"}; on screen: ${nodes().mapNotNull { it.label().takeIf(String::isNotEmpty) }.take(12)}")
        if (first != "picker") return
        settle(1_500)
        val name = arguments.getString("profileName")
        if (tv) {
            // A TV takes no taps: along the row to the profile, then OK.
            if (name != null) repeat(8) { if (!focusedHas(name)) key("DPAD_RIGHT") }
            key("DPAD_CENTER")
        } else if (name != null && tap(name)) {
            return settle(6_000)
        } else {
            // The first profile under the heading.
            val heading = find("Who’s watching?")?.bounds()
            val first = nodes().filter { it.isClickable }.map { it.bounds() }.filter { heading == null || it.top > heading.bottom }.minByOrNull { it.top * 10_000 + it.left }
            first?.let { shell("input tap ${it.centerX()} ${it.centerY()}") }
        }
        settle(6_000)
    }

    private fun phoneTour(movies: String, shows: String, query: String) {
        waitFor(30_000) { find("Libraries") }
        settle(4_000)
        shot("01-home")

        openLibrary(movies)
        shot("02-library")
        openFirstTitle()
        shot("03-movie")

        shell("input keyevent BACK"); settle(1_500)
        shell("input keyevent BACK"); settle(1_500)
        openLibrary(shows)
        openFirstTitle()
        shot("04-show")

        // The show's Play (or Resume), then its controls; no history is kept.
        val play = waitFor(8_000) { nodes().firstOrNull { node -> node.label().let { it.startsWith("Play") || it.startsWith("Resume") } } }
        if (play != null) {
            tapNode(play)
            settle(10_000)
            // Android's own "Viewing full screen" note, the first time something goes full screen.
            if (tap("Got it")) settle(1_000)
            val (w, h) = screen()
            shell("input tap ${w / 2} ${h / 2}")
            settle(1_200)
            shot("05-player")
        }
        // Back out to the tabs, however many presses the player takes.
        repeat(4) {
            if (find("Search") != null) return@repeat
            shell("input keyevent BACK")
            settle(2_500)
            if (tap("Got it")) settle(1_000)
        }

        tap("Search"); settle(1_500)
        tap("Clear")
        nodes().firstOrNull { it.isEditable }?.let { tapNode(it) }
        settle(1_000)
        shell("input text ${query.replace(" ", "%s")}")
        settle(1_000)
        shell("input keyevent ENTER") // search, and the keyboard goes
        settle(5_000)
        shot("06-search")

        tap("Settings"); settle(2_500)
        shot("07-settings")

        // Who's watching, from Settings' Watching as.
        tap("Switch")
        settle(3_000)
        shot("08-profiles")
        shell("input keyevent BACK")
    }

    private fun openLibrary(name: String) {
        tap("Libraries")
        waitFor(10_000) { find(name) }
        tap(name)
        settle(8_000) // the posters too
    }

    /** The grid's first poster, under the page's title and controls. */
    private fun openFirstTitle() {
        val (w, h) = screen()
        shell("input tap ${w / 5} ${h * 2 / 5}")
        settle(5_000)
    }

    private fun tvTour(movies: String, shows: String, query: String) {
        settle(8_000)
        shot("01-home")

        if (sidebar(movies)) {
            settle(6_000)
            shot("02-library")
            if (tvOpenTitle()) {
                settle(5_000)
                shot("03-movie")
            }
            key("BACK"); settle(2_000)
        }

        if (sidebar(shows)) {
            settle(6_000)
            if (tvOpenTitle()) {
                settle(5_000)
                shot("04-show")
            }

            if (focusedStartsWith("Play") || focusedStartsWith("Resume")) {
                key("DPAD_CENTER") // Play has focus as the page opens
                settle(10_000)
                key("DPAD_DOWN") // the controls
                settle(1_200)
                shot("05-player")
                key("BACK"); settle(1_000)
                key("BACK"); settle(3_000)
            }
        }

        if (sidebar("Search")) {
        settle(2_000)
        key("DPAD_CENTER") // the field has focus: its keyboard
        settle(1_500)
        shell("input text ${query.replace(" ", "%s")}")
        settle(1_000)
        key("ENTER")
        settle(5_000)
        shot("06-search")
        }

        if (sidebar("Settings")) {
            settle(2_500)
            shot("07-settings")
        }

        // Who's watching, from the profile at the top of the sidebar.
        if (openSidebar("Home")) {
            repeat(10) { key("DPAD_UP") }
            key("DPAD_CENTER")
            settle(3_000)
            shot("08-profiles")
            key("BACK")
        }
    }

    /**
     * Brings in the sidebar (Back, until focus is in it), moves to [name] with the remote and chooses it. A TV takes no
     * taps. False when it isn't there, so nothing else is pressed.
     */
    private fun sidebar(name: String): Boolean {
        if (!openSidebar(name)) return false
        for (direction in listOf("DPAD_DOWN", "DPAD_UP")) {
            repeat(16) {
                if (focusedHas(name)) {
                    key("DPAD_CENTER")
                    return true
                }
                key(direction)
            }
        }
        Log.w(TAG, "No $name in the sidebar")
        return false
    }

    /**
     * Opens the library's first title: from Browse's hero, its More info (OK there would play it); in a grid, the
     * first poster, which has focus. False when neither is there, so nothing plays.
     */
    private fun tvOpenTitle(): Boolean {
        if (focusedStartsWith("Play") || focusedStartsWith("Resume")) {
            key("DPAD_RIGHT")
            if (!focusedHas("More info")) return false
        } else if (nodes().none { it.isFocused }) {
            return false
        }
        key("DPAD_CENTER")
        return true
    }

    /**
     * Left until focus is in the sidebar (on one of its places, or [name]): left from a screen's left edge brings it
     * in, and unlike Back it never leaves the app. Back once more if that doesn't do it (a screen above a tab's first).
     */
    private fun openSidebar(name: String): Boolean {
        val places = sidebarPlaces + name
        repeat(2) { attempt ->
            repeat(8) {
                if (places.any(::focusedHas)) return true
                key("DPAD_LEFT")
                settle(700)
            }
            if (attempt == 0) {
                key("BACK")
                settle(2_000)
            }
        }
        return places.any(::focusedHas).also { if (!it) Log.w(TAG, "The sidebar didn't come") }
    }

    /** Whether what has focus holds a label starting with [prefix] (Play S1E1, Resume from 12:00). */
    private fun focusedStartsWith(prefix: String): Boolean {
        fun holds(node: AccessibilityNodeInfo): Boolean =
            node.label().startsWith(prefix) || (0 until node.childCount).any { i -> node.getChild(i)?.let(::holds) == true }
        return nodes().any { it.isFocused && holds(it) }
    }

    /** Whether what has focus is, or holds, [label]. */
    private fun focusedHas(label: String): Boolean {
        fun holds(node: AccessibilityNodeInfo): Boolean =
            node.label() == label || (0 until node.childCount).any { i -> node.getChild(i)?.let(::holds) == true }
        return nodes().any { it.isFocused && holds(it) }
    }

    // Driving the screen with platform APIs only.

    private fun key(code: String) {
        shell("input keyevent $code")
        settle(500)
    }

    private fun shell(command: String) {
        automation.executeShellCommand(command).close()
    }

    private fun settle(ms: Long) = SystemClock.sleep(ms)

    private fun screen(): Pair<Int, Int> = context.resources.displayMetrics.let { it.widthPixels to it.heightPixels }

    private fun nodes(): List<AccessibilityNodeInfo> {
        val all = mutableListOf<AccessibilityNodeInfo>()
        fun walk(node: AccessibilityNodeInfo?) {
            if (node == null) return
            // What's cached can miss a focus move (Compose for TV sends few events): asked afresh.
            node.refresh()
            all += node
            for (i in 0 until node.childCount) walk(node.getChild(i))
        }
        // Every window (a menu or sheet is one of its own), and the app's in case the list leaves it out.
        runCatching { automation.windows }.getOrNull().orEmpty().forEach { walk(it.root) }
        walk(automation.rootInActiveWindow)
        return all
    }

    private fun AccessibilityNodeInfo.label(): String = (text ?: contentDescription)?.toString().orEmpty()

    private fun AccessibilityNodeInfo.bounds(): Rect = Rect().also { getBoundsInScreen(it) }

    private fun find(label: String): AccessibilityNodeInfo? =
        nodes().firstOrNull { it.text?.toString() == label || it.contentDescription?.toString() == label }

    private fun tap(label: String): Boolean {
        val node = find(label) ?: return false
        tapNode(node)
        return true
    }

    private fun tapNode(node: AccessibilityNodeInfo) {
        val bounds = node.bounds()
        shell("input tap ${bounds.centerX()} ${bounds.centerY()}")
    }

    private fun <T> waitFor(timeout: Long, check: () -> T?): T? {
        val end = SystemClock.uptimeMillis() + timeout
        while (SystemClock.uptimeMillis() < end) {
            runCatching(check).getOrNull()?.let { return it }
            SystemClock.sleep(500)
        }
        return null
    }

    private fun shot(name: String) {
        val bitmap = automation.takeScreenshot() ?: return Unit.also { Log.w(TAG, "No screenshot for $name") }
        val file = File(folder, "$prefix-$name.png")
        file.outputStream().use { bitmap.compress(Bitmap.CompressFormat.PNG, 100, it) }
        Log.i(TAG, "Saved ${file.path}, ${nodes().size} nodes on screen")
    }

    private companion object {
        const val TAG = "ParityTour"
    }
}
