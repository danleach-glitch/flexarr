package io.github.flexarr.ui.components

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.layout.size
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.drawscope.rotate
import androidx.compose.ui.unit.Dp

/** The Flexarr mark: three bars, the top one tilted (logo-mark.svg, whose view box is 40 wide and 42 tall from 12,12). */
@Composable
fun BrandMark(size: Dp, modifier: Modifier = Modifier) {
    Canvas(modifier.size(size)) {
        val unit = this.size.height / 42f
        fun bar(x: Float, y: Float, width: Float, colour: Color) = drawRoundRect(
            colour,
            topLeft = Offset((x - 12) * unit, (y - 12) * unit),
            size = Size(width * unit, 10 * unit),
            cornerRadius = CornerRadius(5 * unit),
        )
        rotate(-5f, pivot = Offset((14 - 12) * unit, (20 - 12) * unit)) { bar(14f, 15f, 36f, Color(0xFFF2EEE7)) }
        bar(14f, 28.5f, 30f, Color(0xFFA9C2EC))
        bar(14f, 42f, 23f, Color(0xFFE8A0BF))
    }
}
