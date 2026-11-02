// Generated from design/scores.json by `make tokens`. Don't edit; redraw with scripts/design/draw_scores.py.

package io.github.flexarr.ui.theme

import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.graphics.vector.addPathNodes
import androidx.compose.ui.unit.dp

/** The source mark beside a score (docs/design.md › Scores). */
object ScoreMarks {
    /** IMDb, beside the score out of 10. */
    val imdb: ImageVector by lazy {
        mark("imdb", 64f, 32f, listOf(
            0xFFF5C518 to "M5 0 L59 0 C61.76 0 64 2.24 64 5 L64 27 C64 29.76 61.76 32 59 32 L5 32 C2.24 32 0 29.76 0 27 L0 5 C0 2.24 2.24 0 5 0 Z",
            0xFF000000 to "M8 7 L12 7 L12 25 L8 25 Z",
            0xFF000000 to "M14 7 L19 7 L22 17 L25 7 L30 7 L30 25 L26.5 25 L26.5 13.5 L23.5 25 L20.5 25 L17.5 13.5 L17.5 25 L14 25 Z",
            0xFF000000 to "M32 7 L38 7 C42.5 7 44 9.5 44 16 C44 22.5 42.5 25 38 25 L32 25 Z",
            0xFFF5C518 to "M35.5 10.5 L37.5 10.5 C39.8 10.5 40.5 12 40.5 16 C40.5 20 39.8 21.5 37.5 21.5 L35.5 21.5 Z",
            0xFF000000 to "M46 7 L49.5 7 L49.5 25 L46 25 Z",
            0xFF000000 to "M49.5 13 C51 12.2 52 12 53 12 C55.6 12 56.5 14 56.5 18.5 C56.5 23 55.6 25 53 25 C52 25 51 24.6 49.5 23.6 Z",
            0xFFF5C518 to "M49.5 15.6 C50.4 15.2 51 15 51.7 15 C52.8 15 53.2 16 53.2 18.5 C53.2 21 52.8 22 51.7 22 C51 22 50.4 21.8 49.5 21.3 Z",
        ))
    }
    /** Rotten Tomatoes Certified Fresh. */
    val certifiedFresh: ImageVector by lazy {
        mark("certifiedFresh", 32f, 32f, listOf(
            0xFFFA320A to "M16 0.4 L18.73 2.27 L21.97 1.59 L23.78 4.36 L27.03 4.97 L27.64 8.22 L30.41 10.03 L29.73 13.27 L31.6 16 L29.73 18.73 L30.41 21.97 L27.64 23.78 L27.03 27.03 L23.78 27.64 L21.97 30.41 L18.73 29.73 L16 31.6 L13.27 29.73 L10.03 30.41 L8.22 27.64 L4.97 27.03 L4.36 23.78 L1.59 21.97 L2.27 18.73 L0.4 16 L2.27 13.27 L1.59 10.03 L4.36 8.22 L4.97 4.97 L8.22 4.36 L10.03 1.59 L13.27 2.27 Z",
            0xFFFFFFFF to "M16 4 C22.63 4 28 9.37 28 16 C28 22.63 22.63 28 16 28 C9.37 28 4 22.63 4 16 C4 9.37 9.37 4 16 4 Z",
            0xFFFA320A to "M16 9.27 C20.51 9.27 24.16 12.62 24.16 16.75 C24.16 20.88 20.51 24.23 16 24.23 C11.49 24.23 7.84 20.88 7.84 16.75 C7.84 12.62 11.49 9.27 16 9.27 Z",
            0xFF00912D to "M16 10.22 L12.87 7.77 L14.23 10.08 L11.65 10.36 L14.5 11.17 L16 12.4 L17.5 11.17 L20.35 10.36 L17.77 10.08 L19.13 7.77 Z",
        ))
    }
    /** Rotten Tomatoes Fresh, 60% and up. */
    val fresh: ImageVector by lazy {
        mark("fresh", 32f, 32f, listOf(
            0xFFFA320A to "M16 6.1 C22.63 6.1 28 11.02 28 17.1 C28 23.18 22.63 28.1 16 28.1 C9.37 28.1 4 23.18 4 17.1 C4 11.02 9.37 6.1 16 6.1 Z",
            0xFF00912D to "M16 7.5 L11.4 3.9 L13.4 7.3 L9.6 7.7 L13.8 8.9 L16 10.7 L18.2 8.9 L22.4 7.7 L18.6 7.3 L20.6 3.9 Z",
        ))
    }
    /** Rotten Tomatoes Rotten, under 60%. */
    val rotten: ImageVector by lazy {
        mark("rotten", 32f, 32f, listOf(
            0xFF0AC855 to "M28.9 16.5 L29.69 17.92 L28.72 19.17 L29 20.69 L23.74 19.93 L22 19.97 L21.58 20.6 L22.05 22.06 L22.47 23.96 L22.11 25.45 L20.88 25.82 L20.18 27.49 L17.85 23.94 L16.85 23.48 L16 24.3 L14.82 26.12 L13.16 27.95 L11.37 28.68 L10.08 27.81 L8.2 27.92 L9.98 23.45 L10.19 21.85 L9.58 21.21 L8.01 21.12 L6.1 20.88 L4.91 20.07 L5.16 18.78 L3.9 17.75 L8.5 16.5 L9.42 15.82 L8.83 14.99 L7.11 13.64 L5.36 11.79 L4.73 9.98 L5.63 8.89 L5.64 6.97 L9.75 9.29 L11.26 9.56 L12.03 8.91 L12.52 7.35 L13.31 5.64 L14.57 4.83 L16 5.53 L17.42 4.91 L17.7 9.65 L18.11 10.96 L18.93 10.9 L20.55 9.84 L22.7 8.77 L24.61 8.57 L25.53 9.51 L27.65 9.77 L24.48 12.74 L24.17 13.87 L25.05 14.6 L26.95 15.36 Z",
        ))
    }

    private fun mark(name: String, width: Float, height: Float, paths: List<Pair<Long, String>>): ImageVector =
        ImageVector.Builder(name, width.dp, height.dp, width, height).apply {
            for ((fill, d) in paths) addPath(addPathNodes(d), fill = SolidColor(Color(fill)))
        }.build()
}
