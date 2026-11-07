package io.github.flexarr.ui.detail

import io.github.flexarr.core.i18n.Words
import io.github.flexarr.core.i18n.t
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
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.github.flexarr.core.ItemSummary
import io.github.flexarr.core.MissingSeason
import io.github.flexarr.core.SeasonGap
import io.github.flexarr.core.ShowEpisode
import io.github.flexarr.core.ShowMissing
import io.github.flexarr.ui.components.Edge
import io.github.flexarr.ui.components.PosterWidth
import io.github.flexarr.ui.components.RemoteImage
import io.github.flexarr.ui.components.RowSpacing
import io.github.flexarr.ui.components.SectionHeading
import io.github.flexarr.ui.components.pressable
import io.github.flexarr.ui.external.TitleRequestState
import io.github.flexarr.ui.external.TitleUndoDialog
import io.github.flexarr.ui.external.label
import io.github.flexarr.ui.music.musicOnStyle
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.FlexarrIcons
import io.github.flexarr.ui.theme.Tokens

/**
 * What a show's page knows the library lacks, as music's artist page does: *Not in your library* (whole seasons with
 * Request (through Seerr, or Sonarr for the main profile); and, in the same row, seasons it has part of, with how many
 * episodes they lack and Request or Requested), then *Coming up*.
 */
internal fun LazyListScope.showMissingSections(
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
    onRequest: (MissingSeason) -> Unit,
    onUnrequest: (MissingSeason) -> Unit,
    onSearch: (seasonNumber: Int, episodes: List<Int>?) -> Unit,
) {
    if (!missing.isEmpty) {
        item(key = "missing") {
            Column(verticalArrangement = Arrangement.spacedBy(14.dp)) {
                SectionHeading(t(Words.notInYourLibrary))
                val gaps = missing.seasonGaps
                if (gaps.isNotEmpty()) {
                    LazyRow(contentPadding = PaddingValues(horizontal = Edge), horizontalArrangement = Arrangement.spacedBy(RowSpacing), verticalAlignment = Alignment.Top) {
                        items(gaps, key = { if (it is SeasonGap.Part) "part-season-${it.seasonNumber}" else "missing-season-${it.seasonNumber}" }) { gap ->
                            when (gap) {
                                is SeasonGap.Whole -> MissingSeasonCard(gap.season, missing, gap.seasonNumber in asked, requestState(gap.season), canAsk, onRequest, onUnrequest, onSearch)
                                // A season the library has some of: how many episodes it lacks, not each one.
                                is SeasonGap.Part -> {
                                    val own = librarySeasons.firstOrNull { it.seasonNumber == gap.seasonNumber }
                                    PartSeasonCard(gap, own?.artwork?.poster ?: showPoster, own?.title?.takeIf { it.isNotBlank() } ?: gap.title, missing.canSearchEpisodes, gap.requested || gap.seasonNumber in asked, onSearch)
                                }
                            }
                        }
                    }
                }
            }
        }
    }
    if (missing.upcoming.isNotEmpty()) {
        item(key = "coming-up") {
            Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                SectionHeading(t("Coming up"))
                Column(Modifier.padding(horizontal = Edge), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    missing.upcoming.forEach { episode -> EpisodeLine(episode, detail = episode.airDate?.let { ShowMissing.airsLabel(it) }) }
                }
            }
        }
    }
}

/** A season the library has none of: its poster, name and episodes, and Request (or Search) under it. */
@Composable
private fun MissingSeasonCard(
    season: MissingSeason,
    missing: ShowMissing,
    asked: Boolean,
    state: TitleRequestState,
    canAsk: Boolean,
    onRequest: (MissingSeason) -> Unit,
    onUnrequest: (MissingSeason) -> Unit,
    onSearch: (Int, List<Int>?) -> Unit,
) {
    var confirming by remember { mutableStateOf(false) }
    SeasonCardFrame(season.poster, season.title, season.detail) {
        when {
            // Requested through Seerr here: pressed, it asks, then takes back just this season.
            missing.canRequest -> when (state) {
                TitleRequestState.Requested, TitleRequestState.Undoing, TitleRequestState.Sending ->
                    MissingChip(state.label(), FlexarrIcons.requested, on = true, enabled = canAsk && state == TitleRequestState.Requested, description = t("Unrequest {season}", "season" to season.title)) {
                        confirming = true
                    }
                TitleRequestState.Refused -> MissingChip(state.label(), null, on = false, enabled = false) {}
                else -> MissingChip(t(Words.request), FlexarrIcons.request, on = false, enabled = canAsk, description = t("Request {season}", "season" to season.title)) { onRequest(season) }
            }
            season.requested || asked -> MissingChip(t(Words.requested), FlexarrIcons.requested, on = true, enabled = false) {}
            // Sonarr (the main profile): it monitors and searches, which is a request as far as anyone's concerned.
            missing.canSearch -> MissingChip(t(Words.request), FlexarrIcons.request, on = false, enabled = true, description = t("Request {season}", "season" to season.title)) { onSearch(season.seasonNumber, null) }
        }
    }
    if (confirming) TitleUndoDialog(season.title, onConfirm = { onUnrequest(season) }, onDismiss = { confirming = false })
}

/**
 * A season the library has part of: its poster, name and how many episodes it lacks, and under it Request (Sonarr's
 * search, main profile), or Requested once asked or when Sonarr's already looking for them.
 */
@Composable
private fun PartSeasonCard(gap: SeasonGap.Part, poster: String?, title: String, canSearch: Boolean, requested: Boolean, onSearch: (Int, List<Int>?) -> Unit) {
    SeasonCardFrame(poster, title, gap.detail) {
        if (requested) {
            MissingChip(t(Words.requested), FlexarrIcons.requested, on = true, enabled = false) {}
        } else if (canSearch) {
            MissingChip(t(Words.request), FlexarrIcons.request, on = false, enabled = true, description = t("Request season {season}’s missing episodes", "season" to gap.seasonNumber)) {
                onSearch(gap.seasonNumber, null)
            }
        }
    }
}

/** A season card in *Not in your library*: poster (or its name on a plain tile), name and [detail], then [action]. */
@Composable
private fun SeasonCardFrame(poster: String?, title: String, detail: String, action: @Composable () -> Unit) {
    Column(Modifier.width(PosterWidth), verticalArrangement = Arrangement.spacedBy(6.dp)) {
        Box(Modifier.fillMaxWidth().aspectRatio(2f / 3f).clip(RoundedCornerShape(Tokens.radiusArt)).background(Flexarr.seat)) {
            RemoteImage(poster, 342, Modifier.fillMaxSize()) {
                Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                    Text(title, fontSize = 15.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.usher, modifier = Modifier.padding(8.dp))
                }
            }
        }
        Column(verticalArrangement = Arrangement.spacedBy(1.dp)) {
            Text(title, fontSize = 13.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen.copy(alpha = 0.9f), maxLines = 1, overflow = TextOverflow.Ellipsis)
            detail.takeIf { it.isNotEmpty() }?.let { Text(it, fontSize = 12.sp, color = Flexarr.usher, maxLines = 1, overflow = TextOverflow.Ellipsis) }
        }
        action()
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

/**
 * A small action under a card or beside a heading, styled as music's request chip; [on] lights it. TalkBack reads its
 * label (Requested, Search…) and hears [description] as what a double-tap does ("Unrequest Season 2").
 */
@Composable
internal fun MissingChip(label: String, icon: ImageVector?, on: Boolean, enabled: Boolean, description: String? = null, onClick: () -> Unit) {
    val shape = RoundedCornerShape(Tokens.radiusControl)
    Box(
        Modifier.heightIn(min = 48.dp).pressable(onClick, enabled = enabled, onClickLabel = description),
        contentAlignment = Alignment.CenterStart,
    ) {
        Row(
            Modifier.heightIn(min = 32.dp).clip(shape)
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
