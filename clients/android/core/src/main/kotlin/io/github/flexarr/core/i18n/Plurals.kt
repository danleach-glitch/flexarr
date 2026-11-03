package io.github.flexarr.core.i18n

import kotlin.math.abs

/** A plural form, as CLDR names them. */
enum class PluralCategory { ZERO, ONE, TWO, FEW, MANY, OTHER }

/**
 * CLDR's operands of a number, computed exactly as scripts/i18n/plurals.py does, so every platform picks the same
 * form: from the number's shortest decimal form, a whole number having no fraction digits.
 */
internal class PluralOperands(x: Double) {
    val n: Double = abs(x)
    val i: Double
    val v: Double
    val w: Double
    val f: Double
    val t: Double
    val e: Double = 0.0

    init {
        val s = n.toString()
        if (n % 1.0 == 0.0 || s.contains('E') || !s.contains('.')) {
            i = n - n % 1.0; v = 0.0; w = 0.0; f = 0.0; t = 0.0
        } else {
            val whole = s.substringBefore('.')
            val frac = s.substringAfter('.')
            val stripped = frac.trimEnd('0')
            i = whole.toDouble(); v = frac.length.toDouble(); w = stripped.length.toDouble()
            f = frac.toDouble(); t = stripped.ifEmpty { "0" }.toDouble()
        }
    }
}

object Plurals {
    /** The CLDR locale a BCP 47 code uses: pt-PT, then pt, then und (everything is OTHER). */
    fun locale(code: String): String {
        var c = code.replace('_', '-')
        while (true) {
            if (c in PluralRules.byLocale) return c
            val cut = c.lastIndexOf('-')
            if (cut < 0) return "und"
            c = c.substring(0, cut)
        }
    }

    fun category(code: String, x: Double): PluralCategory =
        PluralRules.byLocale[locale(code)]?.invoke(PluralOperands(x)) ?: PluralCategory.OTHER
}
