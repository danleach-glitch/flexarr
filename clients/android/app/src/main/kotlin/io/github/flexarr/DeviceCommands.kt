package io.github.flexarr

import android.content.Context
import android.content.Intent
import android.util.Log
import io.github.flexarr.core.DeviceCommand
import io.github.flexarr.core.DeviceCommandKind
import io.github.flexarr.core.FlexarrClient
import io.github.flexarr.core.FlexarrException
import io.github.flexarr.core.commandStream
import io.github.flexarr.core.liveChannels
import io.github.flexarr.core.podcastEpisode
import io.github.flexarr.player.LivePlayerActivity
import io.github.flexarr.player.PlayerActivity
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch

/** The player on screen now (a title or a live channel), for commands from elsewhere to pause, resume or stop. */
interface ActivePlayback {
    fun pause()
    fun resume()
    fun stop()
}

/**
 * Keeps the server's command stream open while the app is in front, so another device (an iPhone saying "play the
 * next episode on the living-room TV" to Siri), the web app or an assistant can play, pause, resume and stop here.
 * Reconnects when the connection drops, backing off; stops for good when the server says this sign-in has no stream
 * (a sign-in holding the API key itself rather than a token of its own). As the iPhone's DeviceCommandListener.
 */
class DeviceCommands(private val context: Context, private val model: AppModel, private val scope: CoroutineScope) {
    private var task: Job? = null
    private var listeningFor: FlexarrClient? = null

    /** The player showing now, if any; set by the players as they open and close. */
    var active: ActivePlayback? = null

    /** Listens on [client]'s sign-in. Called again with the same sign-in, it carries on. */
    fun start(client: FlexarrClient) {
        val current = listeningFor
        if (current != null && current.baseUrl == client.baseUrl && current.apiKey == client.apiKey && task?.isActive == true) return
        stop()
        listeningFor = client
        task = scope.launch {
            var wait = 2_000L
            while (isActive) {
                try {
                    client.commandStream().collect { command ->
                        Log.i("Flexarr", "Command ${command.kind.raw} from ${command.sentBy ?: "someone"}")
                        handle(command)
                        wait = 2_000L
                    }
                    // Ended cleanly: a newer connection from this device took over.
                    Log.i("Flexarr", "Command stream ended")
                } catch (e: CancellationException) {
                    throw e
                } catch (e: FlexarrException.Http) {
                    if (e.status == 403) {
                        Log.i("Flexarr", "This sign-in has no command stream; not listening")
                        listeningFor = null
                        return@launch
                    }
                } catch (e: Exception) {
                    Log.i("Flexarr", "Command stream dropped; retrying in ${wait / 1000}s: ${e.message}")
                }
                delay(wait)
                wait = (wait * 2).coerceAtMost(60_000L)
            }
        }
    }

    fun stop() {
        task?.cancel()
        task = null
        listeningFor = null
    }

    /** Does what another device, the web app or an assistant asked: play a title here, or pause, resume or stop. */
    private fun handle(command: DeviceCommand) {
        val client = model.client ?: return
        when (command.kind) {
            DeviceCommandKind.Play -> {
                val id = command.itemId ?: return
                // A podcast episode (the command carries only its id) plays in the podcast player; anything else as video.
                scope.launch {
                    val episode = runCatching { client.podcastEpisode(id) }.getOrNull()
                    if (episode != null) {
                        model.podcasts.play(episode)
                    } else {
                        open(PlayerActivity.intent(context, id, fromBeginning = command.fromBeginning, shuffle = command.shuffle))
                    }
                }
            }
            DeviceCommandKind.Watch -> {
                val id = command.channelId ?: return
                // Every channel to change through, favourites first.
                scope.launch {
                    val channels = runCatching { client.liveChannels() }.getOrNull() ?: return@launch
                    val channel = channels.firstOrNull { it.id == id } ?: return@launch
                    open(LivePlayerActivity.intent(context, channel, channels.filter { it.isFavourite } + channels.filterNot { it.isFavourite }))
                }
            }
            // With no picture up, what's playing is music or a podcast.
            DeviceCommandKind.Pause -> active?.pause() ?: pauseAudio()
            DeviceCommandKind.Resume -> active?.resume() ?: resumeAudio()
            DeviceCommandKind.Stop -> active?.stop() ?: pauseAudio()
            DeviceCommandKind.Unknown -> Unit
        }
    }

    private fun pauseAudio() {
        model.music.pause()
        model.podcasts.pause()
    }

    private fun resumeAudio() {
        if (model.music.current != null) model.music.resume() else if (model.podcasts.current != null && !model.podcasts.player.isPlaying) model.podcasts.togglePlayPause()
    }

    private fun open(intent: Intent) {
        context.startActivity(intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
    }
}
