package io.github.flexarr

import io.github.flexarr.core.FlexarrClient
import kotlinx.serialization.serializer

/** One of the API's real answers shared with FlexarrKit's tests (Fixtures/[name].json), read as the app reads it. */
inline fun <reified T> fixture(name: String): T {
    val text = object {}.javaClass.classLoader!!.getResource("$name.json")!!.readText()
    return FlexarrClient.json.decodeFromString(serializer<T>(), text)
}
