using System.Text.Json;
using Flexarr.Api.Configuration;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Playback;

/// <summary>
/// For Settings › Remote access's upload warning (docs/remote-streaming.md): the best speed a remote device has
/// measured lately, against the bitrate the library's busiest files need.
/// </summary>
/// <param name="BestBitrate">The best <c>measuredBitrate</c> a remote device sent in the last 30 days, in bits per second.</param>
/// <param name="MeasuredAt">When that was.</param>
/// <param name="BusiestBitrate">The bitrate of the library's busiest tenth of video files (the least of them); null with none known.</param>
public sealed record RemoteSpeedDto(long BestBitrate, DateTime MeasuredAt, long? BusiestBitrate);

/// <summary>
/// The speeds remote devices measured, kept as each day's best for 30 days in &lt;DataDirectory&gt;/remote-speeds.json, so
/// the warning survives a restart. Written only when a day's best goes up.
/// </summary>
public sealed class RemoteSpeeds(IOptionsMonitor<FlexarrOptions> options, TimeProvider clock)
{
    public const string FileName = "remote-speeds.json";

    /// <summary>How long a reading counts.</summary>
    public static readonly TimeSpan KeptFor = TimeSpan.FromDays(30);

    /// <summary>Readings above this (10 Gbps) aren't a real link: an app's mistake isn't kept.</summary>
    private const long MostBelievable = 10_000_000_000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Lock _lock = new();
    private List<Reading>? _days;

    private sealed record Reading(long Bitrate, DateTime At);

    private string FilePath => Path.Combine(options.CurrentValue.DataDirectory, FileName);

    /// <summary>A remote device's start sent this reading.</summary>
    public void Note(long bitrate)
    {
        if (bitrate is <= 0 or > MostBelievable)
        {
            return;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        lock (_lock)
        {
            var days = Loaded(now);
            var today = days.FindIndex(d => d.At.Date == now.Date);
            if (today >= 0 && days[today].Bitrate >= bitrate)
            {
                return;
            }

            if (today >= 0)
            {
                days[today] = new Reading(bitrate, now);
            }
            else
            {
                days.Add(new Reading(bitrate, now));
            }

            try
            {
                Directory.CreateDirectory(options.CurrentValue.DataDirectory);
                ServerConfigStore.WritePrivately(FilePath, JsonSerializer.Serialize(days, Json));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Kept in memory; written with the next better reading.
            }
        }
    }

    /// <summary>The best reading of the last 30 days; null with none.</summary>
    public (long Bitrate, DateTime At)? Best()
    {
        var now = clock.GetUtcNow().UtcDateTime;
        lock (_lock)
        {
            return Loaded(now).MaxBy(d => d.Bitrate) is { } best ? (best.Bitrate, best.At) : null;
        }
    }

    /// <summary>What's kept, read once, without readings older than <see cref="KeptFor"/>. Call with the lock held.</summary>
    private List<Reading> Loaded(DateTime now)
    {
        if (_days is null)
        {
            try
            {
                _days = File.Exists(FilePath) ? JsonSerializer.Deserialize<List<Reading>>(File.ReadAllText(FilePath), Json) ?? [] : [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                _days = [];
            }
        }

        _days.RemoveAll(d => now - d.At > KeptFor);
        return _days;
    }

    /// <summary>
    /// What Settings › Remote access shows: the best remote reading against the library's busiest tenth of films and
    /// episodes. Null with no remote reading.
    /// </summary>
    public async Task<RemoteSpeedDto?> DescribeAsync(FlexarrDbContext db, CancellationToken ct)
    {
        if (Best() is not { } best)
        {
            return null;
        }

        var videos = db.MediaFiles.AsNoTracking()
            .Where(f => f.Bitrate > 0 && (f.MediaItem!.Kind == MediaKind.Movie || f.MediaItem.Kind == MediaKind.Episode));
        var count = await videos.CountAsync(ct);
        long? busiest = count == 0
            ? null
            : await videos.OrderByDescending(f => f.Bitrate).Select(f => f.Bitrate).Skip(Math.Max(0, (count + 9) / 10 - 1)).FirstOrDefaultAsync(ct);
        return new RemoteSpeedDto(best.Bitrate, best.At, busiest);
    }
}
