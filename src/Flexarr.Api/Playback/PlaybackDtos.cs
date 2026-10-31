using Flexarr.Api.Endpoints;
using Flexarr.Core.Media;
using Flexarr.Streaming;

namespace Flexarr.Api.Playback;

public sealed record PlaybackRequest(
    Guid ItemId,
    DeviceProfile Profile,
    Guid? FileId = null,
    int? AudioStreamIndex = null,
    string? SubtitleKey = null,
    long? MaxBitrate = null,
    int? MaxHeight = null,
    double? StartPositionSeconds = null,
    bool ForceTranscode = false,
    // The session this one takes over from (an audio switch, a recovery), ended before this one starts.
    string? ReplacesSessionId = null,
    // Shuffle a show: with the show's id, a random episode starts; with an episode's, Next is a random one.
    bool Shuffle = false,
    // Getting the next episode ready while this one's credits play: conversion starts now, so it opens at once. It
    // never takes another screen's place; with the server full, it's refused (503) and the player starts normally.
    bool Prepare = false,
    // The listener's dialogue boost and night mode, applied by the server when it converts the sound (the player can't
    // shape a stream it doesn't decode itself). Left out, the sound is as the file has it.
    SoundShaping? Sound = null,
    // A name for the device ("Dan's iPhone"), for a book's "Jump to where you were on …". Absent from apps before 0.58.
    string? DeviceName = null);

public sealed record AudioTrackDto(int Index, string Label, string Codec, string? Language, int? Channels, bool IsDefault);

/// <param name="IsExternal">A subtitle file beside the video, which Bazarr can re-sync, rather than one inside it.</param>
/// <param name="StyledUrl">Styled subtitles (ASS/SSA) as authored, for players that draw them with their fonts and positions.</param>
public sealed record SubtitleTrackDto(string Key, string Label, string Codec, string? Language, bool IsForced, bool IsDefault, bool IsText, string? Url,
    bool IsExternal = false, string? StyledUrl = null);

public sealed record MarkerDto(MarkerType Type, double Start, double End);

/// <summary>The subtitles the viewer picked in the player: a track's key, or "off".</summary>
public sealed record SubtitleChoiceRequest(string Key);

/// <summary>The fonts a file's styled subtitles use (attached to it, as MKVs carry them), by name.</summary>
public sealed record SubtitleFontDto(string Name, string Url);

public sealed record PlaybackInfoDto(
    string SessionId,
    PlaybackMethod Method,
    string Url,
    IReadOnlyList<string> Reasons,
    double DurationSeconds,
    double StartPositionSeconds,
    Guid FileId,
    IReadOnlyList<AudioTrackDto> AudioTracks,
    int? SelectedAudioIndex,
    IReadOnlyList<SubtitleTrackDto> Subtitles,
    string? SelectedSubtitleKey,
    bool SubtitleBurnedIn,
    IReadOnlyList<MarkerDto> Markers,
    ItemSummaryDto Item,
    IReadOnlyList<ItemSummaryDto> Ancestors,
    ItemSummaryDto? Next,
    string? TranscodeSummary,
    bool Shuffle = false,
    PlaybackPreferencesDto? Preferences = null,
    double SubtitleDelaySeconds = 0,
    ItemSummaryDto? Previous = null,
    double AudioDelaySeconds = 0,
    bool AudioDelayInStream = false,
    double? Speed = null,
    bool CanSearchSubtitles = false,
    string? FontsUrl = null,
    bool SoundShaped = false,
    // A book's files on its timeline, chapters, bookmarks and place. Absent from servers before 0.58.
    Books.BookManifestDto? Book = null)
{
    /// <summary>
    /// Names this playback in logs without giving away <see cref="SessionId"/>, which works as a password for the
    /// stream. The apps attach it to what they report, and the server's own lines show it as #playbackId.
    /// </summary>
    public string PlaybackId => Logging.Redactor.ShortHash(SessionId);
}

/// <summary>How this profile likes to watch: auto-play the next episode, and what to skip without asking.</summary>
/// <param name="SubtitleSize">Left out (an older app saving its toggles), the profile's size is kept; the same for the rest.</param>
/// <param name="SubtitlePosition">How high subtitles sit over the picture.</param>
/// <param name="SubtitlesInBlackBars">In films wider than the screen, subtitles go in the black bar below the picture.</param>
public sealed record PlaybackPreferencesDto(bool AutoPlayNext, bool SkipIntros, bool SkipRecaps, bool SkipCredits, SubtitleSize? SubtitleSize = null,
    SubtitlePosition? SubtitlePosition = null, bool? SubtitlesInBlackBars = null);

/// <summary>Subtitles shown later (positive) or earlier (negative), in seconds.</summary>
public sealed record SubtitleDelayRequest(double Seconds);

/// <summary>A file's sound played later (positive) or earlier (negative), in seconds, for everyone.</summary>
public sealed record AudioDelayRequest(double Seconds);

/// <summary>The speed to play a show at from now on: 1 forgets it.</summary>
public sealed record PlaybackSpeedRequest(double Speed);

/// <summary>The button a player is offering over the picture, for a remote to offer too.</summary>
public enum PlayerPrompt
{
    SkipIntro,
    SkipRecap,
    NextEpisode,
}

/// <summary>A soundtrack or subtitle track in a player, as it names it.</summary>
/// <param name="Id">The player's own id for it, to choose it with a device command.</param>
public sealed record PlayerTrack(string Id, string Label, bool Selected);

/// <summary>The tracks a player can switch between. No subtitle selected: they're off.</summary>
public sealed record PlayerTracks(IReadOnlyList<PlayerTrack> Audio, IReadOnlyList<PlayerTrack> Subtitles);

/// <summary>
/// Where a player has got to. Apple TV apps from 0.56 add what a remote on another device needs to follow along; the
/// rest leave it out, and whatever they last said stands.
/// </summary>
/// <param name="Rate">The speed it's playing at (1 is normal).</param>
/// <param name="Prompt">The button over the picture right now: absent, none.</param>
/// <param name="HasNext">There's a next episode to move on to.</param>
/// <param name="Prompt">The button over the picture right now (a <see cref="PlayerPrompt"/> name): absent, or one this server doesn't know, none.</param>
/// <param name="Tracks">Its soundtracks and subtitles: sent when they change, absent otherwise.</param>
/// <param name="Sound">Its sound settings: sent when they change, absent otherwise.</param>
/// <remarks>
/// The prompt and sound come as plain text, not enums: a newer app's new value must never make an older server refuse the
/// report, which carries the watching history.
/// </remarks>
public sealed record ProgressRequest(double PositionSeconds, bool Paused = false, bool Ended = false, double? Rate = null, string? Prompt = null,
    bool? HasNext = null, PlayerTracks? Tracks = null, ReportedSound? Sound = null)
{
    /// <summary>Said by a player that tells remotes what they need (an Apple TV app from 0.56).</summary>
    public bool ForRemotes => Rate is not null || HasNext is not null;
}

/// <summary>A player's sound settings as it reports them, by name; names this server doesn't know are left out.</summary>
public sealed record ReportedSound(string? DialogueBoost = null, string? NightMode = null, string? Speakers = null);

/// <summary>Watched with no connection: where it got to, whether it was finished, and when.</summary>
public sealed record OfflineProgressRequest(double PositionSeconds, bool Finished, DateTime PlayedAt);

public sealed record WatchedRequest(bool Watched);
