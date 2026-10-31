package io.github.flexarr.ui.connect

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
            confirmButton = { TextButton(onClick = model::dismissInvite) { Text("OK", color = Flexarr.screen) } },
            containerColor = Flexarr.seat,
        )
        return
    }
    // One that came to the code this device showed is asked about on that screen.
    val invite = model.offeredInvite?.takeIf { !model.inviteFromCode } ?: return
    val owner = invite.ownerName
    val problem = model.joinProblem
    val notNow: @Composable () -> Unit = { TextButton(onClick = model::dismissInvite) { Text("Not now", color = Flexarr.screen) } }
    when {
        model.joining -> AlertDialog(
            onDismissRequest = {},
            text = {
                Row(horizontalArrangement = Arrangement.spacedBy(12.dp), verticalAlignment = Alignment.CenterVertically) {
                    CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp, color = Flexarr.usher)
                    Text("Joining $owner’s Flexarr…")
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
                if (model.joinCanRetry) TextButton(onClick = model::confirmInvite) { Text("Try again", color = Flexarr.screen) } else notNow()
            },
            dismissButton = if (model.joinCanRetry) notNow else null,
            containerColor = Flexarr.seat,
        )
        else -> AlertDialog(
            onDismissRequest = model::dismissInvite,
            title = { Text("Join $owner’s Flexarr?") },
            text = { Text("You’ll watch as ${invite.profileName}.") },
            confirmButton = { TextButton(onClick = model::confirmInvite) { Text("Join", color = Flexarr.screen) } },
            dismissButton = notNow,
            containerColor = Flexarr.seat,
        )
    }
}
