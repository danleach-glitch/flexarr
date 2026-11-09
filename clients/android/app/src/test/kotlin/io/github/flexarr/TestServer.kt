package io.github.flexarr

import android.app.Application
import android.os.Looper
import androidx.work.Configuration
import androidx.work.WorkManager
import io.github.flexarr.data.ServerCredentials
import kotlinx.coroutines.cancel
import mockwebserver3.Dispatcher
import mockwebserver3.MockResponse
import mockwebserver3.MockWebServer
import mockwebserver3.RecordedRequest
import org.robolectric.RuntimeEnvironment
import org.robolectric.Shadows.shadowOf
import java.time.Duration
import java.util.concurrent.CopyOnWriteArrayList

/** A stand-in Flexarr server and the app's real [AppModel] signed in to it, for tests that go through the real paths. */
class TestServer(private val answer: (RecordedRequest) -> MockResponse?) : AutoCloseable {
    val requests = CopyOnWriteArrayList<RecordedRequest>()
    private val server = MockWebServer().apply {
        dispatcher = object : Dispatcher() {
            override fun dispatch(request: RecordedRequest): MockResponse {
                requests += request
                return answer(request) ?: MockResponse.Builder().code(404).build()
            }
        }
        start()
    }

    val model: AppModel by lazy {
        val app = RuntimeEnvironment.getApplication() as Application
        runCatching { WorkManager.initialize(app, Configuration.Builder().build()) }
        AppModel(app, ServerCredentials(server.url("/").toString(), "key"))
    }

    fun requestsTo(path: String) = requests.filter { it.url.encodedPath.endsWith(path) }

    /** Lets the app run, a moment of real and pretend time at once, until [done]. */
    fun waitUntil(what: String = "it", done: () -> Boolean) {
        repeat(4_000) {
            if (done()) return
            shadowOf(Looper.getMainLooper()).idleFor(Duration.ofMillis(100))
            Thread.sleep(2)
        }
        throw AssertionError("Waited for $what")
    }

    override fun close() {
        runCatching { model.scope.cancel() }
        server.close()
    }

    companion object {
        fun json(body: String, code: Int = 200): MockResponse =
            MockResponse.Builder().code(code).addHeader("Content-Type", "application/json").body(body).build()
    }
}
