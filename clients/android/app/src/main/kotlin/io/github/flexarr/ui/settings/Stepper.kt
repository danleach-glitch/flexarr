package io.github.flexarr.ui.settings

import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Remove
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.github.flexarr.ui.theme.Flexarr

/** A number taken down or up one at a time, within [range], as an iPhone list's Stepper. */
@Composable
internal fun Stepper(label: String, value: Int, range: IntRange, enabled: Boolean = true, onChange: (Int) -> Unit) {
    Row(Modifier.fillMaxWidth().padding(start = 16.dp, end = 4.dp, top = 2.dp, bottom = 2.dp), verticalAlignment = Alignment.CenterVertically) {
        Text(label, fontSize = 17.sp, color = if (enabled) Flexarr.screen else Flexarr.usher, modifier = Modifier.weight(1f))
        val canLower = enabled && value > range.first
        val canRaise = enabled && value < range.last
        IconButton(onClick = { onChange(value - 1) }, enabled = canLower) {
            Icon(Icons.Filled.Remove, contentDescription = "Fewer", tint = if (canLower) Flexarr.screen else Flexarr.aisle)
        }
        IconButton(onClick = { onChange(value + 1) }, enabled = canRaise) {
            Icon(Icons.Filled.Add, contentDescription = "More", tint = if (canRaise) Flexarr.screen else Flexarr.aisle)
        }
    }
}
