package io.github.flexarr.core

/**
 * What pressing and holding a piece of music offers (a card in a row, or a track in a tracklist), in the order every
 * app shares: Play, Shuffle, Play next, Add to queue, Add to playlist, Go to artist, Download. Only those that apply.
 * The page's own ⋯ is [MusicActions.menu]. FlexarrKit's `MusicMenu` has the same rules; change them together.
 */
object MusicMenu {
    enum class Subject { ARTIST, ALBUM, TRACK, PLAYLIST }

    /** A card in a row or grid, or a track in an album's or playlist's tracklist. */
    enum class Place { CARD, ROW }

    enum class Item { PLAY, SHUFFLE, PLAY_NEXT, ADD_TO_QUEUE, ADD_TO_PLAYLIST, GO_TO_ARTIST, DOWNLOAD }

    /**
     * [hasArtist]: the artist's id is known (and worth going to: not on their own page). [canDownload]: this device can
     * download it.
     */
    fun of(subject: Subject, place: Place, platform: MusicActions.Platform, hasArtist: Boolean = false, canDownload: Boolean = false): List<Item> =
        buildList {
            add(Item.PLAY)
            if (subject != Subject.TRACK && place == Place.CARD) add(Item.SHUFFLE)
            add(Item.PLAY_NEXT)
            add(Item.ADD_TO_QUEUE)
            if (platform == MusicActions.Platform.PHONE && subject != Subject.PLAYLIST) add(Item.ADD_TO_PLAYLIST)
            if (hasArtist && (subject == Subject.ALBUM || subject == Subject.TRACK)) add(Item.GO_TO_ARTIST)
            if (platform == MusicActions.Platform.PHONE && canDownload && place == Place.CARD && (subject == Subject.ALBUM || subject == Subject.PLAYLIST)) add(Item.DOWNLOAD)
        }
}
