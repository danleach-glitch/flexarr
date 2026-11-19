package io.github.flexarr.ui.connect

import io.github.flexarr.core.i18n.t
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.size
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import io.github.flexarr.AppModel
import io.github.flexarr.ui.theme.Flexarr


/**
 * "Join Dan’s Flexarr?" (as Gran): a family invite from a link, a paste or a scan, asked about before anything happens
 * (a link could have come from any app or web page). Over every screen, signed in or not; not for one that came to
 * the code this device showed, which that screen asks about.
 */
@Composable
fun InviteDialog(model: AppModel) {
    model.inviteProblem?.let { problem ->
        AlertDialog(
            onDismissRequest = model::dismissInvite,
            text = { Text(problem) },
            confirmButton = { TextButton(onClick = model::dismissInvite) { Text(t("OK"), color = Flexarr.screen) } },
            containerColor = Flexarr.seat,
        )
        return
    }
    // One that came to the code this device showed is asked about on that screen.
    val invite = model.offeredInvite?.takeIf { !model.inviteFromCode } ?: return
    val problem = model.joinProblem
    val notNow: @Composable () -> Unit = { TextButton(onClick = model::dismissInvite) { Text(t("Not now"), color = Flexarr.screen) } }
    when {
        model.joining -> AlertDialog(
            onDismissRequest = {},
            text = {
                Row(horizontalArrangement = Arrangement.spacedBy(12.dp), verticalAlignment = Alignment.CenterVertically) {
                    CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp, color = Flexarr.usher)
                    Text(t("{kind, select, named {Joining {server}…} owner {Joining {owner}’s Flexarr…} other {Joining this Flexarr…}}", *invite.serverNaming.values))
                }
            },
            confirmButton = {},
            containerColor = Flexarr.seat,
        )
        // Try again only when it could work (the host couldn't be reached).
        problem != null -> AlertDialog(
            onDismissRequest = model::dismissInvite,
            text = { Text(problem) },
            confirmButton = {
                if (model.joinCanRetry) TextButton(onClick = model::confirmInvite) { Text(t("Try again"), color = Flexarr.screen) } else notNow()
            },
            dismissButton = if (model.joinCanRetry) notNow else null,
            containerColor = Flexarr.seat,
        )
        else -> AlertDialog(
            onDismissRequest = model::dismissInvite,
            title = { Text(t("{kind, select, named {Join {server}?} owner {Join {owner}’s Flexarr?} other {Join this Flexarr?}}", *invite.serverNaming.values)) },
            text = { Text(t("You’ll watch as {profile}.", "profile" to invite.profileName)) },
            confirmButton = { TextButton(onClick = model::confirmInvite) { Text(t("Join"), color = Flexarr.screen) } },
            dismissButton = notNow,
            containerColor = Flexarr.seat,
        )
    }
}

/**
 * "Signed in as Claire": said once, just after joining as a household device away from home, so it's plain this is
 * the household's own Flexarr on someone's own profile, not a guest's. As Apple's.
 */
@Composable
fun AwayWelcomeDialog(model: AppModel) {
    val welcome = model.awayWelcome ?: return
    AlertDialog(
        onDismissRequest = { model.awayWelcome = null },
        title = { Text(t("Signed in as {profile}", "profile" to welcome.profile)) },
        text = { Text(awayWelcomeMessage(welcome)) },
        confirmButton = { TextButton(onClick = { model.awayWelcome = null }) { Text(t("OK"), color = Flexarr.screen) } },
        containerColor = Flexarr.seat,
    )
}

/** "Smith House works here as it does at home." */
fun awayWelcomeMessage(welcome: AppModel.AwayWelcome): String =
    t("{kind, select, named {{server} works here as it does at home.} owner {{owner}’s Flexarr works here as it does at home.} other {Flexarr works here as it does at home.}}", *welcome.server.values)
