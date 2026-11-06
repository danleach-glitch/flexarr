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
import io.github.flexarr.core.MissingSeason
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
 * Request, or Search for the main profile; how many episodes seasons it has are missing, with Search), then *Coming up*.
 */
internal fun LazyListScope.showMissingSections(
    missing: ShowMissing,
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
                if (missing.seasons.isNotEmpty()) {
                    LazyRow(contentPadding = PaddingValues(horizontal = Edge), horizontalArrangement = Arrangement.spacedBy(RowSpacing), verticalAlignment = Alignment.Top) {
                        items(missing.seasons, key = { "missing-season-${it.seasonNumber}" }) { season ->
                            MissingSeasonCard(season, missing, season.seasonNumber in asked, requestState(season), canAsk, onRequest, onUnrequest, onSearch)
                        }
                    }
                }
                // A season the library has some of: how many episodes it lacks, not each one.
                missing.episodesBySeason.forEach { (season, episodes) ->
                    Row(Modifier.padding(horizontal = Edge), verticalAlignment = Alignment.CenterVertically) {
                        Text(
                            t("Season {season}: {count, plural, one {# episode missing} other {# episodes missing}}", "season" to season, "count" to episodes.size),
                            fontSize = 15.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen, modifier = Modifier.weight(1f),
                        )
                        if (missing.canSearch) {
                            val searching = season in asked
                            MissingChip(if (searching) t("Searching") else t(Words.search), FlexarrIcons.search, on = searching, enabled = !searching, description = t("Search for season {season}’s missing episodes", "season" to season)) {
                                onSearch(season, null)
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
            missing.canSearch -> MissingChip(t(Words.search), FlexarrIcons.search, on = false, enabled = true, description = t("Search for {season}", "season" to season.title)) { onSearch(season.seasonNumber, null) }
        }
    }
    if (confirming) TitleUndoDialog(season.title, onConfirm = { onUnrequest(season) }, onDismiss = { confirming = false })
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
