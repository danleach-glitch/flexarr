package io.github.flexarr

import io.github.flexarr.logging.FlexarrLog
import android.content.Context
import android.content.Intent
import io.github.flexarr.core.DeviceCommand
import io.github.flexarr.core.DeviceCommandKind
import io.github.flexarr.core.DeviceEvent
import io.github.flexarr.core.FlexarrClient
import io.github.flexarr.core.FlexarrException
import io.github.flexarr.core.deviceEvents
import io.github.flexarr.core.liveChannels
import io.github.flexarr.core.podcastEpisode
import io.github.flexarr.data.SoundSettings
import io.github.flexarr.player.LivePlayerActivity
import io.github.flexarr.player.PlayerActivity
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch

private val flexarrLog = FlexarrLog("commands")

/** The player on screen now (a title or a live channel), for commands from elsewhere to pause, resume or stop. */
interface ActivePlayback {
    fun pause()
    fun resume()
    fun stop()
}

/**
 * Keeps the server's command stream open while the app is in front, so another device (an iPhone saying "play the
 * next episode on the living-room TV" to Siri), the web app or an assistant can play, pause, resume and stop here.
 * For the main profile it also brings devices asking to connect ([PairingPrompts]).
 * Reconnects when the connection drops, backing off; stops for good when the server says this sign-in has no stream
 * (a sign-in holding the API key itself rather than a token of its own). As the iPhone's DeviceCommandListener.
 */
class DeviceCommands(private val context: Context, private val model: AppModel, private val scope: CoroutineScope) {
    private var task: Job? = null
    private var listeningFor: FlexarrClient? = null

    /** The player showing now, if any; set by the players as they open and close. */
    var active: ActivePlayback? = null

    /**
     * Listens on [client]'s sign-in. Called again with the same sign-in, it carries on; as another profile, it starts
     * again, as only the main profile hears about devices asking to connect.
     */
    fun start(client: FlexarrClient) {
        val current = listeningFor
        if (current != null && current.baseUrl == client.baseUrl && current.apiKey == client.apiKey && current.profileToken == client.profileToken &&
            task?.isActive == true
        ) return
        stop()
        listeningFor = client
        task = scope.launch {
            var wait = 2_000L
            while (isActive) {
                try {
                    client.deviceEvents().collect { event ->
                        when (event) {
                            is DeviceEvent.Command -> {
                                val command = event.command
                                flexarrLog.info("Command ${command.kind.raw} from ${command.sentBy ?: "someone"}")
                                handle(command)
                            }
                            is DeviceEvent.Pairing -> model.pairing.arrived(event.device)
                            is DeviceEvent.PairingEnded -> model.pairing.ended(event.code)
                        }
                        wait = 2_000L
                    }
                    // Ended cleanly: a newer connection from this device took over.
                    flexarrLog.info("Command stream ended")
                } catch (e: CancellationException) {
                    throw e
                } catch (e: FlexarrException.Http) {
                    if (e.status == 403) {
                        flexarrLog.info("This sign-in has no command stream; not listening")
                        listeningFor = null
                        return@launch
                    }
                } catch (e: Exception) {
                    flexarrLog.info("Command stream dropped; retrying in ${wait / 1000}s: ${e.message}")
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

    /** Does what another device, the web app or an assistant asked: play a title here, pause, resume or stop, or change the sound. */
    private fun handle(command: DeviceCommand) {
        if (model.client == null) return
        when (command.kind) {
            DeviceCommandKind.Play -> {
                val id = command.itemId ?: return
                scope.launch {
                    if (!switchProfile(command.profileId, id)) return@launch
                    val client = model.client ?: return@launch
                    // A podcast episode (the command carries only its id) plays in the podcast player; anything else as video.
                    val episode = runCatching { client.podcastEpisode(id) }.getOrNull()
                    if (episode != null) {
                        // A TV has no podcasts to show it playing, or to stop it from.
                        if (!context.usesTvInterface) model.podcasts.play(episode)
                    } else {
                        open(PlayerActivity.intent(context, id, fromBeginning = command.fromBeginning, shuffle = command.shuffle))
                    }
                }
            }
            DeviceCommandKind.Watch -> {
                val id = command.channelId ?: return
                val client = model.client ?: return
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
            DeviceCommandKind.Sound -> {
                // A player that's open follows the change, as it does one made in Settings.
                val sound = command.sound ?: return
                SoundSettings.update(context) { now ->
                    now.copy(
                        dialogueBoost = SoundSettings.DialogueBoost.entries.firstOrNull { it.name == sound.dialogueBoost } ?: now.dialogueBoost,
                        nightMode = SoundSettings.NightMode.entries.firstOrNull { it.name == sound.nightMode } ?: now.nightMode,
                        speakers = SoundSettings.Speakers.entries.firstOrNull { it.name == sound.speakers } ?: now.speakers,
                    )
                }
            }
            DeviceCommandKind.Unknown -> Unit
        }
    }

    /**
     * A play command for another profile ([profileId]) switches to it first, as the iPhone does: straight away without
     * a PIN. One with a PIN goes back to "Who's watching?", and the title plays once it's chosen (as a link would). An
     * unknown profile, or the one watching, plays as things are. False when the title isn't to be played now.
     */
    private suspend fun switchProfile(profileId: String?, itemId: String): Boolean {
        if (profileId == null || profileId == model.currentProfile?.id) return true
        val profile = model.profiles.firstOrNull { it.id == profileId } ?: return true
        if (!profile.takesPin) {
            return runCatching { model.chooseProfile(profile) }
                .onFailure { flexarrLog.warning("Couldn't switch to the profile a command asked for", it) }
                .isSuccess
        }
        active?.stop()
        model.pendingLink = AppLink.Play(itemId)
        model.switchProfile()
        return false
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
