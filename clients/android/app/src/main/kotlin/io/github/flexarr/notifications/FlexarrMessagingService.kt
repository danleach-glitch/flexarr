package io.github.flexarr.notifications

import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.graphics.Bitmap
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import androidx.core.graphics.drawable.toBitmap
import coil3.imageLoader
import coil3.request.ImageRequest
import coil3.request.SuccessResult
import coil3.request.allowHardware
import coil3.toBitmap
import com.google.firebase.messaging.FirebaseMessagingService
import com.google.firebase.messaging.RemoteMessage
import io.github.flexarr.AppLink
import io.github.flexarr.R
import io.github.flexarr.appModel
import io.github.flexarr.core.PushPayload
import io.github.flexarr.logging.FlexarrLog
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking

private val log = FlexarrLog("notifications")

/**
 * Where Firebase delivers: a new token for this phone, and sealed notifications. The relay sends a data message with no
 * alert (`{e: <sealed>}`), so the app opens it with this phone's key and shows the real title, text, artwork and
 * buttons itself; one it can't open (the key was reset) says only "Something new is ready", as on iPhone.
 */
class FlexarrMessagingService : FirebaseMessagingService() {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    override fun onNewToken(token: String) {
        scope.launch { applicationContext.appModel.notifications.received(token) }
    }

    override fun onMessageReceived(message: RemoteMessage) {
        val sealed = message.data["e"] ?: return
        val key = applicationContext.appModel.notifications.savedKey()
        val payload = key?.let { runCatching { PushPayload.open(sealed, it) }.onFailure { e -> log.warning("Couldn't open a notification", e) }.getOrNull() }
        show(this, payload ?: PushPayload(title = "Flexarr", body = "Something new is ready."))
    }

    companion object {
        private const val CHANNEL = "new"
        private const val UPCOMING_CHANNEL = "upcoming"

        /** The channels Android's settings list: what's new, and the morning list (quieter). */
        fun createChannels(context: Context) {
            val manager = context.getSystemService(NotificationManager::class.java)
            manager.createNotificationChannel(NotificationChannel(CHANNEL, "New in your library", NotificationManager.IMPORTANCE_DEFAULT).apply {
                description = "Films, shows, podcast episodes, recordings and requests as they arrive"
            })
            manager.createNotificationChannel(NotificationChannel(UPCOMING_CHANNEL, "Upcoming", NotificationManager.IMPORTANCE_LOW).apply {
                description = "Each morning, what's airing today"
            })
        }

        fun show(context: Context, payload: PushPayload) {
            if (!NotificationManagerCompat.from(context).areNotificationsEnabled()) return
            // Each its own, as on iPhone: a show's new episodes gather under its thread rather than replacing each other
            // (the same one sent twice still replaces itself).
            val id = listOf(payload.thread, payload.category, payload.title, payload.body, payload.info).hashCode()
            val info = payload.info
            val opens: AppLink = when (payload.category) {
                PushPayload.PODCAST -> info["podcast"]?.let { AppLink.Podcast(it) }
                else -> info["item"]?.let { AppLink.Item(it) }
            } ?: AppLink.Home
            val builder = NotificationCompat.Builder(context, if (payload.category == PushPayload.UPCOMING) UPCOMING_CHANNEL else CHANNEL)
                .setSmallIcon(R.drawable.ic_brand_mark)
                .setColor(0xFFA9C2EC.toInt())
                .setContentTitle(payload.title)
                .setContentText(payload.body)
                .setStyle(NotificationCompat.BigTextStyle().bigText(payload.body))
                .setAutoCancel(true)
                .setContentIntent(activity(context, id, opens))
            payload.thread?.let(builder::setGroup)
            payload.image?.let { url -> artwork(context, url) }?.let { builder.setLargeIcon(it) }

            val play: AppLink? = when (payload.category) {
                PushPayload.VIDEO, PushPayload.RECORDING -> info["play"]?.let { AppLink.Play(it) }
                PushPayload.PODCAST -> info["episode"]?.let { AppLink.PodcastEpisode(it) }
                else -> null
            }
            play?.let { builder.addAction(R.drawable.ic_pip_play, "Play", activity(context, id + 1, it)) }
            val download = when (payload.category) {
                PushPayload.VIDEO -> info["play"]
                PushPayload.PODCAST -> info["episode"]
                else -> null
            }
            download?.let { builder.addAction(0, "Download", DownloadReceiver.intent(context, id, it, podcast = payload.category == PushPayload.PODCAST)) }
            runCatching {
                NotificationManagerCompat.from(context).notify(id, builder.build())
                payload.thread?.let { thread -> NotificationManagerCompat.from(context).notify(summaryId(thread), summary(context, payload, thread, opens)) }
            }.onFailure { log.warning("Couldn't show a notification", it) }
        }

        private fun summaryId(thread: String) = "thread:$thread".hashCode()

        /** What a thread's notifications gather under (Android shows them together beneath it); it doesn't alert itself. */
        private fun summary(context: Context, payload: PushPayload, thread: String, opens: AppLink) =
            NotificationCompat.Builder(context, if (payload.category == PushPayload.UPCOMING) UPCOMING_CHANNEL else CHANNEL)
                .setSmallIcon(R.drawable.ic_brand_mark)
                .setColor(0xFFA9C2EC.toInt())
                .setContentTitle(payload.title)
                .setContentText(payload.body)
                .setGroup(thread)
                .setGroupSummary(true)
                .setGroupAlertBehavior(NotificationCompat.GROUP_ALERT_CHILDREN)
                .setAutoCancel(true)
                .setContentIntent(activity(context, summaryId(thread), opens))
                .build()

        private fun activity(context: Context, request: Int, link: AppLink): PendingIntent =
            PendingIntent.getActivity(context, request, link.intent(context), PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)

        /** The artwork, fetched now (Firebase gives the service a few seconds); none if it doesn't come. */
        private fun artwork(context: Context, url: String): Bitmap? = runBlocking {
            val request = ImageRequest.Builder(context).data(url).size(512).allowHardware(false).build()
            (context.imageLoader.execute(request) as? SuccessResult)?.image?.toBitmap()
        }
    }
}

/** A notification's Download button: asks for the download without opening the app, as on iPhone. */
class DownloadReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val id = intent.getStringExtra(ID) ?: return
        val podcast = intent.getBooleanExtra(PODCAST, false)
        NotificationManagerCompat.from(context).cancel(intent.getIntExtra(NOTIFICATION, 0))
        val pending = goAsync()
        val model = context.appModel
        model.scope.launch {
            try {
                if (podcast) model.downloads.downloadEpisode(id) else model.downloads.download(id, model.downloads.quality, automatic = false)
            } catch (e: Exception) {
                log.warning("Couldn't download from a notification", e)
            } finally {
                pending.finish()
            }
        }
    }

    companion object {
        private const val ID = "id"
        private const val PODCAST = "podcast"
        private const val NOTIFICATION = "notification"

        fun intent(context: Context, notification: Int, id: String, podcast: Boolean): PendingIntent =
            PendingIntent.getBroadcast(
                context,
                notification + 2,
                Intent(context, DownloadReceiver::class.java).putExtra(ID, id).putExtra(PODCAST, podcast).putExtra(NOTIFICATION, notification),
                PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
            )
    }
}
