package io.github.flexarr.player

/**
 * Whether a player keeps the screen on: while the picture moves, or is on its way (the spinner is up, or the next
 * episode is being counted down to over the last frame). Not over a pause, the end or an error, so a phone put down
 * paused goes dark and a TV's screensaver starts. [countingDown] alone isn't enough: a countdown over the credits or
 * to a skip follows the picture, so pausing holds it for as long as the pause lasts; only once the file has [ended]
 * does the clock run it.
 */
internal fun screenStaysOn(
    playing: Boolean,
    loading: Boolean,
    failed: Boolean,
    countingDown: Boolean = false,
    ended: Boolean = false,
): Boolean = playing || ((loading || (countingDown && ended)) && !failed)
