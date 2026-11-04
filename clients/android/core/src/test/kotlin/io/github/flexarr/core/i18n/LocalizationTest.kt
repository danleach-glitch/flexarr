package io.github.flexarr.core.i18n

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.booleanOrNull
import kotlinx.serialization.json.doubleOrNull
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Test
import java.io.File

/** The shared cases every platform's formatter runs (languages/tests/format-cases.json), and the lookup. */
class LocalizationTest {
    private val cases: JsonObject = run {
        var dir: File? = File("").absoluteFile
        while (dir != null && !File(dir, "languages/tests/format-cases.json").exists()) dir = dir.parentFile
        Json.parseToJsonElement(File(dir!!, "languages/tests/format-cases.json").readText()).jsonObject
    }

    @After fun english() = Localization.show(null)

    @Test fun formatsEverySharedCase() {
        Localization.deviceLanguages = listOf("en-US")
        for (c in cases.getValue("format").jsonArray.map { it.jsonObject }) {
            val values = c.getValue("args").jsonObject.mapValues { (_, v) ->
                val p = v.jsonPrimitive
                if (p.isString) p.content else p.doubleOrNull
            }
            val got = MessageFormat.format(MessageFormat.parse(c.getValue("message").jsonPrimitive.content), c.getValue("locale").jsonPrimitive.content, values)
            assertEquals(c.getValue("message").jsonPrimitive.content, c.getValue("expect").jsonPrimitive.content, got)
        }
    }

    @Test fun refusesEverySharedError() {
        for (m in cases.getValue("errors").jsonArray) {
            assertThrows(MessageFormat.ParseException::class.java) { MessageFormat.parse(m.jsonPrimitive.content) }
        }
    }

    @Test fun fallsBackToEnglishStringByString() {
        Localization.show(LanguagePack("de", "Deutsch", messages = mapOf(
            "Play" to "Abspielen", "music::Play" to "Wiedergeben", "Broken {x}" to "Kaputt {",
            "{n, plural, one {# season} other {# seasons}}" to "{n, plural, one {# Staffel} other {# Staffeln}}",
        )))
        assertEquals("Abspielen", t("Play"))
        assertEquals("Wiedergeben", t("Play", ctx = "music"))
        assertEquals("Wiedergeben", t("music::Play"))
        assertEquals("2 Staffeln", t("{n, plural, one {# season} other {# seasons}}", "n" to 2))
        assertEquals("Broken 1", t("Broken {x}", "x" to 1))
        assertEquals("Not translated", t("Not translated"))
    }

    @Test fun picksTheDevicesBestOfferedLanguage() {
        assertEquals("pt", Localization.deviceChoice(listOf("de", "pt"), listOf("pt-BR", "de")))
        assertEquals("pt-BR", Localization.deviceChoice(listOf("pt-BR"), listOf("pt_br")))
        assertNull(Localization.deviceChoice(listOf("de"), listOf("en-GB", "de")))
    }

    @Test fun picksCldrPluralForms() {
        assertEquals(PluralCategory.FEW, Plurals.category("pl", 3.0))
        assertEquals(PluralCategory.ZERO, Plurals.category("ar", 0.0))
        assertEquals(PluralCategory.OTHER, Plurals.category("zz", 1.0))
    }
}
