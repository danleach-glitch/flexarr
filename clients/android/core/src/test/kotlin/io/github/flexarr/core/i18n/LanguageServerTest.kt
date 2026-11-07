package io.github.flexarr.core.i18n

import io.github.flexarr.core.FlexarrClient
import kotlinx.coroutines.test.runTest
import mockwebserver3.MockResponse
import mockwebserver3.MockWebServer
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import java.io.File
import java.nio.file.Files

/** The apps against servers with and without languages (0.61 and earlier). */
class LanguageServerTest {
    private lateinit var server: MockWebServer

    @Before fun start() {
        server = MockWebServer()
        server.start()
    }

    @After fun stop() {
        server.close()
        Localization.show(null)
    }

    private fun client() = FlexarrClient(server.url("/"), apiKey = "device-token")

    @Test fun aServerBeforeLanguagesOffersNone() = runTest {
        server.enqueue(MockResponse.Builder().code(404).build())
        assertTrue(client().languages().isEmpty())
    }

    @Test fun downloadsTheMostSpecificPackNotEnglish() = runTest {
        val offered = listOf(LanguageInfo("en", "English"), LanguageInfo("en-XA", "Pseudo", version = 3))
        val asked = mutableListOf<String>()
        Localization.settle("en-XA", offered) { code -> asked += code; LanguagePack(code, "Pseudo", version = 3, messages = mapOf("Play" to "⟦Ƥĺàý⟧")) }
        assertEquals(listOf("en-XA"), asked)
        assertEquals("⟦Ƥĺàý⟧", t("Play"))
    }

    @Test fun sendsTheLanguageShownAsAcceptLanguage() = runTest {
        Localization.show(LanguagePack("en-XB", "Test"))
        server.enqueue(MockResponse.Builder().code(404).build())
        client().languages()
        assertEquals("en-XB", server.takeRequest().headers["Accept-Language"])
    }

    /** A scratch files folder as the app's, with the languages folder inside it; put back afterwards. */
    private fun withCache(work: suspend (File) -> Unit) = runTest {
        val root = Files.createTempDirectory("flexarr-lang").toFile()
        val before = Localization.cacheDir
        Localization.cacheDir = File(root, "files").apply { mkdirs() }
        try {
            work(root)
        } finally {
            Localization.cacheDir = before
            root.deleteRecursively()
        }
    }

    @Test fun aPackNamingAnotherCodeIsNotKeptOrShown() = withCache { root ->
        val offered = listOf(LanguageInfo("de", "Deutsch", version = 9))
        Localization.settle("de", offered) { LanguagePack("../../escaped", "Evil", version = 99, messages = mapOf("Play" to "pwned")) }
        assertEquals("Play", t("Play"))
        assertFalse(File(root, "escaped.json").exists())
        assertFalse(File(root, "files/escaped.json").exists())
        assertTrue(File(root, "files/languages").listFiles().orEmpty().none { it.name.endsWith(".json") })
    }

    @Test fun aCodeThatIsNotOneNamesNoFile() {
        assertTrue(Localization.isFileSafe("de"))
        assertTrue(Localization.isFileSafe("pt-BR"))
        assertTrue(Localization.isFileSafe("zh-Hans"))
        assertTrue(Localization.isFileSafe("es-419"))
        assertTrue(Localization.isFileSafe("en-XA"))
        assertFalse(Localization.isFileSafe("../de"))
        assertFalse(Localization.isFileSafe("de/../../x"))
        assertFalse(Localization.isFileSafe(""))
        assertNull(Localization.newestOnDevice("../../x"))
    }

    @Test fun signingOutForgetsDownloadedPacks() = withCache { root ->
        val offered = listOf(LanguageInfo("de", "Deutsch", version = 9_999_999))
        Localization.settle("de", offered) { code -> LanguagePack(code, "Deutsch", version = 9_999_999, messages = mapOf("Play" to "Abspielen")) }
        assertTrue(File(root, "files/languages/de.json").exists())
        Localization.lastShown = "de"
        Localization.forgetDownloaded()
        assertFalse(File(root, "files/languages/de.json").exists())
        // What it showed last stays, to start in.
        assertEquals("de", Localization.lastShown)
        assertTrue((Localization.newestOnDevice("de")?.version ?: 0) < 9_999_999)
    }
}
