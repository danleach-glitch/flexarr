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
 * "Join Dan's Flexarr as Gran?": a family invite from a link, a paste or a scan, asked about before anything happens
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
    val problem = model.joinProblem
    val body: (@Composable () -> Unit)? = when {
        model.joining -> {
            {
                Row(horizontalArrangement = Arrangement.spacedBy(12.dp), verticalAlignment = Alignment.CenterVertically) {
                    CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp, color = Flexarr.usher)
                    Text("Joining…")
                }
            }
        }
        problem != null -> {
            { Text(problem) }
        }
        else -> null
    }
    AlertDialog(
        onDismissRequest = model::dismissInvite,
        title = { Text("Join ${invite.ownerName}’s Flexarr as ${invite.profileName}?") },
        text = body,
        confirmButton = {
            TextButton(onClick = model::confirmInvite, enabled = !model.joining) {
                Text(if (problem != null) "Try again" else "Join", color = Flexarr.screen)
            }
        },
        dismissButton = {
            TextButton(onClick = model::dismissInvite, enabled = !model.joining) { Text("Cancel", color = Flexarr.screen) }
        },
        containerColor = Flexarr.seat,
    )
}
