package io.github.flexarr.ui.components

import androidx.compose.foundation.Image
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.width
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.semantics.clearAndSetSemantics
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.unit.Dp
import io.github.flexarr.core.RottenTomatoesState
import io.github.flexarr.core.Scores
import io.github.flexarr.ui.theme.ScoreMarks

/** A source's mark beside its score (docs/design.md › Scores), as one thing for TalkBack. */
@Composable
fun ScoreChip(mark: ImageVector, text: String, label: String, markHeight: Dp, style: TextStyle) {
    Row(
        Modifier.clearAndSetSemantics { contentDescription = label },
        horizontalArrangement = Arrangement.spacedBy(markHeight * 0.4f),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Image(mark, contentDescription = null, modifier = Modifier.height(markHeight).width(markHeight * (mark.viewportWidth / mark.viewportHeight)))
        Text(text, style = style, maxLines = 1)
    }
}

/** IMDb, then Rotten Tomatoes with its tomato, each as its own item of a facts line. */
@Composable
fun ScoreChipItems(scores: Scores, markHeight: Dp, style: TextStyle) {
    val imdb = scores.imdbText
    if (imdb != null) ScoreChip(ScoreMarks.imdb, imdb, scores.imdbLabel ?: imdb, markHeight, style)
    scores.rottenTomatoes?.let { rt ->
        ScoreChip(tomato(rt.state), "${rt.score}%", scores.rottenTomatoesLabel ?: "${rt.score}%", markHeight, style)
    }
}

fun tomato(state: RottenTomatoesState): ImageVector = when (state) {
    RottenTomatoesState.CertifiedFresh -> ScoreMarks.certifiedFresh
    RottenTomatoesState.Rotten -> ScoreMarks.rotten
    RottenTomatoesState.Fresh, RottenTomatoesState.Unknown -> ScoreMarks.fresh
}
