using System.Text.Json;
using Flexarr.Api.Configuration;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Flexarr.Core.Parsing;
using Flexarr.Core.Probing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Endpoints;

/// <summary>A track's ReplayGain: gains in dB, peaks as a fraction of full scale, null for whatever the file doesn't say.</summary>
public sealed record LoudnessDto(double? TrackGain, double? AlbumGain, double? TrackPeak, double? AlbumPeak);

public static class LoudnessEndpoints
{
    public static RouteGroupBuilder MapLoudnessEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/items/{id:guid}/loudness", async (Guid id, FlexarrDbContext db, LoudnessCache loudness, CancellationToken ct) =>
        {
            var file = await db.MediaFiles.AsNoTracking()
                .Where(f => f.MediaItemId == id && f.MediaItem!.Kind == MediaKind.Track && f.MissingSince == null)
                .OrderBy(f => f.Path)
                .FirstOrDefaultAsync(ct);
            if (file is null)
            {
                return Results.NotFound();
            }

            var found = await loudness.GetAsync(file, ct);
            return Results.Ok(new LoudnessDto(found.TrackGain, found.AlbumGain, found.TrackPeak, found.AlbumPeak));
        }).WithTags("Items");

        return api;
    }
}

/// <summary>
/// Reads a track's ReplayGain tags when they're first asked for, which means running ffprobe again (the scanner doesn't
/// keep tags), and keeps the answer, including "none", on disk under cache/loudness so the next request doesn't touch the
/// track until the file changes.
/// </summary>
public sealed partial class LoudnessCache(IMediaProbe probe, IOptionsMonitor<FlexarrOptions> options, ILogger<LoudnessCache> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<Loudness> GetAsync(MediaFile file, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(file);

        var key = Stamp(file.Path);
        var cachePath = Path.Combine(options.CurrentValue.ResolvedCacheDirectory, "loudness", file.Id.ToString("N") + ".json");

        if (ReadCached(cachePath) is { } cached && cached.Key == key)
        {
            return cached.Loudness;
        }

        var loudness = await ReadAsync(file.Path, ct);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            await File.WriteAllTextAsync(cachePath, JsonSerializer.Serialize(new Entry(key, loudness), Json), ct);
        }
        catch (IOException e)
        {
            LogCacheWriteFailed(logger, cachePath, e.Message);
        }

        return loudness;
    }

    private async Task<Loudness> ReadAsync(string trackPath, CancellationToken ct)
    {
        if (!File.Exists(trackPath))
        {
            return Loudness.Unknown;
        }

        try
        {
            return ReplayGain.FromTags((await probe.ProbeAsync(trackPath, ct)).Tags);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A file ffprobe can't read plays at its own level; the scanner reports the file itself.
            LogProbeFailed(logger, trackPath, e.Message);
            return Loudness.Unknown;
        }
    }

    /// <summary>When the file last changed, or "-" when it isn't there, so a retagged file is read again.</summary>
    private static string Stamp(string path) =>
        File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) : "-";

    private Entry? ReadCached(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Entry>(File.ReadAllText(path), Json);
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            LogCacheReadFailed(logger, path, e.Message);
            return null;
        }
    }

    private sealed record Entry(string Key, Loudness Loudness);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Couldn't probe {Path} for ReplayGain: {Reason}")]
    private static partial void LogProbeFailed(ILogger logger, string path, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Couldn't write the loudness cache {Path}: {Reason}")]
    private static partial void LogCacheWriteFailed(ILogger logger, string path, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Ignoring the unreadable loudness cache {Path}: {Reason}")]
    private static partial void LogCacheReadFailed(ILogger logger, string path, string reason);
}
