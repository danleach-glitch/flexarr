package io.github.flexarr.ui.tv

import io.github.flexarr.core.i18n.Words
import io.github.flexarr.core.i18n.t
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
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.tv.material3.Icon
import androidx.tv.material3.Text
import androidx.compose.ui.graphics.vector.ImageVector
import io.github.flexarr.core.ItemSummary
import io.github.flexarr.core.MissingSeason
import io.github.flexarr.core.SeasonGap
import io.github.flexarr.core.ShowEpisode
import io.github.flexarr.core.ShowMissing
import io.github.flexarr.ui.components.RemoteImage
import io.github.flexarr.ui.external.SeerrNotAnswering
import io.github.flexarr.ui.external.TitleRequestState
import io.github.flexarr.ui.external.label
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.FlexarrIcons

/**
 * A show's gaps on the TV, as the phone's: *Not in your library* (one row of the seasons it has none of and those it
 * has part of, with how many episodes they lack) and *Coming up*. Pressing a season requests it (or, for the main
 * profile without Seerr, asks Sonarr to search); pressing a requested one asks, then takes back just that season; pressing a
 * partly-had season asks Sonarr for all its missing episodes (main profile). Cards with nothing to do (and the
 * coming-up episode cards) still take focus, so the page scrolls to them.
 */
@Composable
internal fun TvShowMissing(
    missing: ShowMissing,
    /** The library's own seasons of the show, for a partly-had season's poster and name. */
    librarySeasons: List<ItemSummary>,
    /** The show's poster, for a partly-had season without one of its own. */
    showPoster: String?,
    /** Seasons searched for here, ahead of the server's next answer. */
    asked: Set<Int>,
    /** Where a season's request stands (Request, Requested, Can't request…), counting what was done here. */
    requestState: (MissingSeason) -> TitleRequestState,
    /** Whether Request and Requested can be pressed now (not on a kids profile, nor while Seerr isn't answering). */
    canAsk: Boolean,
    /** Seerr is connected but not answering: Request and Requested are greyed, with a line saying why. */
    seerrNotAnswering: Boolean = false,
    onRequest: (MissingSeason) -> Unit,
    onUnrequest: (MissingSeason) -> Unit,
    onSearch: (seasonNumber: Int, episodes: List<Int>?) -> Unit,
) {
    var confirming by remember { mutableStateOf<MissingSeason?>(null) }
    if (!missing.isEmpty) {
        Column {
            TvRowHeading(t(Words.notInYourLibrary))
            val gaps = missing.seasonGaps
            if (gaps.isNotEmpty()) {
                LazyRow(contentPadding = PaddingValues(horizontal = TvLayout.Edge, vertical = 14.dp), horizontalArrangement = Arrangement.spacedBy(TvLayout.RowSpacing)) {
                    items(gaps, key = { if (it is SeasonGap.Part) "part-season-${it.seasonNumber}" else "missing-season-${it.seasonNumber}" }) { gap ->
                        if (gap is SeasonGap.Part) {
                            // A season the library has some of: how many episodes it lacks, and Request them from Sonarr (main
                            // profile), or Requested once asked or when Sonarr's already looking for them.
                            val own = librarySeasons.firstOrNull { it.seasonNumber == gap.seasonNumber }
                            val title = own?.title?.takeIf { it.isNotBlank() } ?: gap.title
                            val requested = gap.requested || gap.seasonNumber in asked
                            val canSearch = missing.canSearchEpisodes
                            SeasonCard(
                                poster = own?.artwork?.poster ?: showPoster,
                                title = title,
                                detail = gap.detail,
                                status = if (requested) t(Words.requested) else if (canSearch) t(Words.request) else null,
                                statusIcon = if (requested) FlexarrIcons.requested else FlexarrIcons.request,
                                action = if (canSearch && !requested) { { onSearch(gap.seasonNumber, null) } } else null,
                                dimmed = requested,
                                description = if (canSearch && !requested) t("Request season {season}’s missing episodes", "season" to gap.seasonNumber) else null,
                            )
                        } else {
                            val season = (gap as SeasonGap.Whole).season
                            val state = if (missing.canRequest) requestState(season) else null
                            val requested = state == TitleRequestState.Requested || state == TitleRequestState.Undoing || state == TitleRequestState.Sending ||
                                (state == null && (season.requested || season.seasonNumber in asked))
                            val (status, action) = when {
                                state == TitleRequestState.Requested -> state.label() to { if (canAsk) confirming = season }
                                state == TitleRequestState.Refused -> state.label() to null
                                state != null && requested -> state.label() to null
                                requested -> t(Words.requested) to null
                                missing.canRequest -> t(Words.request) to { if (canAsk) onRequest(season) }
                                missing.canSearch -> t(Words.request) to { onSearch(season.seasonNumber, null) }
                                else -> null to null
                            }
                            // Request or Requested that can't be pressed now (a kids profile, or Seerr not answering): greyed, as on the phones.
                            val blocked = !canAsk && missing.canRequest && (state == TitleRequestState.Requested || (!requested && state != TitleRequestState.Refused))
                            val icon = if (requested) FlexarrIcons.requested else FlexarrIcons.request
                            SeasonCard(season.poster, season.title, season.detail, status, icon, action, dimmed = blocked)
                        }
                    }
                }
                if (missing.seasons.isNotEmpty() && missing.canRequest && seerrNotAnswering) {
                    Text(SeerrNotAnswering, fontSize = 13.sp, fontWeight = FontWeight.Medium, color = Flexarr.usher, modifier = Modifier.padding(horizontal = TvLayout.Edge))
                }
            }
        }
    }
    confirming?.let { season -> TvTitleUndoMenu(season.title, onDismiss = { confirming = null }) { onUnrequest(season) } }
    if (missing.upcoming.isNotEmpty()) {
        Column {
            TvRowHeading(t("Coming up"))
            LazyRow(contentPadding = PaddingValues(horizontal = TvLayout.Edge, vertical = 12.dp), horizontalArrangement = Arrangement.spacedBy(20.dp)) {
                items(missing.upcoming, key = { "upcoming-${it.code}" }) { episode ->
                    EpisodeCard(episode, detail = episode.airDate?.let { ShowMissing.airsLabel(it) }, onClick = {})
                }
            }
        }
    }
}

/**
 * A season card: poster (or its name on a plain tile), name, [detail], and [status] with [statusIcon] under them.
 * TalkBack reads [description] when given, else the name and status.
 */
@Composable
private fun SeasonCard(
    poster: String?,
    title: String,
    detail: String,
    status: String?,
    statusIcon: ImageVector,
    action: (() -> Unit)?,
    dimmed: Boolean = false,
    description: String? = null,
) {
    val width = 150.dp
    TvArtworkButton(
        ambient = Flexarr.silver,
        onClick = { action?.invoke() },
        modifier = Modifier.width(width),
        contentDescription = description ?: listOfNotNull(title, detail.takeIf { it.isNotEmpty() }, status).joinToString(", "),
        artwork = {
            Box(Modifier.size(width, width * 1.5f)) {
                RemoteImage(poster, 342, Modifier.fillMaxSize()) {
                    Box(Modifier.fillMaxSize().background(Flexarr.seat), contentAlignment = Alignment.Center) {
                        Text(title, fontSize = 14.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.usher)
                    }
                }
            }
        },
        caption = { focused ->
            Column(verticalArrangement = Arrangement.spacedBy(3.dp)) {
                Text(title, fontSize = 12.5.sp, fontWeight = FontWeight.SemiBold, color = if (focused) Flexarr.screen else Flexarr.screen.copy(alpha = 0.88f), maxLines = 1, overflow = TextOverflow.Ellipsis)
                detail.takeIf { it.isNotEmpty() }?.let { Text(it, fontSize = 11.sp, color = Flexarr.usher, maxLines = 1) }
                status?.let {
                    Row(Modifier.alpha(if (dimmed) 0.5f else 1f), horizontalArrangement = Arrangement.spacedBy(4.dp), verticalAlignment = Alignment.CenterVertically) {
                        Icon(statusIcon, contentDescription = null, tint = Flexarr.screen, modifier = Modifier.size(13.dp))
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
