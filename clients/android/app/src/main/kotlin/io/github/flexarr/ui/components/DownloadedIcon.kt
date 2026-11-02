package io.github.flexarr.ui.components

import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.StrokeJoin
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.graphics.vector.path
import androidx.compose.ui.unit.dp
import io.github.flexarr.ui.theme.Flexarr

/**
 * Downloaded: a green disc with a dark arrow, as the iPhone's filled arrow.down.circle. It carries its own colours, so
 * show it with `tint = Color.Unspecified`.
 */
val DownloadedIcon: ImageVector by lazy {
    ImageVector.Builder("Downloaded", 24.dp, 24.dp, 24f, 24f).apply {
        path(fill = SolidColor(Flexarr.go)) {
            moveTo(12f, 2f)
            arcToRelative(10f, 10f, 0f, true, true, 0f, 20f)
            arcToRelative(10f, 10f, 0f, true, true, 0f, -20f)
            close()
        }
        path(stroke = SolidColor(Flexarr.room), strokeLineWidth = 2.2f, strokeLineCap = StrokeCap.Round, strokeLineJoin = StrokeJoin.Round) {
            moveTo(12f, 7f)
            verticalLineTo(16.5f)
            moveTo(8f, 12.5f)
            lineTo(12f, 16.5f)
            lineTo(16f, 12.5f)
        }
    }.build()
}
