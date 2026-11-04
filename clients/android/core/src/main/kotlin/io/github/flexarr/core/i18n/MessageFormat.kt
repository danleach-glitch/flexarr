package io.github.flexarr.core.i18n

import java.text.NumberFormat
import java.util.Locale

/**
 * The ICU MessageFormat subset Flexarr's messages use (docs/languages.md): text, {value}, {value, number},
 * {value, plural, …} with # and =N, {value, select, …}, and ICU's apostrophe quoting. scripts/i18n/icu.py is the
 * reference; languages/tests/format-cases.json keeps the platforms in step.
 */
object MessageFormat {
    sealed interface Part
    data class Text(val value: String) : Part
    data object Pound : Part
    data class Arg(val name: String, val kind: String, val options: Map<String, List<Part>> = emptyMap()) : Part

    class ParseException(message: String) : IllegalArgumentException(message)

    fun parse(message: String): List<Part> {
        val p = Parser(message)
        val parts = p.parts(inPlural = false, nested = false)
        if (p.pos != message.length) throw ParseException("unexpected '}' at ${p.pos}")
        return parts
    }

    fun format(parts: List<Part>, language: String, values: Map<String, Any?>): String =
        StringBuilder().also { write(it, parts, language, values, null) }.toString()

    private fun write(out: StringBuilder, parts: List<Part>, language: String, values: Map<String, Any?>, pound: Double?) {
        for (part in parts) {
            when (part) {
                is Text -> out.append(part.value)
                Pound -> out.append(pound?.let { number(it, language) } ?: "#")
                is Arg -> {
                    if (!values.containsKey(part.name)) {
                        out.append('{').append(part.name).append('}')
                        continue
                    }
                    val v = values[part.name]
                    when (part.kind) {
                        "plural" -> {
                            val n = (v as? Number)?.toDouble() ?: v.toString().toDoubleOrNull() ?: 0.0
                            val exact = if (n % 1.0 == 0.0) "=${n.toLong()}" else null
                            val chosen = exact?.let { part.options[it] }
                                ?: part.options[Plurals.category(language, n).name.lowercase()]
                                ?: part.options.getValue("other")
                            write(out, chosen, language, values, n)
                        }
                        "select" -> write(out, part.options[v.toString()] ?: part.options.getValue("other"), language, values, pound)
                        else -> out.append(if (v is Number) number(v.toDouble(), language) else v?.toString() ?: "")
                    }
                }
            }
        }
    }

    private fun number(n: Double, language: String): String {
        val f = NumberFormat.getNumberInstance(Localization.formattingLocale(language))
        f.maximumFractionDigits = 3
        return f.format(n)
    }

    private class Parser(val s: String) {
        var pos = 0

        fun parts(inPlural: Boolean, nested: Boolean): List<Part> {
            val parts = mutableListOf<Part>()
            val text = StringBuilder()
            fun flush() {
                if (text.isNotEmpty()) { parts += Text(text.toString()); text.clear() }
            }
            while (pos < s.length) {
                val c = s[pos]
                when {
                    c == '\'' -> {
                        val next = s.getOrNull(pos + 1)
                        if (next == '\'') { text.append('\''); pos += 2; continue }
                        if (next == '{' || next == '}' || (inPlural && next == '#')) {
                            var end = pos + 1
                            while (true) {
                                if (end >= s.length) throw ParseException("unterminated quote")
                                if (s[end] == '\'') {
                                    if (s.getOrNull(end + 1) == '\'') { text.append('\''); end += 2; continue }
                                    break
                                }
                                text.append(s[end]); end++
                            }
                            pos = end + 1
                            continue
                        }
                        text.append('\''); pos++
                    }
                    c == '{' -> { flush(); pos++; parts += arg() }
                    c == '}' -> { if (!nested) throw ParseException("unexpected '}' at $pos"); break }
                    c == '#' && inPlural -> { flush(); parts += Pound; pos++ }
                    else -> { text.append(c); pos++ }
                }
            }
            flush()
            return parts
        }

        private fun skipSpace() { while (pos < s.length && s[pos].isWhitespace()) pos++ }

        private fun word(): String {
            val start = pos
            while (pos < s.length && (s[pos].isLetterOrDigit() || s[pos] in "_=-")) pos++
            return s.substring(start, pos)
        }

        private val name = Regex("^[A-Za-z_][A-Za-z0-9_]*$")
        private val selector = Regex("^(=\\d+|[A-Za-z_][A-Za-z0-9_-]*)$")

        private fun arg(): Arg {
            skipSpace()
            val n = word()
            if (!name.matches(n)) throw ParseException("bad value name '$n'")
            skipSpace()
            if (s.getOrNull(pos) == '}') { pos++; return Arg(n, "simple") }
            if (s.getOrNull(pos) != ',') throw ParseException("expected ',' or '}' after {$n")
            pos++; skipSpace()
            val kind = word(); skipSpace()
            if (kind == "number") {
                if (s.getOrNull(pos) == '}') { pos++; return Arg(n, "number") }
                throw ParseException("{$n, number} takes no style")
            }
            if (kind != "plural" && kind != "select") throw ParseException("unsupported type '$kind' in {$n}")
            if (s.getOrNull(pos) != ',') throw ParseException("expected ',' after {$n, $kind")
            pos++
            val options = LinkedHashMap<String, List<Part>>()
            while (true) {
                skipSpace()
                if (s.getOrNull(pos) == '}') { pos++; break }
                val sel = word()
                if (!selector.matches(sel) || (kind == "select" && sel.startsWith("="))) throw ParseException("bad selector '$sel' in {$n}")
                if (sel in options) throw ParseException("selector '$sel' twice in {$n}")
                skipSpace()
                if (s.getOrNull(pos) != '{') throw ParseException("expected '{' after '$sel' in {$n}")
                pos++
                val sub = parts(kind == "plural", nested = true)
                if (s.getOrNull(pos) != '}') throw ParseException("unclosed option '$sel' in {$n}")
                pos++
                options[sel] = sub
            }
            if ("other" !in options) throw ParseException("{$n, $kind} needs an 'other'")
            return Arg(n, kind, options)
        }
    }
}
