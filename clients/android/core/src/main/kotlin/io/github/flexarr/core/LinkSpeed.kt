package io.github.flexarr.core

import kotlinx.serialization.Serializable
import kotlinx.serialization.builtins.ListSerializer
import kotlinx.serialization.builtins.MapSerializer
import kotlinx.serialization.builtins.serializer
import kotlinx.serialization.json.Json

/**
 * How fast data reaches this device from the server lately, for the start request's `measuredBitrate`
 * (docs/remote-streaming.md): readings of the video stream's segments as they arrive, and the speed test's. Kept per
 * server and per address in use (home, the away address, a guest's tunnel: see [key]), and saved ([Memory]) so the
 * first play after opening the app has something to go on.
 *
 * A reading is fresh for half an hour, then only a starting point for a day. With a few fresh readings (or a speed
 * test among them), their median (the lower middle one when there's an even number), so a link that swings is judged
 * by a typical reading, not its good moments (#539); without, the same of the day's readings; with none, nothing.
 */
class LinkSpeed(private val now: () -> Long = System::currentTimeMillis, private val memory: Memory? = null) {
    /** Where the readings are kept between launches (the app's preferences). */
    interface Memory {
        fun read(): String?
        fun write(text: String)
    }

    @Serializable
    internal data class Reading(val bps: Long, val at: Long, val test: Boolean = false)

    private val readings: MutableMap<String, ArrayDeque<Reading>> by lazy { load() }

    /** When each place was last speed tested this launch (see [testDue]). */
    private val testedAt = mutableMapOf<String, Long>()

    /** Notes [bytes] of a segment arriving over [millis] at [key]. */
    @Synchronized
    fun sample(key: String, bytes: Long, millis: Long) {
        if (bytes < MIN_SAMPLE_BYTES || millis <= 0) return
        add(key, Reading(bytes * 8 * 1000 / millis, now()))
    }

    /** Notes a speed test at [key] that measured [bitsPerSecond]. */
    @Synchronized
    fun tested(key: String, bitsPerSecond: Long) {
        if (bitsPerSecond <= 0) return
        add(key, Reading(bitsPerSecond, now(), test = true))
    }

    private fun add(key: String, reading: Reading) {
        val list = readings.getOrPut(key) { ArrayDeque() }
        list.removeAll { reading.at - it.at >= KEPT_MS }
        list.addLast(reading)
        while (list.size > KEPT) list.removeFirst()
        // Only the places used most lately.
        if (readings.size > PLACES) {
            readings.entries.minByOrNull { it.value.lastOrNull()?.at ?: 0L }?.key?.let(readings::remove)
        }
        save()
    }

    /** The speed in bits per second at [key]: the fresh readings', else the day's, else null. */
    @Synchronized
    fun current(key: String): Long? = fresh(key) ?: median(key, KEPT_MS)

    /** The speed from the last half hour's readings at [key], or null with too little to go on. */
    @Synchronized
    fun fresh(key: String): Long? = median(key, FRESH_MS)

    private fun median(key: String, within: Long): Long? {
        val at = now()
        val recent = readings[key].orEmpty().filter { at - it.at in 0 until within }
        if (recent.size < MIN_SAMPLES && recent.none { it.test }) return null
        val sorted = recent.map { it.bps }.sorted()
        return sorted[(sorted.size - 1) / 2]
    }

    /**
     * Whether to run a speed test at [key] now: not within [TEST_INTERVAL_MS] of the last one (unless the network has
     * changed since); for the title page ([onlyIfStale]), only with no fresh reading.
     */
    @Synchronized
    fun testDue(key: String, onlyIfStale: Boolean = false): Boolean {
        if (onlyIfStale && fresh(key) != null) return false
        val last = testedAt[key] ?: return true
        return now() - last >= TEST_INTERVAL_MS
    }

    /** A speed test at [key] is starting: the next waits [TEST_INTERVAL_MS]. */
    @Synchronized
    fun testStarted(key: String) {
        testedAt[key] = now()
    }

    /** Another network: a speed test may run again at once. */
    @Synchronized
    fun networkChanged() {
        testedAt.clear()
    }

    private fun load(): MutableMap<String, ArrayDeque<Reading>> {
        val text = runCatching { memory?.read() }.getOrNull() ?: return mutableMapOf()
        val saved = runCatching { json.decodeFromString(serializer, text) }.getOrNull() ?: return mutableMapOf()
        val at = now()
        return saved.mapValues { (_, list) -> ArrayDeque(list.filter { at - it.at in 0 until KEPT_MS }.takeLast(KEPT)) }
            .filterValues { it.isNotEmpty() }.toMutableMap()
    }

    private fun save() {
        val memory = memory ?: return
        runCatching { memory.write(json.encodeToString(serializer, readings.mapValues { it.value.toList() })) }
    }

    companion object {
        /** Transfers smaller than this are mostly the round trip, not the speed. */
        const val MIN_SAMPLE_BYTES = 200_000L
        const val KEPT = 12

        /** Fewer segment readings than this say nothing (a speed test says something by itself). */
        const val MIN_SAMPLES = 3
        const val FRESH_MS = 30 * 60_000L

        /** How long a reading is kept, as a starting point once it isn't fresh. */
        const val KEPT_MS = 24 * 60 * 60_000L

        /** At most this often a speed test at one place, unless the network changes. */
        const val TEST_INTERVAL_MS = 10 * 60_000L

        /** The speed test's size on Wi-Fi or Ethernet, and on mobile data (or any network that counts what it carries). */
        const val TEST_BYTES_UNMETERED = 4_000_000
        const val TEST_BYTES_METERED = 1_000_000

        /** How many servers and addresses are kept. */
        const val PLACES = 8

        /**
         * Where the readings belong: the server (its home address, or a guest's [guestTarget]) and the address in
         * use now, so home, Tailscale and a guest's tunnel are each judged on their own.
         */
        fun key(client: FlexarrClient, guestTarget: String? = null): String =
            if (guestTarget != null) "guest|$guestTarget" else "${client.addresses.home}|${client.baseUrl}"

        private val json = Json { ignoreUnknownKeys = true }
        private val serializer = MapSerializer(String.serializer(), ListSerializer(Reading.serializer()))
    }
}
