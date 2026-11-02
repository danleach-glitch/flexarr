package io.github.flexarr.ui.external

import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.sp
import io.github.flexarr.AppModel
import io.github.flexarr.core.Availability
import io.github.flexarr.core.requestSeasons
import io.github.flexarr.core.requestTitle
import io.github.flexarr.core.unrequestTitle
import io.github.flexarr.ui.Navigator
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.FlexarrIcons

// Film and show requests, as music's (ExternalMusicScreens): Request, then Requested, which asks and takes it back for
// the household; greyed while Seerr isn't answering; Can't request with why when the server says it can't be had.

/** Where a film, show or season from outside the library stands, for its control. */
internal enum class TitleRequestState { CanRequest, Sending, Requested, Undoing, Refused, Partly, InLibrary, None }

/** What a request is kept under: "movie:603", "tv:1399", or a season's "tv:1399:s2". */
internal fun titleKey(mediaType: String, tmdbId: Int, season: Int? = null): String =
    if (season == null) "$mediaType:$tmdbId" else "$mediaType:$tmdbId:s$season"

/** Where [key] stands, counting what this profile did here ahead of the server's [availability]. */
internal fun AppModel.titleState(key: String, availability: Availability, inLibrary: Boolean = false): TitleRequestState = when {
    inLibrary || availability == Availability.Available -> TitleRequestState.InLibrary
    availability == Availability.PartlyAvailable -> TitleRequestState.Partly
    key in titleUndoing -> TitleRequestState.Undoing
    key in titleAsked -> TitleRequestState.Requested
    key in titleSending -> TitleRequestState.Sending
    key in titleRefused -> TitleRequestState.Refused
    availability == Availability.Requested && key !in titleUndone -> TitleRequestState.Requested
    availability == Availability.None || (availability == Availability.Requested && key in titleUndone) -> TitleRequestState.CanRequest
    // A state from a newer server: nothing to offer.
    else -> TitleRequestState.None
}

/** Whether Request or Requested can be pressed: kids profiles don't request, and nothing waits on Seerr while it's away. */
internal val AppModel.canAskForTitles: Boolean get() = !isKids && !seerrUnreachable

internal fun TitleRequestState.label(): String = when (this) {
    TitleRequestState.CanRequest -> "Request"
    TitleRequestState.Sending -> "Requesting…"
    TitleRequestState.Requested, TitleRequestState.Undoing -> "Requested"
    TitleRequestState.Refused -> "Can’t request"
    TitleRequestState.Partly -> "Partly in your library"
    TitleRequestState.InLibrary -> "In your library"
    TitleRequestState.None -> "Not available"
}

internal fun TitleRequestState.icon(): ImageVector? = when (this) {
    TitleRequestState.CanRequest -> FlexarrIcons.request
    TitleRequestState.Sending, TitleRequestState.Requested, TitleRequestState.Undoing -> FlexarrIcons.requested
    else -> null
}

/** Whether the control does something when pressed: Request asks, Requested asks first then takes it back. */
internal fun TitleRequestState.pressable(model: AppModel): Boolean =
    model.canAskForTitles && (this == TitleRequestState.CanRequest || this == TitleRequestState.Requested)

/** The one quiet line when Seerr is connected but not answering, as the web's. */
internal const val SeerrNotAnswering = "Seerr isn’t answering."

/** The question before Requested is taken back, its answer and the way out, as music's and the web's. */
internal fun titleUndoTitle(name: String) = "Unrequest $name?"
internal const val TitleUndoConfirm = "Unrequest"
internal const val TitleUndoKeep = "Keep it"

/** Asked once before Requested is taken back. */
@Composable
internal fun TitleUndoDialog(name: String, onConfirm: () -> Unit, onDismiss: () -> Unit) {
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(titleUndoTitle(name)) },
        confirmButton = { TextButton(onClick = { onDismiss(); onConfirm() }) { Text(TitleUndoConfirm) } },
        dismissButton = { TextButton(onClick = onDismiss) { Text(TitleUndoKeep) } },
    )
}

/** "Seerr isn't answering." under a control greyed for it, or why a refused request can't be had. */
@Composable
internal fun TitleRequestNote(model: AppModel, key: String, state: TitleRequestState) {
    val text = when {
        state == TitleRequestState.Refused -> model.titleRefused[key]
        model.seerrUnreachable && (state == TitleRequestState.CanRequest || state == TitleRequestState.Requested) -> SeerrNotAnswering
        else -> null
    } ?: return
    Text(text, fontSize = 13.sp, fontWeight = FontWeight.Medium, color = Flexarr.usher, textAlign = TextAlign.Center)
}

/** Asks Seerr for a film or show (or just [season] of a show), once; the snackbar says if it didn't work. */
internal fun Navigator.requestTitle(model: AppModel, mediaType: String, tmdbId: Int, season: Int? = null) {
    watchlistChange("Couldn’t request this") {
        model.askForTitle(titleKey(mediaType, tmdbId, season)) { client ->
            if (season != null) client.requestSeasons(tmdbId, listOf(season)) else client.requestTitle(mediaType, tmdbId)
        }
    }
}

/** Takes a film or show request (or just [season] of a show's) back, once; the snackbar says if it didn't work. */
internal fun Navigator.unrequestTitle(model: AppModel, mediaType: String, tmdbId: Int, season: Int? = null) {
    watchlistChange("Couldn’t unrequest this") {
        model.undoTitle(titleKey(mediaType, tmdbId, season)) { it.unrequestTitle(mediaType, tmdbId, season?.let(::listOf)) }
    }
}
