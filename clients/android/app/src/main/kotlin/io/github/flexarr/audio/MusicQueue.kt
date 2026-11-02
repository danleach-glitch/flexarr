package io.github.flexarr.audio

import io.github.flexarr.core.ItemSummary

/**
 * The music queue as plain data, so its rules can be tested without a player: the tracks in the order they'll play
 * ([tracks]), the order they were given ([original], for turning shuffle back off), which one is current, and whether
 * it's shuffled (unshuffled, the two orders are the same). The web's `queueReducer` (frontend/src/hooks/queue.ts) has
 * the same playNext and enqueue.
 */
internal data class MusicQueue(val tracks: List<ItemSummary>, val original: List<ItemSummary>, val index: Int, val shuffled: Boolean) {
    /**
     * Slots [added] in right after the current track. Shuffled, they also follow the current track in the given order,
     * so turning shuffle off still plays them next. Callers play [added] instead when the queue is empty.
     */
    fun playNext(added: List<ItemSummary>): MusicQueue {
        if (added.isEmpty() || tracks.isEmpty()) return this
        val newTracks = tracks.toMutableList().apply { addAll(index + 1, added) }
        if (!shuffled) return copy(tracks = newTracks, original = newTracks)
        val current = tracks.getOrNull(index)?.id
        val at = original.indexOfFirst { it.id == current }.let { if (it >= 0) it + 1 else original.size }
        return copy(tracks = newTracks, original = original.toMutableList().apply { addAll(at, added) })
    }

    /** Adds [added] to the end, in both orders. Callers play [added] instead when the queue is empty. */
    fun enqueue(added: List<ItemSummary>): MusicQueue {
        if (added.isEmpty() || tracks.isEmpty()) return this
        val newTracks = tracks + added
        return copy(tracks = newTracks, original = if (shuffled) original + added else newTracks)
    }
}
