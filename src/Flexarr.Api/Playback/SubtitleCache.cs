using System.Collections.Concurrent;
using Flexarr.Api.Configuration;
using Flexarr.Core.Media;
using Flexarr.Streaming;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Playback;

/// <summary>
/// Converts a file's text subtitles once, in the background, starting as soon as playback begins, so they're
/// usually ready by the time someone turns them on. Concurrent requests share the same conversion.
/// </summary>
public sealed class SubtitleCache(IOptionsMonitor<FlexarrOptions> options) : IDisposable
{
    /// <summary>Each conversion reads the whole file: two at a time, so a household starting several films doesn't swamp the disk.</summary>
    private const int AtOnce = 2;

    /// <summary>Longest one file's conversion may take: a share that stopped answering mustn't hold a slot for good.</summary>
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(10);

    /// <summary>The longest a file's size can stretch <see cref="Limit"/> to.</summary>
    private static readonly TimeSpan LongestLimit = TimeSpan.FromHours(1);

    /// <summary>The slowest a file is expected to be read at (a busy share, an old USB drive), in bytes a second.</summary>
    private const double SlowestRead = 25 * 1024 * 1024;

    private readonly ConcurrentDictionary<Guid, Lazy<Task<IReadOnlyDictionary<string, string>>>> _jobs = new();
    private readonly ConcurrentDictionary<(Guid File, string Key, int Page), Lazy<Task<string?>>> _pages = new();
    private readonly ConcurrentDictionary<(Guid File, string Key), Lazy<Task<string?>>> _alone = new();
    private readonly SemaphoreSlim _converting = new(AtOnce, AtOnce);

    public void Prefetch(MediaFile file, IReadOnlyList<SubtitleTrack> tracks)
    {
        if (tracks.Any(t => t.IsText))
        {
            _ = Job(file, tracks);
        }
    }

    /// <summary>A track's WebVTT; with <paramref name="styled"/>, once its styled copy (an embedded ASS track's) is kept too.</summary>
    public async Task<string?> GetAsync(MediaFile file, IReadOnlyList<SubtitleTrack> tracks, SubtitleTrack track, CancellationToken ct, bool styled = false)
    {
        var settings = PlaybackEndpoints.TranscodeSettingsFor(options.CurrentValue);
        var cached = Subtitles.CachePath(file, track, settings);
        if (File.Exists(cached) && (!styled || track.ExternalPath is not null || File.Exists(Subtitles.CachePath(file, track, settings, ".ass"))))
        {
            return cached;
        }

        // One that didn't convert from this file before isn't tried again as playback starts (see Job): asked for, it is,
        // on its own.
        if (!Subtitles.Failed(file, track, settings))
        {
            IReadOnlyDictionary<string, string>? results = null;
            try
            {
                results = await Job(file, tracks).WaitAsync(ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // The file's conversion ran out of time, which is noted against its tracks.
            }

            if (results?.GetValueOrDefault(track.Key) is { } path)
            {
                return path;
            }

            // Tried by itself just now, or a sidecar: asking again straight away would only fail the same way. One that
            // shared its read of the file with other tracks may have been brought down by one of them.
            if (!SharedItsPass(track, tracks) || !Subtitles.Failed(file, track, settings))
            {
                return null;
            }
        }

        return await Alone(file, track).WaitAsync(ct);
    }

    /// <summary>
    /// How long reading <paramref name="size"/> bytes for their subtitles may take: <see cref="Limit"/>, or for a big
    /// file as long as it takes to read from a slow drive, so a 90 GB film on a share isn't given up on (and its
    /// subtitles never had) because it can't be read in ten minutes.
    /// </summary>
    internal static TimeSpan LimitFor(long size) =>
        TimeSpan.FromSeconds(Math.Clamp(size / SlowestRead, Limit.TotalSeconds, LongestLimit.TotalSeconds));

    /// <summary>Whether <paramref name="track"/> is read from the file in one pass with other tracks (see <see cref="Subtitles.ToWebVttAsync"/>).</summary>
    internal static bool SharedItsPass(SubtitleTrack track, IReadOnlyList<SubtitleTrack> tracks)
    {
        static bool Embedded(SubtitleTrack t) => t.IsText && t.ExternalPath is null && !t.InVideo;
        return Embedded(track) && tracks.Count(t => Embedded(t) && t.IsTeletext == track.IsTeletext) > 1;
    }

    /// <summary>One track converted by itself, when someone asks for one that didn't convert with the rest of its file.</summary>
    private Task<string?> Alone(MediaFile file, SubtitleTrack track)
    {
        var lazy = _alone.GetOrAdd((file.Id, track.Key), key => new Lazy<Task<string?>>(async () =>
        {
            // Captions come from decoding the picture, not reading the file: its size says nothing about how long.
            using var limit = new CancellationTokenSource(track.InVideo ? Limit : LimitFor(file.Size));
            try
            {
                await _converting.WaitAsync(limit.Token);
                try
                {
                    var converted = await Subtitles.ToWebVttAsync(file, [track], PlaybackEndpoints.TranscodeSettingsFor(options.CurrentValue), limit.Token);
                    return converted.GetValueOrDefault(track.Key);
                }
                finally
                {
                    _converting.Release();
                }
            }
            catch (OperationCanceledException)
            {
                return null; // Out of time again.
            }
            finally
            {
                _alone.TryRemove(key, out _);
            }
        }));
        return lazy.Value;
    }

    /// <summary>A teletext track's WebVTT from one page the viewer chose, converted on its own when first asked for.</summary>
    public async Task<string?> GetTeletextPageAsync(MediaFile file, SubtitleTrack track, int page, CancellationToken ct)
    {
        var lazy = _pages.GetOrAdd((file.Id, track.Key, page), key => new Lazy<Task<string?>>(async () =>
        {
            using var limit = new CancellationTokenSource(Limit);
            try
            {
                await _converting.WaitAsync(limit.Token);
                try
                {
                    return await Subtitles.TeletextPageToWebVttAsync(file, track, page, PlaybackEndpoints.TranscodeSettingsFor(options.CurrentValue), limit.Token);
                }
                finally
                {
                    _converting.Release();
                }
            }
            finally
            {
                _pages.TryRemove(key, out _);
            }
        }));
        return await lazy.Value.WaitAsync(ct);
    }

    private Task<IReadOnlyDictionary<string, string>> Job(MediaFile file, IReadOnlyList<SubtitleTrack> tracks)
    {
        var lazy = _jobs.GetOrAdd(file.Id, id => new Lazy<Task<IReadOnlyDictionary<string, string>>>(async () =>
        {
            // Not tied to a request: the conversion should finish even if the viewer moves on, within its limit.
            using var limit = new CancellationTokenSource(LimitFor(file.Size));
            try
            {
                await _converting.WaitAsync(limit.Token);
                try
                {
                    // Tracks that didn't convert from this file before are left: this runs at every play.
                    return await Subtitles.ToWebVttAsync(file, tracks, PlaybackEndpoints.TranscodeSettingsFor(options.CurrentValue), limit.Token, retryFailed: false);
                }
                finally
                {
                    _converting.Release();
                }
            }
            finally
            {
                _jobs.TryRemove(file.Id, out _);
            }
        }));
        return lazy.Value;
    }

    public void Dispose() => _converting.Dispose();
}
