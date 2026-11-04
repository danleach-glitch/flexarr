// Generated from languages/cldr/plurals.json by `make strings`. Don't edit.

package io.github.flexarr.core.i18n

@Suppress("ktlint", "CyclomaticComplexMethod")
internal object PluralRules {
    private fun md(x: Double, m: Int): Double = x % m
    private fun rng(x: Double, lo: Double, hi: Double): Boolean = x % 1.0 == 0.0 && x >= lo && x <= hi

    private fun r0(o: PluralOperands): PluralCategory {
        if (o.n == 1.0) return PluralCategory.ONE
        return PluralCategory.OTHER
    }
    private fun r1(o: PluralOperands): PluralCategory {
        if (rng(o.n, 0.0, 1.0)) return PluralCategory.ONE
        return PluralCategory.OTHER
    }
    private fun r2(o: PluralOperands): PluralCategory {
        if (o.i == 0.0 || o.n == 1.0) return PluralCategory.ONE
        return PluralCategory.OTHER
    }
    private fun r3(o: PluralOperands): PluralCategory {
        if (o.n == 0.0) return PluralCategory.ZERO
        if (o.n == 1.0) return PluralCategory.ONE
        if (o.n == 2.0) return PluralCategory.TWO
        if (rng(md(o.n, 100), 3.0, 10.0)) return PluralCategory.FEW
        if (rng(md(o.n, 100), 11.0, 99.0)) return PluralCategory.MANY
        return PluralCategory.OTHER
    }
    private fun r4(o: PluralOperands): PluralCategory {
        if (o.i == 1.0 && o.v == 0.0) return PluralCategory.ONE
        return PluralCategory.OTHER
    }
    private fun r5(o: PluralOperands): PluralCategory {
        if (md(o.n, 10) == 1.0 && !(md(o.n, 100) == 11.0)) return PluralCategory.ONE
        if (rng(md(o.n, 10), 2.0, 4.0) && !(rng(md(o.n, 100), 12.0, 14.0))) return PluralCategory.FEW
        if (md(o.n, 10) == 0.0 || rng(md(o.n, 10), 5.0, 9.0) || rng(md(o.n, 100), 11.0, 14.0)) return PluralCategory.MANY
        return PluralCategory.OTHER
    }
    private fun r6(o: PluralOperands): PluralCategory {
        if (o.n == 0.0) return PluralCategory.ZERO
        if (o.n == 1.0) return PluralCategory.ONE
        return PluralCategory.OTHER
    }
    private fun r7(o: PluralOperands): PluralCategory {
        return PluralCategory.OTHER
    }
    private fun r8(o: PluralOperands): PluralCategory {
        if (md(o.n, 10) == 1.0 && !(md(o.n, 100) == 11.0 || md(o.n, 100) == 71.0 || md(o.n, 100) == 91.0)) return PluralCategory.ONE
        if (md(o.n, 10) == 2.0 && !(md(o.n, 100) == 12.0 || md(o.n, 100) == 72.0 || md(o.n, 100) == 92.0)) return PluralCategory.TWO
        if ((rng(md(o.n, 10), 3.0, 4.0) || md(o.n, 10) == 9.0) && !(rng(md(o.n, 100), 10.0, 19.0) || rng(md(o.n, 100), 70.0, 79.0) || rng(md(o.n, 100), 90.0, 99.0))) return PluralCategory.FEW
        if (!(o.n == 0.0) && md(o.n, 1000000) == 0.0) return PluralCategory.MANY
        return PluralCategory.OTHER
    }
    private fun r9(o: PluralOperands): PluralCategory {
        if ((o.v == 0.0 && md(o.i, 10) == 1.0 && !(md(o.i, 100) == 11.0)) || (md(o.f, 10) == 1.0 && !(md(o.f, 100) == 11.0))) return PluralCategory.ONE
        if ((o.v == 0.0 && rng(md(o.i, 10), 2.0, 4.0) && !(rng(md(o.i, 100), 12.0, 14.0))) || (rng(md(o.f, 10), 2.0, 4.0) && !(rng(md(o.f, 100), 12.0, 14.0)))) return PluralCategory.FEW
        return PluralCategory.OTHER
    }
    private fun r10(o: PluralOperands): PluralCategory {
        if (o.i == 1.0 && o.v == 0.0) return PluralCategory.ONE
        if ((o.e == 0.0 && !(o.i == 0.0) && md(o.i, 1000000) == 0.0 && o.v == 0.0) || !(rng(o.e, 0.0, 5.0))) return PluralCategory.MANY
        return PluralCategory.OTHER
    }
    private fun r11(o: PluralOperands): PluralCategory {
        if ((o.v == 0.0 && (o.i == 1.0 || o.i == 2.0 || o.i == 3.0)) || (o.v == 0.0 && !(md(o.i, 10) == 4.0 || md(o.i, 10) == 6.0 || md(o.i, 10) == 9.0)) || (!(o.v == 0.0) && !(md(o.f, 10) == 4.0 || md(o.f, 10) == 6.0 || md(o.f, 10) == 9.0))) return PluralCategory.ONE
        return PluralCategory.OTHER
    }
    private fun r12(o: PluralOperands): PluralCategory {
        if (o.i == 1.0 && o.v == 0.0) return PluralCategory.ONE
        if (rng(o.i, 2.0, 4.0) && o.v == 0.0) return PluralCategory.FEW
        if (!(o.v == 0.0)) return PluralCategory.MANY
        return PluralCategory.OTHER
    }
    private fun r13(o: PluralOperands): PluralCategory {
        if (o.n == 0.0) return PluralCategory.ZERO
        if (o.n == 1.0) return PluralCategory.ONE
        if (o.n == 2.0) return PluralCategory.TWO
        if (o.n == 3.0) return PluralCategory.FEW
        if (o.n == 6.0) return PluralCategory.MANY
        return PluralCategory.OTHER
    }
    private fun r14(o: PluralOperands): PluralCategory {
        if (o.n == 1.0 || (!(o.t == 0.0) && (o.i == 0.0 || o.i == 1.0))) return PluralCategory.ONE
        return PluralCategory.OTHER
    }
    private fun r15(o: PluralOperands): PluralCategory {
        if ((o.v == 0.0 && md(o.i, 100) == 1.0) || md(o.f, 100) == 1.0) return PluralCategory.ONE
        if ((o.v == 0.0 && md(o.i, 100) == 2.0) || md(o.f, 100) == 2.0) return PluralCategory.TWO
        if ((o.v == 0.0 && rng(md(o.i, 100), 3.0, 4.0)) || rng(md(o.f, 100), 3.0, 4.0)) return PluralCategory.FEW
        return PluralCategory.OTHER
    }
    private fun r16(o: PluralOperands): PluralCategory {
        if (o.n == 1.0) return PluralCategory.ONE
        if ((o.e == 0.0 && !(o.i == 0.0) && md(o.i, 1000000) == 0.0 && o.v == 0.0) || !(rng(o.e, 0.0, 5.0))) return PluralCategory.MANY
        return PluralCategory.OTHER
    }
    private fun r17(o: PluralOperands): PluralCategory {
        if ((o.i == 0.0 || o.i == 1.0)) return PluralCategory.ONE
        return PluralCategory.OTHER
    }
    private fun r18(o: PluralOperands): PluralCategory {
        if (rng(o.i, 0.0, 1.0)) return PluralCategory.ONE
        if ((o.e == 0.0 && !(o.i == 0.0) && md(o.i, 1000000) == 0.0 && o.v == 0.0) || !(rng(o.e, 0.0, 5.0))) return PluralCategory.MANY
        return PluralCategory.OTHER
    }
    private fun r19(o: PluralOperands): PluralCategory {
        if (o.n == 1.0) return PluralCategory.ONE
        if (o.n == 2.0) return PluralCategory.TWO
        if (rng(o.n, 3.0, 6.0)) return PluralCategory.FEW
        if (rng(o.n, 7.0, 10.0)) return PluralCategory.MANY
        return PluralCategory.OTHER
    }
    private fun r20(o: PluralOperands): PluralCategory {
        if ((o.n == 1.0 || o.n == 11.0)) return PluralCategory.ONE
        if ((o.n == 2.0 || o.n == 12.0)) return PluralCategory.TWO
        if ((rng(o.n, 3.0, 10.0) || rng(o.n, 13.0, 19.0))) return PluralCategory.FEW
        return PluralCategory.OTHER
    }
    private fun r21(o: PluralOperands): PluralCategory {
        if (o.v == 0.0 && md(o.i, 10) == 1.0) return PluralCategory.ONE
        if (o.v == 0.0 && md(o.i, 10) == 2.0) return PluralCategory.TWO
        if (o.v == 0.0 && (md(o.i, 100) == 0.0 || md(o.i, 100) == 20.0 || md(o.i, 100) == 40.0 || md(o.i, 100) == 60.0 || md(o.i, 100) == 80.0)) return PluralCategory.FEW
        if (!(o.v == 0.0)) return PluralCategory.MANY
        return PluralCategory.OTHER
    }
    private fun r22(o: PluralOperands): PluralCategory {
        if ((o.i == 1.0 && o.v == 0.0) || (o.i == 0.0 && !(o.v == 0.0))) return PluralCategory.ONE
        if (o.i == 2.0 && o.v == 0.0) return PluralCategory.TWO
        return PluralCategory.OTHER
    }
    private fun r23(o: PluralOperands): PluralCategory {
        if ((o.t == 0.0 && md(o.i, 10) == 1.0 && !(md(o.i, 100) == 11.0)) || (md(o.t, 10) == 1.0 && !(md(o.t, 100) == 11.0))) return PluralCategory.ONE
        return PluralCategory.OTHER
    }
    private fun r24(o: PluralOperands): PluralCategory {
        if (o.n == 1.0) return PluralCategory.ONE
        if (o.n == 2.0) return PluralCategory.TWO
        return PluralCategory.OTHER
    }
    private fun r25(o: PluralOperands): PluralCategory {
        if (o.n == 0.0) return PluralCategory.ZERO
        if (o.n == 1.0) return PluralCategory.ONE
        if ((md(o.n, 100) == 2.0 || md(o.n, 100) == 22.0 || md(o.n, 100) == 42.0 || md(o.n, 100) == 62.0 || md(o.n, 100) == 82.0) || (md(o.n, 1000) == 0.0 && (rng(md(o.n, 100000), 1000.0, 20000.0) || md(o.n, 100000) == 40000.0 || md(o.n, 100000) == 60000.0 || md(o.n, 100000) == 80000.0)) || (!(o.n == 0.0) && md(o.n, 1000000) == 100000.0)) return PluralCategory.TWO
        if ((md(o.n, 100) == 3.0 || md(o.n, 100) == 23.0 || md(o.n, 100) == 43.0 || md(o.n, 100) == 63.0 || md(o.n, 100) == 83.0)) return PluralCategory.FEW
        if (!(o.n == 1.0) && (md(o.n, 100) == 1.0 || md(o.n, 100) == 21.0 || md(o.n, 100) == 41.0 || md(o.n, 100) == 61.0 || md(o.n, 100) == 81.0)) return PluralCategory.MANY
        return PluralCategory.OTHER
    }
    private fun r26(o: PluralOperands): PluralCategory {
        if (o.n == 0.0) return PluralCategory.ZERO
        if ((o.i == 0.0 || o.i == 1.0) && !(o.n == 0.0)) return PluralCategory.ONE
        return PluralCategory.OTHER
    }
    private fun r27(o: PluralOperands): PluralCategory {
        if (md(o.n, 10) == 1.0 && !(rng(md(o.n, 100), 11.0, 19.0))) return PluralCategory.ONE
        if (rng(md(o.n, 10), 2.0, 9.0) && !(rng(md(o.n, 100), 11.0, 19.0))) return PluralCategory.FEW
        if (!(o.f == 0.0)) return PluralCategory.MANY
        return PluralCategory.OTHER
    }
    private fun r28(o: PluralOperands): PluralCategory {
        if (md(o.n, 10) == 0.0 || rng(md(o.n, 100), 11.0, 19.0) || (o.v == 2.0 && rng(md(o.f, 100), 11.0, 19.0))) return PluralCategory.ZERO
        if ((md(o.n, 10) == 1.0 && !(md(o.n, 100) == 11.0)) || (o.v == 2.0 && md(o.f, 10) == 1.0 && !(md(o.f, 100) == 11.0)) || (!(o.v == 2.0) && md(o.f, 10) == 1.0)) return PluralCategory.ONE
        return PluralCategory.OTHER
    }
    private fun r29(o: PluralOperands): PluralCategory {
        if ((o.v == 0.0 && md(o.i, 10) == 1.0 && !(md(o.i, 100) == 11.0)) || (md(o.f, 10) == 1.0 && !(md(o.f, 100) == 11.0))) return PluralCategory.ONE
        return PluralCategory.OTHER
    }
    private fun r30(o: PluralOperands): PluralCategory {
        if (o.i == 1.0 && o.v == 0.0) return PluralCategory.ONE
        if (!(o.v == 0.0) || o.n == 0.0 || (!(o.n == 1.0) && rng(md(o.n, 100), 1.0, 19.0))) return PluralCategory.FEW
        return PluralCategory.OTHER
    }
    private fun r31(o: PluralOperands): PluralCategory {
        if (o.n == 1.0) return PluralCategory.ONE
        if (o.n == 2.0) return PluralCategory.TWO
        if (o.n == 0.0 || rng(md(o.n, 100), 3.0, 10.0)) return PluralCategory.FEW
        if (rng(md(o.n, 100), 11.0, 19.0)) return PluralCategory.MANY
        return PluralCategory.OTHER
    }
    private fun r32(o: PluralOperands): PluralCategory {
        if (o.i == 1.0 && o.v == 0.0) return PluralCategory.ONE
        if (o.v == 0.0 && rng(md(o.i, 10), 2.0, 4.0) && !(rng(md(o.i, 100), 12.0, 14.0))) return PluralCategory.FEW
        if ((o.v == 0.0 && !(o.i == 1.0) && rng(md(o.i, 10), 0.0, 1.0)) || (o.v == 0.0 && rng(md(o.i, 10), 5.0, 9.0)) || (o.v == 0.0 && rng(md(o.i, 100), 12.0, 14.0))) return PluralCategory.MANY
        return PluralCategory.OTHER
    }
    private fun r33(o: PluralOperands): PluralCategory {
        if (o.v == 0.0 && md(o.i, 10) == 1.0 && !(md(o.i, 100) == 11.0)) return PluralCategory.ONE
        if (o.v == 0.0 && rng(md(o.i, 10), 2.0, 4.0) && !(rng(md(o.i, 100), 12.0, 14.0))) return PluralCategory.FEW
        if ((o.v == 0.0 && md(o.i, 10) == 0.0) || (o.v == 0.0 && rng(md(o.i, 10), 5.0, 9.0)) || (o.v == 0.0 && rng(md(o.i, 100), 11.0, 14.0))) return PluralCategory.MANY
        return PluralCategory.OTHER
    }
    private fun r34(o: PluralOperands): PluralCategory {
        if (md(o.n, 10) == 1.0 && !(md(o.n, 100) == 11.0)) return PluralCategory.ONE
        if (o.n == 2.0) return PluralCategory.TWO
        if (!(o.n == 2.0) && rng(md(o.n, 10), 2.0, 9.0) && !(rng(md(o.n, 100), 11.0, 19.0))) return PluralCategory.FEW
        if (!(o.f == 0.0)) return PluralCategory.MANY
        return PluralCategory.OTHER
    }
    private fun r35(o: PluralOperands): PluralCategory {
        if (o.i == 0.0 || o.n == 1.0) return PluralCategory.ONE
        if (rng(o.n, 2.0, 10.0)) return PluralCategory.FEW
        return PluralCategory.OTHER
    }
    private fun r36(o: PluralOperands): PluralCategory {
        if ((o.n == 0.0 || o.n == 1.0) || (o.i == 0.0 && o.f == 1.0)) return PluralCategory.ONE
        return PluralCategory.OTHER
    }
    private fun r37(o: PluralOperands): PluralCategory {
        if (o.v == 0.0 && md(o.i, 100) == 1.0) return PluralCategory.ONE
        if (o.v == 0.0 && md(o.i, 100) == 2.0) return PluralCategory.TWO
        if ((o.v == 0.0 && rng(md(o.i, 100), 3.0, 4.0)) || !(o.v == 0.0)) return PluralCategory.FEW
        return PluralCategory.OTHER
    }
    private fun r38(o: PluralOperands): PluralCategory {
        if (rng(o.n, 0.0, 1.0) || rng(o.n, 11.0, 99.0)) return PluralCategory.ONE
        return PluralCategory.OTHER
    }

    val byLocale: Map<String, (PluralOperands) -> PluralCategory> = mapOf(
        "af" to ::r0,
        "ak" to ::r1,
        "am" to ::r2,
        "an" to ::r0,
        "ar" to ::r3,
        "ars" to ::r3,
        "as" to ::r2,
        "asa" to ::r0,
        "ast" to ::r4,
        "az" to ::r0,
        "bal" to ::r0,
        "be" to ::r5,
        "bem" to ::r0,
        "bez" to ::r0,
        "bg" to ::r0,
        "bho" to ::r1,
        "blo" to ::r6,
        "bm" to ::r7,
        "bn" to ::r2,
        "bo" to ::r7,
        "br" to ::r8,
        "brx" to ::r0,
        "bs" to ::r9,
        "ca" to ::r10,
        "ce" to ::r0,
        "ceb" to ::r11,
        "cgg" to ::r0,
        "chr" to ::r0,
        "ckb" to ::r0,
        "cs" to ::r12,
        "csw" to ::r1,
        "cv" to ::r6,
        "cy" to ::r13,
        "da" to ::r14,
        "de" to ::r4,
        "doi" to ::r2,
        "dsb" to ::r15,
        "dv" to ::r0,
        "dz" to ::r7,
        "ee" to ::r0,
        "el" to ::r0,
        "en" to ::r4,
        "eo" to ::r0,
        "es" to ::r16,
        "et" to ::r4,
        "eu" to ::r0,
        "fa" to ::r2,
        "ff" to ::r17,
        "fi" to ::r4,
        "fil" to ::r11,
        "fo" to ::r0,
        "fr" to ::r18,
        "fur" to ::r0,
        "fy" to ::r4,
        "ga" to ::r19,
        "gd" to ::r20,
        "gl" to ::r10,
        "gsw" to ::r0,
        "gu" to ::r2,
        "guw" to ::r1,
        "gv" to ::r21,
        "ha" to ::r0,
        "haw" to ::r0,
        "he" to ::r22,
        "hi" to ::r2,
        "hnj" to ::r7,
        "hr" to ::r9,
        "hsb" to ::r15,
        "hu" to ::r0,
        "hy" to ::r17,
        "ia" to ::r4,
        "id" to ::r7,
        "ie" to ::r4,
        "ig" to ::r7,
        "ii" to ::r7,
        "io" to ::r4,
        "is" to ::r23,
        "it" to ::r10,
        "iu" to ::r24,
        "ja" to ::r7,
        "jbo" to ::r7,
        "jgo" to ::r0,
        "jmc" to ::r0,
        "jv" to ::r7,
        "jw" to ::r7,
        "ka" to ::r0,
        "kab" to ::r17,
        "kaj" to ::r0,
        "kcg" to ::r0,
        "kde" to ::r7,
        "kea" to ::r7,
        "kk" to ::r0,
        "kkj" to ::r0,
        "kl" to ::r0,
        "km" to ::r7,
        "kn" to ::r2,
        "ko" to ::r7,
        "kok" to ::r2,
        "kok-Latn" to ::r2,
        "ks" to ::r0,
        "ksb" to ::r0,
        "ksh" to ::r6,
        "ku" to ::r0,
        "kw" to ::r25,
        "ky" to ::r0,
        "lag" to ::r26,
        "lb" to ::r0,
        "lg" to ::r0,
        "lij" to ::r4,
        "lkt" to ::r7,
        "lld" to ::r10,
        "ln" to ::r1,
        "lo" to ::r7,
        "lt" to ::r27,
        "lv" to ::r28,
        "mas" to ::r0,
        "mg" to ::r1,
        "mgo" to ::r0,
        "mk" to ::r29,
        "ml" to ::r0,
        "mn" to ::r0,
        "mo" to ::r30,
        "mr" to ::r0,
        "ms" to ::r7,
        "mt" to ::r31,
        "my" to ::r7,
        "nah" to ::r0,
        "naq" to ::r24,
        "nb" to ::r0,
        "nd" to ::r0,
        "ne" to ::r0,
        "nl" to ::r4,
        "nn" to ::r0,
        "nnh" to ::r0,
        "no" to ::r0,
        "nqo" to ::r7,
        "nr" to ::r0,
        "nso" to ::r1,
        "ny" to ::r0,
        "nyn" to ::r0,
        "om" to ::r0,
        "or" to ::r0,
        "os" to ::r0,
        "osa" to ::r7,
        "pa" to ::r1,
        "pap" to ::r0,
        "pcm" to ::r2,
        "pl" to ::r32,
        "prg" to ::r28,
        "ps" to ::r0,
        "pt" to ::r18,
        "pt-PT" to ::r10,
        "rm" to ::r0,
        "ro" to ::r30,
        "rof" to ::r0,
        "ru" to ::r33,
        "rwk" to ::r0,
        "sah" to ::r7,
        "saq" to ::r0,
        "sat" to ::r24,
        "sc" to ::r4,
        "scn" to ::r10,
        "sd" to ::r0,
        "sdh" to ::r0,
        "se" to ::r24,
        "seh" to ::r0,
        "ses" to ::r7,
        "sg" to ::r7,
        "sgs" to ::r34,
        "sh" to ::r9,
        "shi" to ::r35,
        "si" to ::r36,
        "sk" to ::r12,
        "sl" to ::r37,
        "sma" to ::r24,
        "smi" to ::r24,
        "smj" to ::r24,
        "smn" to ::r24,
        "sms" to ::r24,
        "sn" to ::r0,
        "so" to ::r0,
        "sq" to ::r0,
        "sr" to ::r9,
        "ss" to ::r0,
        "ssy" to ::r0,
        "st" to ::r0,
        "su" to ::r7,
        "sv" to ::r4,
        "sw" to ::r4,
        "syr" to ::r0,
        "ta" to ::r0,
        "te" to ::r0,
        "teo" to ::r0,
        "tg" to ::r2,
        "th" to ::r7,
        "ti" to ::r1,
        "tig" to ::r0,
        "tk" to ::r0,
        "tl" to ::r11,
        "tn" to ::r0,
        "to" to ::r7,
        "tpi" to ::r7,
        "tr" to ::r0,
        "ts" to ::r0,
        "tzm" to ::r38,
        "ug" to ::r0,
        "uk" to ::r33,
        "und" to ::r7,
        "ur" to ::r4,
        "uz" to ::r0,
        "ve" to ::r0,
        "vec" to ::r10,
        "vi" to ::r2,
        "vo" to ::r0,
        "vun" to ::r0,
        "wa" to ::r1,
        "wae" to ::r0,
        "wo" to ::r7,
        "xh" to ::r0,
        "xog" to ::r0,
        "yi" to ::r4,
        "yo" to ::r7,
        "yue" to ::r7,
        "zh" to ::r7,
        "zu" to ::r2,
    )
}
