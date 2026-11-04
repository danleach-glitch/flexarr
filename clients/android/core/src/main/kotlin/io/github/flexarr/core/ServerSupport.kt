package io.github.flexarr.core

import io.github.flexarr.core.i18n.Words
import io.github.flexarr.core.i18n.t

/**
 * The oldest server these apps support. Anything a newer server added is optional in the models ("Absent from servers
 * before …"); anything older than this is not catered for, and the apps say so: the connect screen won't sign in to
 * it, and a sign-in kept from before shows a notice. FlexarrKit's `ServerSupport` keeps the same floor.
 */
object ServerSupport {
    const val MINIMUM_VERSION = "0.50.0"

    /** What the connect screen and the notice say (docs/design.md, Words). */
    val UPDATE_MESSAGE: String get() = t(Words.updateYourServer)

    /**
     * Whether [version] ("0.49.2", "0.60.0-dev.1a2b3c4", "0.59.0+d4c892c…") is older than [MINIMUM_VERSION]. A dev build
     * counts by its numbers, so "0.60.0-dev.x" is new enough; a version that doesn't read never counts as old.
     */
    fun isTooOld(version: String?): Boolean {
        val found = version?.let(::numbers) ?: return false
        val minimum = numbers(MINIMUM_VERSION) ?: return false
        for (i in 0 until 3) if (found[i] != minimum[i]) return found[i] < minimum[i]
        return false
    }

    /** "1.2.3" (or "v1.2.3", "1.2.3-dev.x", "1.2.3+build") as [1, 2, 3]; "1.2" as [1, 2, 0]. Null when it doesn't read. */
    internal fun numbers(version: String): List<Int>? {
        val core = version.trim().removePrefix("v").removePrefix("V").takeWhile { it != '+' && it != '-' }
        val parts = core.split('.')
        if (parts.size !in 2..3) return null
        val numbers = parts.map { part -> if (part.isNotEmpty() && part.all { it in '0'..'9' }) part.toIntOrNull() ?: return null else return null }
        return numbers + List(3 - numbers.size) { 0 }
    }
}

/** The server is older than the apps support ([ServerSupport.MINIMUM_VERSION]). */
class ServerTooOldException : Exception(ServerSupport.UPDATE_MESSAGE)
