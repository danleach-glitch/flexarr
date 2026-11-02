package io.github.flexarr.core

/**
 * A music artist's or album's page actions, in the order every client shows them (docs/music-requests.md): Play,
 * Shuffle, Radio, Download, Follow (an artist) or Request (an album), then ⋯ with the rest; only those that apply.
 * FlexarrKit's `MusicActions` holds the same rules for the Apple apps; change both together.
 */
object MusicActions {
    enum class Kind { ARTIST, ALBUM }

    enum class Platform { PHONE, TV }

    enum class Button { PLAY, SHUFFLE, RADIO, DOWNLOAD, FOLLOW, REQUEST, MORE }

    data class Input(
        val kind: Kind,
        /** Something here plays: an artist with albums in the library, an album with tracks. A placeholder artist (followed, no albums yet) or an album from outside the library has none. */
        val hasMusic: Boolean,
        /** Music requests are on for this profile ([MusicRequestStatus.configured]) and the page can follow or request it. */
        val canRequest: Boolean,
        val platform: Platform,
        /** The app plays an artist's radio from this page. */
        val canRadio: Boolean = false,
        /** The phone can keep a copy of it (an album). */
        val canDownload: Boolean = false,
        /** The ⋯ menu has something in it beyond [menu]'s queue actions. */
        val hasMenu: Boolean = false,
        /** The app's ⋯ offers Play next and Add to queue (and, on phones, Add to playlist) for what plays here. */
        val canQueue: Boolean = false,
    )

    /** What ⋯ holds, in this order. */
    enum class MenuItem { PLAY_NEXT, ADD_TO_QUEUE, ADD_TO_PLAYLIST }

    fun menu(input: Input): List<MenuItem> = buildList {
        if (input.canQueue && input.hasMusic) {
            add(MenuItem.PLAY_NEXT)
            add(MenuItem.ADD_TO_QUEUE)
            if (input.platform == Platform.PHONE) add(MenuItem.ADD_TO_PLAYLIST)
        }
    }

    fun of(input: Input): List<Button> = buildList {
        if (input.hasMusic) {
            add(Button.PLAY)
            add(Button.SHUFFLE)
            if (input.kind == Kind.ARTIST && input.canRadio) add(Button.RADIO)
            if (input.platform == Platform.PHONE && input.canDownload) add(Button.DOWNLOAD)
        }
        if (input.canRequest) add(if (input.kind == Kind.ARTIST) Button.FOLLOW else Button.REQUEST)
        if (input.hasMenu || menu(input).isNotEmpty()) add(Button.MORE)
    }
}
