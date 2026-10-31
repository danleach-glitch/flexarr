package io.github.flexarr.player

import android.hardware.Sensor
import android.hardware.SensorEvent
import android.hardware.SensorEventListener
import android.hardware.SensorManager
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.ui.platform.LocalContext
import kotlin.math.sqrt

/**
 * A shake of the phone answers the sleep timer, as on iPhone: yes to "Still watching?", and awake once it has run out.
 * The accelerometer is only listened to while there's something to answer, so it costs nothing the rest of the time.
 */
@Composable
internal fun ShakeForSleep(sleep: VideoSleep) {
    val context = LocalContext.current
    val listening = sleep.phase != VideoSleep.Phase.Off
    val current by rememberUpdatedState(sleep)
    DisposableEffect(listening) {
        val sensors = context.getSystemService(SensorManager::class.java)
        val accelerometer = sensors?.getDefaultSensor(Sensor.TYPE_ACCELEROMETER)
        if (!listening || sensors == null || accelerometer == null) return@DisposableEffect onDispose {}
        val shakes = ShakeListener { current.shaken() }
        sensors.registerListener(shakes, accelerometer, SensorManager.SENSOR_DELAY_UI)
        onDispose { sensors.unregisterListener(shakes) }
    }
}

/**
 * A shake: a few hard jolts (well over gravity) close together, as UIKit counts one, so setting the phone down or
 * turning over in bed isn't. Once heard, the next is a second later at the earliest.
 */
internal class ShakeListener(private val onShake: () -> Unit) : SensorEventListener {
    private var jolts = 0
    private var firstJolt = 0L
    private var lastShake = 0L

    override fun onSensorChanged(event: SensorEvent) {
        val (x, y, z) = event.values
        val force = sqrt(x * x + y * y + z * z) / SensorManager.GRAVITY_EARTH
        if (force < JOLT_FORCE) return
        val now = event.timestamp / 1_000_000
        if (now - firstJolt > WINDOW_MS) {
            firstJolt = now
            jolts = 0
        }
        if (++jolts < JOLTS || now - lastShake < QUIET_MS) return
        jolts = 0
        lastShake = now
        onShake()
    }

    override fun onAccuracyChanged(sensor: Sensor?, accuracy: Int) = Unit

    private companion object {
        const val JOLT_FORCE = 2.5f
        const val JOLTS = 3
        const val WINDOW_MS = 800L
        const val QUIET_MS = 1_000L
    }
}
