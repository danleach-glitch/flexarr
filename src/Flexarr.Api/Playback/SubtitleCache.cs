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
public sealed class SubtitleCache(IOptionsMonitor<FlexarrOptions> options)
{
    private readonly ConcurrentDictionary<Guid, Lazy<Task<IReadOnlyDictionary<string, string>>>> _jobs = new();

    public void Prefetch(MediaFile file, IReadOnlyList<SubtitleTrack> tracks)
    {
        if (tracks.Any(t => t.IsText))
        {
            _ = Job(file, tracks);
        }
    }

    public async Task<string?> GetAsync(MediaFile file, IReadOnlyList<SubtitleTrack> tracks, SubtitleTrack track, CancellationToken ct)
    {
        var settings = PlaybackEndpoints.TranscodeSettingsFor(options.CurrentValue);
        var cached = Subtitles.CachePath(file, track, settings);
        if (File.Exists(cached))
        {
            return cached;
        }

        var results = await Job(file, tracks).WaitAsync(ct);
        return results.GetValueOrDefault(track.Key);
    }

    private Task<IReadOnlyDictionary<string, string>> Job(MediaFile file, IReadOnlyList<SubtitleTrack> tracks)
    {
        var lazy = _jobs.GetOrAdd(file.Id, id => new Lazy<Task<IReadOnlyDictionary<string, string>>>(async () =>
        {
            try
            {
                // Not tied to a request: the conversion should finish even if the viewer moves on.
                return await Subtitles.ToWebVttAsync(file, tracks, PlaybackEndpoints.TranscodeSettingsFor(options.CurrentValue), CancellationToken.None);
            }
            finally
            {
                _jobs.TryRemove(file.Id, out _);
            }
        }));
        return lazy.Value;
    }
}
