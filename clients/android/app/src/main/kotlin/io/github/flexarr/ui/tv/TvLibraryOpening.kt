package io.github.flexarr.ui.tv

import android.content.Context
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import io.github.flexarr.core.Library
import io.github.flexarr.core.LibraryType
import io.github.flexarr.core.i18n.Words
import io.github.flexarr.core.i18n.t
import io.github.flexarr.core.i18n.tk

/**
 * Which of a library's views its tab opens on: Browse (a hero and rows of just this library), All (every title, with
 * sort, genre and A–Z) or Genres (a tile for each). Settings › Libraries, for every library on this TV, as on the
 * Apple TV.
 */
enum class TvLibraryOpening(val raw: String, private val english: String) {
    Browse("browse", Words.browse),
    All("all", tk("All titles")),
    Genres("genres", Words.genres);

    /** In the language shown. */
    val label: String get() = t(english)

    companion object {
        private var current: TvLibraryOpening? by mutableStateOf(null)

        /** The setting; the tabs follow it as it changes. */
        fun saved(context: Context): TvLibraryOpening =
            current ?: (entries.firstOrNull { it.raw == prefs(context).getString(KEY, null) } ?: Browse).also { current = it }

        fun save(context: Context, opening: TvLibraryOpening) {
            prefs(context).edit().putString(KEY, opening.raw).apply()
            current = opening
        }

        /** Films and shows have genres; a home videos library opens on Browse instead. */
        fun hasGenres(library: Library): Boolean = library.type == LibraryType.Movies || library.type == LibraryType.TvShows

        private const val KEY = "libraryOpensOn"

        private fun prefs(context: Context) = context.getSharedPreferences("tv", Context.MODE_PRIVATE)
    }
}
