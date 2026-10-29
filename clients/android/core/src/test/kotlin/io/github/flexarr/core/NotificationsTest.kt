package io.github.flexarr.core

import kotlinx.coroutines.runBlocking
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import mockwebserver3.MockResponse
import mockwebserver3.MockWebServer
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Test
import java.util.Base64

class NotificationsTest {
    @Test
    fun opensWhatCryptoKitSealed() {
        // Sealed by CryptoKit (AES.GCM.SealedBox.combined), as the iPhone and the server's AesGcm make it.
        val sealed = "AAECAwQFBgcICQoLY6OdUicrlywAkpb0jS9W2rJZwOtqAxlSc+xPmOz1VyeNCieR+KERzNbJ4dV+zZdVmFUkGKanyuAYK9Hjguw5um91eJ7zJ339Cg+F3VPsCa6IIqe/Px8dwcS8HjQpSCXJ2a5p1FGMrjqU4RaHnSk="
        val payload = PushPayload.open(sealed, ByteArray(32) { 7 })
        assertEquals("New show: Severance", payload.title)
        assertEquals("Season 1", payload.body)
        assertEquals(PushPayload.VIDEO, payload.category)
        assertEquals(mapOf("item" to "abc", "play" to "def"), payload.info)
        assertNull(payload.image)
    }

    @Test
    fun anotherKeyDoesNotOpenIt() {
        val sealed = PushPayload.seal("""{"t":"x"}""", PushPayload.newKey())
        assertThrows(Exception::class.java) { PushPayload.open(sealed, PushPayload.newKey()) }
    }

    @Test
    fun registersAsAndroidWithTheSettingsBesideTheRest() = runBlocking {
        MockWebServer().use { server ->
            server.start()
            server.enqueue(MockResponse.Builder().code(200).addHeader("Content-Type", "application/json").body("""{"profileId":"p","profileName":"Dan","films":true,"shows":false,"podcasts":true,"recordings":true,"requests":true,"upcoming":true,"upcomingHour":8,"timeZone":"Europe/London","relay":true}""").build())
            server.enqueue(MockResponse.Builder().code(404).build())
            val client = FlexarrClient(server.url("/"), apiKey = "k")
            val device = client.registerForNotifications("fcm:token_1", ByteArray(32) { 1 }, "Europe/London", NotificationSettings(shows = false), useCurrentProfile = true)
            assertEquals(NotificationSettings(shows = false, upcoming = true, upcomingHour = 8), device.settings)
            val request = server.takeRequest()
            assertEquals("PUT", request.method)
            val body = Json.parseToJsonElement(request.body!!.utf8()).jsonObject
            assertEquals("android", body["platform"]!!.jsonPrimitive.content)
            assertEquals("production", body["environment"]!!.jsonPrimitive.content)
            assertEquals(Base64.getEncoder().encodeToString(ByteArray(32) { 1 }), body["key"]!!.jsonPrimitive.content)
            assertEquals("false", body["shows"]!!.jsonPrimitive.content)
            assertEquals("true", body["useCurrentProfile"]!!.jsonPrimitive.content)
            assertNull(client.notificationDevice())
        }
    }
}
