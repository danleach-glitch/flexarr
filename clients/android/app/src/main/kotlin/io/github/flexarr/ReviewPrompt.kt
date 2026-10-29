package io.github.flexarr

import android.app.Activity
import android.content.ActivityNotFoundException
import android.content.Context
import android.content.Intent
import android.content.SharedPreferences
import androidx.core.net.toUri
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.core.content.edit
import com.google.android.play.core.review.ReviewManagerFactory
import com.google.android.play.core.ktx.launchReview
import com.google.android.play.core.ktx.requestReview
import io.github.flexarr.core.ReviewRules
import io.github.flexarr.logging.FlexarrLog
import kotlinx.coroutines.CancellationException

private val log = FlexarrLog("review")

/**
 * Google Play's "Enjoying Flexarr?" rating prompt, asked for when [ReviewRules] say (after a title is watched to the
 * end and the player has closed), and Rate Flexarr in Settings. Without Google Play (or its services) neither does
 * anything worse than open the store's web page.
 */
object ReviewPrompt {
    /** The store listing, for Rate Flexarr. */
    private const val PACKAGE = "com.danleach.flexarr"

    private var rules: ReviewRules? = null

    /** Set when the prompt should come up now (Home asks once the app is in front); [ask] clears it. */
    var isDue by mutableStateOf(false)
        private set

    /** At launch: the first time the app opened starts the week's wait. */
    fun start(context: Context) {
        val prefs = context.applicationContext.getSharedPreferences("reviewPrompt", Context.MODE_PRIVATE)
        rules = ReviewRules(PreferencesStore(prefs), BuildConfig.VERSION_NAME).also { it.opened() }
    }

    /**
     * A film or episode was watched into its credits (with history on). [closing]: the player is going away, so it's
     * a good moment to ask; false when it carries on to the next episode.
     */
    fun titleFinished(closing: Boolean = true) {
        val rules = rules ?: return
        if (rules.watchedToEnd(closing)) isDue = true
    }

    /** Asks Google Play for the prompt over [activity]. Google decides whether it shows; not again this version either way. */
    suspend fun ask(activity: Activity) {
        if (!isDue) return
        isDue = false
        rules?.asked()
        try {
            val manager = ReviewManagerFactory.create(activity)
            manager.launchReview(activity, manager.requestReview())
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            // No Google Play on this device, or it said no: nothing to show.
            log.info("No rating prompt", e)
        }
    }

    /** Rate Flexarr: the Play Store app's page for Flexarr, or the web's when there's no store app. */
    fun openStoreListing(context: Context) {
        try {
            context.startActivity(Intent(Intent.ACTION_VIEW, "market://details?id=$PACKAGE".toUri()).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
        } catch (_: ActivityNotFoundException) {
            runCatching {
                context.startActivity(Intent(Intent.ACTION_VIEW, "https://play.google.com/store/apps/details?id=$PACKAGE".toUri()).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
            }
        }
    }

    private class PreferencesStore(private val prefs: SharedPreferences) : ReviewRules.Store {
        override var firstOpened: Long?
            get() = prefs.getLong(FIRST_OPENED, 0).takeIf { it > 0 }
            set(value) = prefs.edit { if (value == null) remove(FIRST_OPENED) else putLong(FIRST_OPENED, value) }
        override var watches: Int
            get() = prefs.getInt(WATCHES, 0)
            set(value) = prefs.edit { putInt(WATCHES, value) }
        override var days: List<String>
            get() = prefs.getString(DAYS, null)?.split(',')?.filter { it.isNotEmpty() }.orEmpty()
            set(value) = prefs.edit { putString(DAYS, value.joinToString(",")) }
        override var askedVersion: String?
            get() = prefs.getString(ASKED_VERSION, null)
            set(value) = prefs.edit { putString(ASKED_VERSION, value) }
    }

    private const val FIRST_OPENED = "firstOpened"
    private const val WATCHES = "watches"
    private const val DAYS = "days"
    private const val ASKED_VERSION = "askedVersion"
}
