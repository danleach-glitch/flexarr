package io.github.flexarr.ui.detail

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyListScope
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.github.flexarr.core.MissingSeason
import io.github.flexarr.core.ShowEpisode
import io.github.flexarr.core.ShowMissing
import io.github.flexarr.ui.components.Edge
import io.github.flexarr.ui.components.PosterWidth
import io.github.flexarr.ui.components.RemoteImage
import io.github.flexarr.ui.components.RowSpacing
import io.github.flexarr.ui.components.SectionHeading
import io.github.flexarr.ui.components.pressable
import io.github.flexarr.ui.music.musicOnStyle
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.FlexarrIcons
import io.github.flexarr.ui.theme.Tokens

/**
 * What a show's page knows the library lacks, as music's artist page does: *Not in your library* (whole seasons with
 * Request, or Search for the main profile; missing episodes of seasons it has, with Search), then *Coming up*.
 */
internal fun LazyListScope.showMissingSections(
    missing: ShowMissing,
    /** Seasons asked for here, ahead of the server's next answer. */
    asked: Set<Int>,
    onRequest: (MissingSeason) -> Unit,
    onSearch: (seasonNumber: Int, episodes: List<Int>?) -> Unit,
) {
    if (!missing.isEmpty) {
        item(key = "missing") {
            Column(verticalArrangement = Arrangement.spacedBy(14.dp)) {
                SectionHeading("Not in your library")
                if (missing.seasons.isNotEmpty()) {
                    LazyRow(contentPadding = PaddingValues(horizontal = Edge), horizontalArrangement = Arrangement.spacedBy(RowSpacing), verticalAlignment = Alignment.Top) {
                        items(missing.seasons, key = { "missing-season-${it.seasonNumber}" }) { season ->
                            MissingSeasonCard(season, missing, season.seasonNumber in asked, onRequest, onSearch)
                        }
                    }
                }
                missing.episodesBySeason.forEach { (season, episodes) ->
                    Column(Modifier.padding(horizontal = Edge), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Text("Season $season", fontSize = 15.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen, modifier = Modifier.weight(1f))
                            if (missing.canSearch) {
                                val searching = season in asked
                                MissingChip(if (searching) "Searching" else "Search", FlexarrIcons.search, on = searching, enabled = !searching, description = "Search for season $season’s missing episodes") {
                                    onSearch(season, null)
                                }
                            }
                        }
                        episodes.forEach { EpisodeLine(it, detail = null) }
                    }
                }
            }
        }
    }
    if (missing.upcoming.isNotEmpty()) {
        item(key = "coming-up") {
            Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                SectionHeading("Coming up")
                Column(Modifier.padding(horizontal = Edge), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    missing.upcoming.forEach { episode -> EpisodeLine(episode, detail = episode.airDate?.let { ShowMissing.airsLabel(it) }) }
                }
            }
        }
    }
}

/** A season the library has none of: its poster, name and episodes, and Request (or Search) under it. */
@Composable
private fun MissingSeasonCard(season: MissingSeason, missing: ShowMissing, asked: Boolean, onRequest: (MissingSeason) -> Unit, onSearch: (Int, List<Int>?) -> Unit) {
    Column(Modifier.width(PosterWidth), verticalArrangement = Arrangement.spacedBy(6.dp)) {
        Box(Modifier.fillMaxWidth().aspectRatio(2f / 3f).clip(RoundedCornerShape(Tokens.radiusArt)).background(Flexarr.seat)) {
            RemoteImage(season.poster, 342, Modifier.fillMaxSize()) {
                Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                    Text(season.title, fontSize = 15.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.usher, modifier = Modifier.padding(8.dp))
                }
            }
        }
        Column(verticalArrangement = Arrangement.spacedBy(1.dp)) {
            Text(season.title, fontSize = 13.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen.copy(alpha = 0.9f), maxLines = 1, overflow = TextOverflow.Ellipsis)
            season.detail.takeIf { it.isNotEmpty() }?.let { Text(it, fontSize = 12.sp, color = Flexarr.usher, maxLines = 1, overflow = TextOverflow.Ellipsis) }
        }
        val requested = season.requested || asked
        when {
            requested -> MissingChip("Requested", FlexarrIcons.requested, on = true, enabled = false) {}
            missing.canRequest -> MissingChip("Request", FlexarrIcons.request, on = false, enabled = true, description = "Request ${season.title}") { onRequest(season) }
            missing.canSearch -> MissingChip("Search", FlexarrIcons.search, on = false, enabled = true, description = "Search for ${season.title}") { onSearch(season.seasonNumber, null) }
        }
    }
}

/** An episode the library lacks or that's still to air: "S2E3 · Title", and what's known of it under. */
@Composable
private fun EpisodeLine(episode: ShowEpisode, detail: String?) {
    Row(horizontalArrangement = Arrangement.spacedBy(12.dp), verticalAlignment = Alignment.CenterVertically) {
        Box(Modifier.width(96.dp).aspectRatio(16f / 9f).clip(RoundedCornerShape(Tokens.radiusArt)).background(Flexarr.seat)) {
            RemoteImage(episode.still, 300, Modifier.fillMaxSize()) {}
        }
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
            Text(episode.label, fontSize = 14.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen, maxLines = 2, overflow = TextOverflow.Ellipsis)
            detail?.let { Text(it, fontSize = 12.sp, color = Flexarr.usher) }
        }
    }
}

/** A small action under a card or beside a heading, styled as music's request chip; [on] lights it. */
@Composable
internal fun MissingChip(label: String, icon: ImageVector?, on: Boolean, enabled: Boolean, description: String? = null, onClick: () -> Unit) {
    val shape = RoundedCornerShape(Tokens.radiusControl)
    Box(
        Modifier.heightIn(min = 48.dp).pressable(onClick, enabled = enabled).semantics { if (description != null) contentDescription = description },
        contentAlignment = Alignment.CenterStart,
    ) {
        Row(
            Modifier.height(32.dp).clip(shape)
                .musicOnStyle(on, Flexarr.silver, shape, offFill = Flexarr.screen.copy(alpha = 0.09f))
                .then(if (on) Modifier else Modifier.border(1.dp, Flexarr.screen.copy(alpha = 0.22f), shape))
                .alpha(if (enabled || on) 1f else 0.5f)
                .padding(horizontal = 9.dp),
            horizontalArrangement = Arrangement.spacedBy(4.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            icon?.let { Icon(it, contentDescription = null, tint = Flexarr.screen, modifier = Modifier.size(15.dp)) }
            Text(label, fontSize = 12.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen, maxLines = 1)
            Spacer(Modifier.width(1.dp))
        }
    }
}
