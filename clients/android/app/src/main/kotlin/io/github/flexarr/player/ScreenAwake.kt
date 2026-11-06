package io.github.flexarr.player

/**
 * Whether a player keeps the screen on: while the picture moves, or is on its way (the spinner is up). Not over a
 * pause, the end or an error, so a phone put down paused goes dark and a TV's screensaver starts.
 */
internal fun screenStaysOn(playing: Boolean, loading: Boolean, failed: Boolean): Boolean = playing || (loading && !failed)
