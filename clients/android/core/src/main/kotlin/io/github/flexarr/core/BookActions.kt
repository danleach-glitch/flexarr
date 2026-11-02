package io.github.flexarr.core

/**
 * An audiobook's page actions, in the order every client shows them (docs/design.md › Platforms): Play or Resume,
 * Start over, Finished (or Unread once it's finished), Download, then ⋯ with the rest; only those that apply. Like
 * [DetailActions] for films and shows. FlexarrKit's `BookActions` holds the same rules for the Apple apps; change both
 * together. Part way through, Mark as unread is offered in Start over's question rather than as a button of its own.
 */
object BookActions {
    enum class Platform { PHONE, TV }

    enum class Button { PLAY, START_OVER, FINISHED, DOWNLOAD, MORE }

    data class Input(
        /** A place part way through, not finished. */
        val hasProgress: Boolean,
        val finished: Boolean,
        val isKids: Boolean,
        val platform: Platform,
        /** This device can download the book (phones). */
        val canDownload: Boolean = false,
        /** The ⋯ menu has something in it. */
        val hasMenu: Boolean = false,
    )

    fun of(input: Input): List<Button> = buildList {
        add(Button.PLAY)
        if (input.hasProgress || input.finished) add(Button.START_OVER)
        if (!input.isKids) add(Button.FINISHED)
        if (input.platform == Platform.PHONE && input.canDownload) add(Button.DOWNLOAD)
        if (input.hasMenu) add(Button.MORE)
    }
}
