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

    private readonly ConcurrentDictionary<Guid, Lazy<Task<IReadOnlyDictionary<string, string>>>> _jobs = new();
    private readonly ConcurrentDictionary<(Guid File, string Key, int Page), Lazy<Task<string?>>> _pages = new();
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

        var results = await Job(file, tracks).WaitAsync(ct);
        return results.GetValueOrDefault(track.Key);
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
            using var limit = new CancellationTokenSource(Limit);
            try
            {
                await _converting.WaitAsync(limit.Token);
                try
                {
                    return await Subtitles.ToWebVttAsync(file, tracks, PlaybackEndpoints.TranscodeSettingsFor(options.CurrentValue), limit.Token);
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
