package io.github.flexarr.core.i18n

import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import java.io.File
import java.util.Locale
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.CopyOnWriteArraySet

/** A language the server offers. Absent from servers before 0.61, which offer none. */
@Serializable
data class LanguageInfo(val code: String, val name: String, val direction: String = "ltr", val version: Long = 0)

@Serializable
internal data class LanguageList(val languages: List<LanguageInfo> = emptyList())

/** A language's strings for the Android apps, keyed by their English (as `make strings` writes them). */
@Serializable
data class LanguagePack(val code: String, val name: String, val direction: String = "ltr", val version: Long = 0, val messages: Map<String, String> = emptyMap())

/**
 * A string in the language shown (docs/languages.md, *Writing a string*): `t("Mark as watched")`,
 * `t("Resume from {time}", "time" to time)`, `t("Play", ctx = "music")`. The English is the key, so `make strings`
 * collects it from the code: always a plain literal, never a template. A language without the string, or whose
 * translation can't be formatted, shows the English.
 */
fun t(english: String, vararg values: Pair<String, Any?>, ctx: String? = null): String =
    Localization.translate(english, values.toMap(), ctx)

/** Marks a string for `make strings` without translating it, for tables translated later with `t(value)`. */
fun tk(english: String): String = english

/**
 * The packs the apps show: built in (core's resources/languages, written by `make strings`) and downloaded from the
 * server (GET /languages/{code}?scope=android, kept in [cacheDir]), the newer winning, string by string falling back
 * to English.
 */
object Localization {
    private val json = Json { ignoreUnknownKeys = true }
    private val contextKey = Regex("^([a-z][a-z-]*)::(.*)$", RegexOption.DOT_MATCHES_ALL)

    @Volatile private var pack: LanguagePack? = null
    private val parsed = ConcurrentHashMap<String, List<MessageFormat.Part>>()
    private val failed = ConcurrentHashMap.newKeySet<String>()
    private val listeners = CopyOnWriteArraySet<() -> Unit>()

    /** Where downloaded packs are kept (the app sets it to its files folder). */
    @Volatile var cacheDir: File? = null

    /** The language shown: a BCP 47 code, "en" for English. */
    val code: String get() = pack?.code ?: "en"

    val isRightToLeft: Boolean get() = pack?.direction == "rtl"

    /** The locale to format dates and numbers with: the language shown, in the device's own variant of it. */
    val locale: Locale get() = formattingLocale(code)

    /** The device's languages, best first (the app sets it from LocaleList; tests may too). */
    @Volatile var deviceLanguages: List<String> = listOf(Locale.getDefault().toLanguageTag())

    fun formattingLocale(language: String): Locale {
        val shown = if (language == "en-XA") "en" else language
        val base = shown.substringBefore('-')
        val device = deviceLanguages.firstOrNull { tag ->
            tag.substringBefore('-').lowercase() == base && (shown == base || tag.equals(shown, ignoreCase = true))
        }
        return Locale.forLanguageTag(device ?: shown)
    }

    fun onChange(listener: () -> Unit): () -> Unit {
        listeners += listener
        return { listeners -= listener }
    }

    internal fun translate(message: String, values: Map<String, Any?>, given: String?): String {
        val split = if (given == null) contextKey.matchEntire(message) else null
        val english = split?.groupValues?.get(2) ?: message
        val ctx = given ?: split?.groupValues?.get(1)
        val key = if (ctx != null) "$ctx::$english" else english
        val current = pack
        val translation = current?.messages?.get(key)
        if (current != null && translation != null) {
            parts(current.code, key, translation)?.let { return MessageFormat.format(it, current.code, values) }
        }
        return parts("en", key, english)?.let { MessageFormat.format(it, "en", values) } ?: english
    }

    private fun parts(language: String, key: String, message: String): List<MessageFormat.Part>? {
        val k = "$language\u0000$key"
        parsed[k]?.let { return it }
        if (k in failed) return null
        return runCatching { MessageFormat.parse(message) }.onSuccess { parsed[k] = it }.onFailure { failed += k }.getOrNull()
    }

    /** Shows a pack (null: English). */
    fun show(next: LanguagePack?) {
        val now = pack
        if (now?.code == next?.code && now?.version == next?.version) return
        pack = next
        parsed.clear()
        failed.clear()
        listeners.forEach { it() }
    }

    // ---- packs on the device ----

    /** The pack built into this app for a language (de-AT tries de-AT then de). */
    fun bundled(code: String): LanguagePack? = fallbacks(code).firstNotNullOfOrNull { c ->
        Localization::class.java.getResourceAsStream("/languages/$c.json")?.use { runCatching { json.decodeFromString<LanguagePack>(it.reader().readText()) }.getOrNull() }
    }

    private fun kept(code: String): LanguagePack? =
        cacheDir?.resolve("languages/$code.json")?.takeIf { it.exists() }?.let { runCatching { json.decodeFromString<LanguagePack>(it.readText()) }.getOrNull() }

    private fun keep(pack: LanguagePack) {
        val dir = cacheDir?.resolve("languages") ?: return
        runCatching {
            dir.mkdirs()
            val tmp = File(dir, "${pack.code}.json.part")
            tmp.writeText(json.encodeToString(LanguagePack.serializer(), pack))
            tmp.renameTo(File(dir, "${pack.code}.json"))
        }
    }

    /** The language this device showed last, to start in it before the server answers. */
    var lastShown: String?
        get() = cacheDir?.resolve("languages/last")?.takeIf { it.exists() }?.readText()?.trim()?.ifEmpty { null }
        set(value) {
            val f = cacheDir?.resolve("languages/last") ?: return
            runCatching { f.parentFile?.mkdirs(); if (value == null) f.delete() else f.writeText(value) }
        }

    /** Starts in the language shown last, before anything draws, so it doesn't flash English first. */
    fun startWithLastShown() {
        lastShown?.let { show(newestOnDevice(it)) }
    }

    /** The newest pack on the device for a language: built in or kept from the server. */
    fun newestOnDevice(code: String): LanguagePack? {
        val built = bundled(code)
        val downloaded = fallbacks(code).firstNotNullOfOrNull { kept(it) }
        return listOfNotNull(built, downloaded).maxByOrNull { it.version }
    }

    /** The best of the device's languages among those offered (pt-BR, then pt); null for English or none. */
    fun deviceChoice(offered: List<String>, preferred: List<String> = deviceLanguages): String? {
        for (tag in preferred) {
            for (c in fallbacks(normalise(tag))) {
                if (c == "en") return null
                if (c in offered) return c
            }
        }
        return null
    }

    internal fun normalise(tag: String): String {
        val parts = tag.replace('_', '-').split('-')
        return (listOf(parts.first().lowercase()) + parts.drop(1).map {
            if (it.length == 4) it.lowercase().replaceFirstChar(Char::uppercase) else it.uppercase()
        }).joinToString("-")
    }

    internal fun fallbacks(code: String): List<String> {
        val out = mutableListOf<String>()
        var c = code
        while (true) {
            out += c
            val cut = c.lastIndexOf('-')
            if (cut < 0) return out
            c = c.substring(0, cut)
        }
    }

    /**
     * Settles the language: the profile's choice (null follows the device), shown from the device's newest copy at
     * once, then from the server's when its list says it has a newer one. Offline, the device's copy stays.
     */
    suspend fun settle(profileChoice: String?, offered: List<LanguageInfo>, download: suspend (String) -> LanguagePack?) {
        val code = profileChoice ?: deviceChoice(offered.map { it.code })
        if (code == null || code == "en") return show(null)
        val onDevice = newestOnDevice(code)
        show(onDevice)
        // The most specific the server has (de-AT, then de); English is never a pack.
        val published = fallbacks(code).filter { it != "en" }.firstNotNullOfOrNull { c -> offered.firstOrNull { it.code == c } }
        if (published != null && published.version > (onDevice?.version ?: -1)) {
            download(published.code)?.let { keep(it); show(it) }
        } else if (published == null && onDevice == null) {
            show(null)
        }
    }
}
