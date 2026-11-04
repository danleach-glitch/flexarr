package io.github.flexarr.core.i18n

import io.github.flexarr.core.FlexarrClient
import kotlinx.coroutines.test.runTest
import mockwebserver3.MockResponse
import mockwebserver3.MockWebServer
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test

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
}
