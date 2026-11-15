package io.github.flexarr.ui.components

import android.app.Application
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.width
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.text.TextLayoutResult
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config
import org.robolectric.annotation.GraphicsMode

/** A tab's name in a language that runs long (the pseudo-language's 30% longer) is drawn smaller, not cut short. */
@RunWith(RobolectricTestRunner::class)
// Real text measuring: Robolectric's stand-in measures every string as nothing.
@GraphicsMode(GraphicsMode.Mode.NATIVE)
@Config(application = Application::class, sdk = [34])
class FittingTextTest {
    @get:Rule val rule = createComposeRule()

    private var last: TextLayoutResult? = null

    private fun fitting(text: String) {
        rule.setContent {
            // A tab on a 360 dp phone: five across, 8 dp apart.
            Box(Modifier.width(64.dp)) {
                FittingText(text, largest = 12.sp, smallest = 9.sp, color = Color.White, style = TextStyle(), onTextLayout = { last = it })
            }
        }
        rule.waitForIdle()
    }

    @Test
    fun `a long tab name shrinks to fit its tab`() {
        // "Libraries" in the pseudo-language: too wide for the tab at full size.
        val long = "⟦Ŀîƀŕåŕîéš~~~⟧"
        fitting(long)
        val result = last!!
        assertFalse("the name is cut short at ${result.layoutInput.style.fontSize}", result.isLineEllipsized(0) || result.hasVisualOverflow)
        assertTrue("drawn at ${result.layoutInput.style.fontSize}", result.layoutInput.style.fontSize.value < 12f)
    }

    @Test
    fun `a name that fits keeps its size`() {
        fitting("Home")
        assertEquals(12f, last!!.layoutInput.style.fontSize.value)
    }
}
