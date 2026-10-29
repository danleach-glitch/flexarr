package io.github.flexarr.core

// What's asked for out loud ("Hey Google, play the latest The Rest Is History on Flexarr"), as the Apple apps' App
// Intents (FlexarrIntents.swift) understand it: the kind of request, then the words of what's wanted, matched against
// names the way Siri's titles are.

/** What a spoken request asks for, once the words that say what kind of request it is are taken off. */
sealed interface SpokenRequest {
    /** "Play Flexarr", "play something": what was playing, else what's next. */
    data object Anything : SpokenRequest
    /** "Play my Up Next": its first episode (the rest follow by themselves). */
    data object UpNext : SpokenRequest
    /** "Continue watching", "pick up where I left off": the top of Continue Watching (Continue listening, in the car). */
    data object Continue : SpokenRequest
    /** "Play the latest The Rest Is History": the podcast's newest episode not heard yet. */
    data class Latest(val term: String) : SpokenRequest
    /** "Play The Rest Is History", "play Heat", "play Queen": a podcast's next episode, a film or show, or music. */
    data class Title(val term: String) : SpokenRequest

    companion object {
        /** What [said] asks for. Nothing said at all is [Anything], as Android's "play music on …" sends it. */
        fun parse(said: String?): SpokenRequest {
            var text = said.orEmpty().trim().trimEnd('.', '!', '?').trim()
            fun cut(prefixes: List<String>): Boolean {
                val prefix = prefixes.firstOrNull { text.startsWith(it, ignoreCase = true) } ?: return false
                text = text.substring(prefix.length).trim()
                return true
            }
            appSuffixes.firstOrNull { text.endsWith(it, ignoreCase = true) }?.let { text = text.dropLast(it.length).trim() }
            cut(verbs)
            val plain = text.lowercase()
            return when {
                plain.isEmpty() || plain in anything -> Anything
                plain in upNext -> UpNext
                plain in continuing -> Continue
                cut(latest) -> if (text.isEmpty()) Anything else Latest(text)
                else -> {
                    cut(next)
                    if (text.isEmpty()) Anything else Title(text)
                }
            }
        }

        private val appSuffixes = listOf(" on flexarr", " in flexarr", " with flexarr", " from flexarr")
        private val verbs = listOf("play ", "listen to ", "watch ", "put on ", "resume ")
        private val anything = setOf("play", "something", "anything", "flexarr", "music", "some music", "a podcast", "podcasts", "my podcasts")
        private val upNext = setOf("up next", "my up next", "the up next", "my queue")
        private val continuing = setOf(
            "continue watching", "continue listening", "where i left off", "pick up where i left off", "what i was watching",
            "what i was listening to",
        )
        private val latest = listOf(
            "the latest episode of ", "the newest episode of ", "latest episode of ", "newest episode of ", "the latest ",
            "the newest ", "latest ", "newest ",
        )
        private val next = listOf("the next episode of ", "next episode of ", "the next ")
    }
}

/** Words that say little about which title is meant ("the" in "The Traitors"), left out of matching unless they're all there is. */
private val fillerWords = setOf("the", "a", "an")

/** The words of [said] as names are compared: lower case, letters and numbers only, filler left out. */
internal fun spokenWords(said: String): List<String> {
    val words = said.lowercase().split(Regex("[^\\p{L}\\p{N}]+")).filter { it.isNotEmpty() }
    return words.filterNot { it in fillerWords }.ifEmpty { words }
}

/**
 * How closely [name] matches [said]: 0 for the same words, 1 for a name that starts with them, 2 for one that has them
 * all somewhere; null when a word said isn't in it.
 */
fun spokenMatch(said: String, name: String): Int? {
    val wanted = spokenWords(said)
    if (wanted.isEmpty()) return null
    val words = spokenWords(name)
    val joined = words.joinToString(" ")
    return when {
        words == wanted -> 0
        joined.startsWith(wanted.joinToString(" ")) -> 1
        wanted.all { joined.contains(it) } -> 2
        else -> null
    }
}

/**
 * The [candidates] whose names have every word said in them, closest first ("rest is history", "history"), and among
 * equally close ones the shorter name. Otherwise they keep their order, so a caller lists the kinds it prefers first.
 */
fun <T> matchingSpoken(said: String, candidates: List<T>, name: (T) -> String): List<T> =
    candidates.mapNotNull { candidate -> spokenMatch(said, name(candidate))?.let { candidate to it } }
        .sortedWith(compareBy<Pair<T, Int>> { it.second }.thenBy { name(it.first).length })
        .map { it.first }
