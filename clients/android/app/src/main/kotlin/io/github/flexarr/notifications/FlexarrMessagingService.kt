package io.github.flexarr.notifications

import android.Manifest
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.graphics.Bitmap
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import androidx.core.content.ContextCompat
import androidx.core.graphics.drawable.toBitmap
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.ProcessLifecycleOwner
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
import io.github.flexarr.core.FlexarrException
import io.github.flexarr.core.PushPayload
import io.github.flexarr.logging.FlexarrLog
import io.github.flexarr.ui.pairing.pairingProblem
import kotlinx.coroutines.CancellationException
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
        private const val DEVICES_CHANNEL = "devices"

        /** The channels Android's settings list: what's new, the morning list (quieter), and devices asking to connect (urgent). */
        fun createChannels(context: Context) {
            val manager = context.getSystemService(NotificationManager::class.java)
            manager.createNotificationChannel(NotificationChannel(CHANNEL, "New in your library", NotificationManager.IMPORTANCE_DEFAULT).apply {
                description = "Films, shows, podcast episodes, recordings and requests as they arrive"
            })
            manager.createNotificationChannel(NotificationChannel(UPCOMING_CHANNEL, "Upcoming", NotificationManager.IMPORTANCE_LOW).apply {
                description = "Each morning, what's airing today"
            })
            manager.createNotificationChannel(NotificationChannel(DEVICES_CHANNEL, "Devices asking to connect", NotificationManager.IMPORTANCE_HIGH).apply {
                description = "A TV, phone or tablet waiting for you to let it in"
            })
        }

        fun show(context: Context, payload: PushPayload) {
            if (!NotificationManagerCompat.from(context).areNotificationsEnabled()) return
            if (payload.category == PushPayload.PAIRING) return showPairing(context, payload)
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

        private fun pairingId(code: String) = "pairing:$code".hashCode()

        /**
         * A device asking to connect: Allow and Don't allow answer from the notification (on a locked phone, once it's
         * unlocked), and a tap opens the app straight to the prompt. Not shown while the app is asking about it already,
         * and taken away once it's let in, turned away or its code runs out.
         */
        private fun showPairing(context: Context, payload: PushPayload) {
            if (Build.VERSION.SDK_INT >= 33 && ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) return
            val code = payload.info["pairing"]
            val model = context.appModel
            val inFront = ProcessLifecycleOwner.get().lifecycle.currentState.isAtLeast(Lifecycle.State.STARTED)
            if (code != null && inFront && model.isMainProfile && model.pairing.isWaiting(code)) return
            val opens = code?.let { AppLink.Pairing(it) } ?: AppLink.Home
            val id = code?.let(::pairingId) ?: listOf(payload.category, payload.title, payload.body).hashCode()
            val builder = NotificationCompat.Builder(context, DEVICES_CHANNEL)
                .setSmallIcon(R.drawable.ic_brand_mark)
                .setColor(0xFFA9C2EC.toInt())
                .setContentTitle(payload.title)
                .setContentText(payload.body)
                .setStyle(NotificationCompat.BigTextStyle().bigText(payload.body))
                .setPriority(NotificationCompat.PRIORITY_HIGH)
                .setCategory(NotificationCompat.CATEGORY_STATUS)
                .setAutoCancel(true)
                // Codes last minutes, not hours.
                .setTimeoutAfter(15 * 60 * 1000L)
                .setContentIntent(activity(context, id, opens))
            if (code != null) {
                // Its name, for "“Living room” is connected": as the app heard it, else from the title the server wrote.
                val name = model.pairing.waiting.firstOrNull { it.code == code }?.deviceName
                    ?: payload.title.removeSuffix(" wants to connect").takeIf { it != payload.title && it.isNotBlank() }
                builder.addAction(PairingReceiver.action(context, "Allow", code, name, allow = true))
                builder.addAction(PairingReceiver.action(context, "Don’t allow", code, name, allow = false))
            }
            runCatching { NotificationManagerCompat.from(context).notify(id, builder.build()) }
                .onFailure { log.warning("Couldn't show a notification", it) }
        }

        /**
         * What came of Allow on a pairing notification, in its place: connected, or why not (a tap opens the app, to
         * try there). Its own id, so the device leaving the waiting list doesn't take it away too.
         */
        internal fun showPairingAnswer(context: Context, code: String, title: String, text: String, opens: AppLink) {
            cancelPairing(context, code)
            if (Build.VERSION.SDK_INT >= 33 && ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) return
            val id = "pairing-answer:$code".hashCode()
            val notification = NotificationCompat.Builder(context, DEVICES_CHANNEL)
                .setSmallIcon(R.drawable.ic_brand_mark)
                .setColor(0xFFA9C2EC.toInt())
                .setContentTitle(title)
                .setContentText(text)
                .setStyle(NotificationCompat.BigTextStyle().bigText(text))
                .setCategory(NotificationCompat.CATEGORY_STATUS)
                // Said once, without sounding again.
                .setOnlyAlertOnce(true)
                .setSilent(true)
                .setAutoCancel(true)
                .setTimeoutAfter(15 * 60 * 1000L)
                .setContentIntent(activity(context, id, opens))
                .build()
            runCatching { NotificationManagerCompat.from(context).notify(id, notification) }
                .onFailure { log.warning("Couldn't show a notification", it) }
        }

        /** Takes away the notification about the device showing [code], if there is one. */
        fun cancelPairing(context: Context, code: String) {
            NotificationManagerCompat.from(context).cancel(pairingId(code))
        }

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

/**
 * A pairing notification's Allow and Don't allow: answered with this phone's sign-in without opening the app, as on
 * iPhone. On Android 12 and later a locked phone asks to be unlocked first. Allow says what came of it in the
 * notification's place; Don't allow just takes it away. While managing the server is locked here, Allow can't be
 * answered from outside (the main profile's PIN has to be typed), so it says to open the prompt, which asks for it.
 */
class PairingReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val code = intent.getStringExtra(CODE) ?: return
        val allow = intent.getBooleanExtra(ALLOW, false)
        val name = intent.getStringExtra(NAME)
        val model = context.appModel
        val client = model.client
        FlexarrMessagingService.cancelPairing(context, code)
        if (client == null) {
            if (allow) FlexarrMessagingService.showPairingAnswer(context, code, "Couldn’t allow it", "Open Flexarr to allow it.", AppLink.Home)
            return
        }
        val pending = goAsync()
        model.scope.launch {
            try {
                if (allow) {
                    model.pairing.allow(client, code)
                    model.pairing.finished(code)
                    val title = name?.let { "“$it” is connected" } ?: "It’s connected"
                    FlexarrMessagingService.showPairingAnswer(context, code, title, "It’ll be ready in a moment.", AppLink.Home)
                } else {
                    model.pairing.decline(client, code)
                }
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                log.warning("Couldn't answer a device asking to connect", e)
                if (allow) {
                    val status = (e as? FlexarrException.Http)?.status
                    // Refused: the main profile while locked (its PIN unlocks it, in the app), or another profile.
                    if (status == 403) model.loadSignIn()
                    val needsPin = status == 403 && model.canUnlock
                    // Gone or not this profile's to allow: the app, rather than the prompt for a code that's no use.
                    val retry = needsPin || status !in setOf(403, 404)
                    val title = name?.let { "Couldn’t allow “$it”" } ?: "Couldn’t allow it"
                    val text = if (needsPin) "Open Flexarr to allow it with the main profile’s PIN." else pairingProblem(e, outside = true)
                    FlexarrMessagingService.showPairingAnswer(context, code, title, text, if (retry) AppLink.Pairing(code) else AppLink.Home)
                }
            } finally {
                pending.finish()
            }
        }
    }

    companion object {
        private const val CODE = "code"
        private const val NAME = "name"
        private const val ALLOW = "allow"

        fun action(context: Context, title: String, code: String, name: String?, allow: Boolean): NotificationCompat.Action {
            val intent = Intent(context, PairingReceiver::class.java).putExtra(CODE, code).putExtra(ALLOW, allow).putExtra(NAME, name)
            // One per code and answer, so each notification's buttons carry their own.
            val request = "pairing:$code:${if (allow) "allow" else "decline"}".hashCode()
            val pending = PendingIntent.getBroadcast(context, request, intent, PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
            return NotificationCompat.Action.Builder(0, title, pending)
                .setAuthenticationRequired(true)
                .build()
        }
    }
}
