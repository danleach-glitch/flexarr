package io.github.flexarr.ui.tv

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.tv.material3.Icon
import androidx.tv.material3.Text
import io.github.flexarr.core.MissingSeason
import io.github.flexarr.core.ShowEpisode
import io.github.flexarr.core.ShowMissing
import io.github.flexarr.ui.components.RemoteImage
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.FlexarrIcons

/**
 * A show's gaps on the TV, as the phone's: *Not in your library* (seasons it has none of, then each partly-had
 * season's missing episodes) and *Coming up*. Pressing a season requests it (or, for the main profile without Seerr,
 * searches); Search beside a season's missing episodes asks Sonarr for all of them (main profile). Episode cards have
 * nothing to do but still take focus, so the page scrolls to them.
 */
@Composable
internal fun TvShowMissing(
    missing: ShowMissing,
    asked: Set<Int>,
    onRequest: (MissingSeason) -> Unit,
    onSearch: (seasonNumber: Int, episodes: List<Int>?) -> Unit,
) {
    if (!missing.isEmpty) {
        Column {
            TvRowHeading("Not in your library")
            if (missing.seasons.isNotEmpty()) {
                LazyRow(contentPadding = PaddingValues(horizontal = TvLayout.Edge, vertical = 14.dp), horizontalArrangement = Arrangement.spacedBy(TvLayout.RowSpacing)) {
                    items(missing.seasons, key = { "missing-season-${it.seasonNumber}" }) { season ->
                        val requested = season.requested || season.seasonNumber in asked
                        val (status, action) = when {
                            requested -> "Requested" to null
                            missing.canRequest -> "Request" to { onRequest(season) }
                            missing.canSearch -> "Search" to { onSearch(season.seasonNumber, null) }
                            else -> null to null
                        }
                        SeasonCard(season, status, requested, action)
                    }
                }
            }
            missing.episodesBySeason.forEach { (season, episodes) ->
                val searched = season in asked
                Row(
                    modifier = Modifier.padding(start = TvLayout.Edge, end = TvLayout.Edge, top = 6.dp),
                    horizontalArrangement = Arrangement.spacedBy(16.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Text(
                        "Season $season: ${episodes.size} ${if (episodes.size == 1) "episode" else "episodes"}",
                        fontSize = 14.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.usher,
                    )
                    // The whole season's missing episodes, as on the web and the phones.
                    if (missing.canSearch) {
                        TvActionButton(
                            if (searched) "Searching" else "Search",
                            onClick = { onSearch(season, null) },
                            icon = FlexarrIcons.search,
                            enabled = !searched,
                            contentDescription = "Search for season $season’s missing episodes",
                        )
                    }
                }
                LazyRow(contentPadding = PaddingValues(horizontal = TvLayout.Edge, vertical = 12.dp), horizontalArrangement = Arrangement.spacedBy(20.dp)) {
                    items(episodes, key = { "missing-${it.code}" }) { episode ->
                        EpisodeCard(episode, detail = episode.airDate?.let { ShowMissing.airedLabel(it) }, onClick = {})
                    }
                }
            }
        }
    }
    if (missing.upcoming.isNotEmpty()) {
        Column {
            TvRowHeading("Coming up")
            LazyRow(contentPadding = PaddingValues(horizontal = TvLayout.Edge, vertical = 12.dp), horizontalArrangement = Arrangement.spacedBy(20.dp)) {
                items(missing.upcoming, key = { "upcoming-${it.code}" }) { episode ->
                    EpisodeCard(episode, detail = episode.airDate?.let { ShowMissing.airsLabel(it) }, onClick = {})
                }
            }
        }
    }
}

@Composable
private fun SeasonCard(season: MissingSeason, status: String?, requested: Boolean, action: (() -> Unit)?) {
    val width = 150.dp
    TvArtworkButton(
        ambient = Flexarr.silver,
        onClick = { action?.invoke() },
        modifier = Modifier.width(width),
        contentDescription = listOfNotNull(season.title, status).joinToString(", "),
        artwork = {
            Box(Modifier.size(width, width * 1.5f)) {
                RemoteImage(season.poster, 342, Modifier.fillMaxSize()) {
                    Box(Modifier.fillMaxSize().background(Flexarr.seat), contentAlignment = Alignment.Center) {
                        Text(season.title, fontSize = 14.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.usher)
                    }
                }
            }
        },
        caption = { focused ->
            Column(verticalArrangement = Arrangement.spacedBy(3.dp)) {
                Text(season.title, fontSize = 12.5.sp, fontWeight = FontWeight.SemiBold, color = if (focused) Flexarr.screen else Flexarr.screen.copy(alpha = 0.88f), maxLines = 1, overflow = TextOverflow.Ellipsis)
                season.detail.takeIf { it.isNotEmpty() }?.let { Text(it, fontSize = 11.sp, color = Flexarr.usher, maxLines = 1) }
                status?.let {
                    Row(horizontalArrangement = Arrangement.spacedBy(4.dp), verticalAlignment = Alignment.CenterVertically) {
                        Icon(if (requested) FlexarrIcons.requested else if (it == "Search") FlexarrIcons.search else FlexarrIcons.request, contentDescription = null, tint = Flexarr.screen, modifier = Modifier.size(13.dp))
                        Text(it, fontSize = 11.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen)
                    }
                }
            }
        },
    )
}

@Composable
private fun EpisodeCard(episode: ShowEpisode, detail: String?, onClick: () -> Unit) {
    val width = 220.dp
    TvArtworkButton(
        ambient = Flexarr.silver,
        onClick = onClick,
        modifier = Modifier.width(width),
        contentDescription = listOfNotNull(episode.label, detail).joinToString(", "),
        artwork = {
            Box(Modifier.size(width, width * 9f / 16f)) {
                RemoteImage(episode.still, 500, Modifier.fillMaxSize()) { Box(Modifier.fillMaxSize().background(Flexarr.seat)) }
            }
        },
        caption = { focused ->
            Column(verticalArrangement = Arrangement.spacedBy(3.dp)) {
                Text(episode.label, fontSize = 12.5.sp, fontWeight = FontWeight.SemiBold, color = if (focused) Flexarr.screen else Flexarr.screen.copy(alpha = 0.88f), maxLines = 2, overflow = TextOverflow.Ellipsis)
                detail?.let { Text(it, fontSize = 11.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.usher) }
            }
        },
    )
}
