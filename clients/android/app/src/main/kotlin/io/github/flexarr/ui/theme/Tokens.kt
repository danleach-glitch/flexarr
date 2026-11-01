// Generated from design/tokens.json by `make tokens`. Don't edit; change the JSON.

package io.github.flexarr.ui.theme

import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.dp

object Tokens {
    /** Base background: a dim, plum-tinted screening room. */
    val room = Color(0xFF1B1720)
    /** Raised surfaces: panels, menus, inputs. */
    val seat = Color(0xFF25202C)
    /** A surface on a surface: a menu over a panel. */
    val seatRaised = Color(0xFF2E2836)
    /** Hairlines and inactive controls. */
    val aisle = Color(0xFF3A3343)
    /** Primary text: warm projected white. */
    val screen = Color(0xFFF2EEE7)
    /** Secondary text. */
    val usher = Color(0xFFA39AAE)
    /** Fallback accent when a title has no ambient colour. */
    val silver = Color(0xFFA9C2EC)
    /** Connected, on, set up, downloaded, in the library. */
    val go = Color(0xFF7FD1A0)
    /** Destructive actions and error messages, never decoration. */
    val danger = Color(0xFFF08A7E)
    /** The live and recording dot on Live TV, nothing else. */
    val record = Color(0xFFE5484D)
    /** Inside text fields and menus, below the panel. */
    val well = Color(0xFF141118)
    /** Posters, stills, covers. */
    val radiusArt = 4.dp
    /** Buttons and inputs. */
    val radiusControl = 10.dp
    /** Panels, sheets, cards of text. */
    val radiusPanel = 14.dp
    /** The main button's fill: the ambient colour mixed with screen, so a dark or dull title colour still reads. */
    const val MAIN_BUTTON_AMBIENT_MIX = 0.68f
}
