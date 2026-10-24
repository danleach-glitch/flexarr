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
    string? ReplacesSessionId = null);

public sealed record AudioTrackDto(int Index, string Label, string Codec, string? Language, int? Channels, bool IsDefault);

public sealed record SubtitleTrackDto(string Key, string Label, string Codec, string? Language, bool IsForced, bool IsDefault, bool IsText, string? Url);

public sealed record MarkerDto(MarkerType Type, double Start, double End);

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
    string? TranscodeSummary);

public sealed record ProgressRequest(double PositionSeconds, bool Paused = false, bool Ended = false);

public sealed record WatchedRequest(bool Watched);
