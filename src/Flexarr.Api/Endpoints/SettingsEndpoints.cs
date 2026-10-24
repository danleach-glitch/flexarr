using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flexarr.Api.Configuration;
using Flexarr.Api.Playback;
using Flexarr.Api.Security;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Flexarr.Core.Paths;
using Flexarr.Core.Scanning;
using Flexarr.Integrations.Arr;
using Flexarr.Streaming;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Endpoints;

public sealed record ArrConnectionDto(string Name, ArrKind Kind, string BaseUrl, bool HasApiKey);

public sealed record ArrConnectionUpdate(string Name, ArrKind Kind, string BaseUrl, string? ApiKey);

public sealed record SettingsDto(
    TranscodingOptions Transcoding,
    IReadOnlyList<HardwareAcceleration> AvailableHardwareAcceleration,
    AuthenticationRequired AuthenticationRequired,
    IReadOnlyList<PathMapping> PathMappings,
    IReadOnlyList<ArrConnectionDto> Arr,
    SeerrConnectionDto? Seerr,
    string DataDirectory,
    string FfmpegPath,
    string FfprobePath);

public sealed record SeerrConnectionDto(string BaseUrl, bool HasApiKey);

public sealed record SeerrConnectionUpdate(string BaseUrl, string? ApiKey);

public sealed record SettingsUpdate(
    TranscodingOptions? Transcoding,
    AuthenticationRequired? AuthenticationRequired,
    IReadOnlyList<PathMapping>? PathMappings,
    IReadOnlyList<ArrConnectionUpdate>? Arr,
    SeerrConnectionUpdate? Seerr = null);

public sealed record ArrStatusDto(string Name, ArrKind Kind, bool Ok, string? Version, string? Error);

public sealed record StreamActivityDto(string SessionId, ItemSummaryDto Item, string? Series, PlaybackMethod Method, string? Transcode, double PositionSeconds, double DurationSeconds, bool Paused);

public sealed record ActivityDto(
    ScanStatusDto? Scan,
    int EpisodesAnalysed,
    int EpisodesTotal,
    int ItemsWithMetadata,
    int ItemsTotal,
    IReadOnlyList<StreamActivityDto> Streams);

public static class SettingsEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
    private static IReadOnlyList<HardwareAcceleration>? _available;

    public static RouteGroupBuilder MapSettingsEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/settings").WithTags("Settings").RequireAuthorization(FlexarrClaims.AdminPolicy);

        group.MapGet("/", async (IOptionsMonitor<FlexarrOptions> options, CancellationToken ct) => ToDto(options.CurrentValue, await AvailableAsync(options.CurrentValue, ct)));

        group.MapPut("/", async (SettingsUpdate update, IOptionsMonitor<FlexarrOptions> options, CancellationToken ct) =>
        {
            var current = options.CurrentValue;
            if (update.Arr?.FirstOrDefault(a => !Uri.TryCreate(a.BaseUrl, UriKind.Absolute, out _)) is { } bad)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["arr"] = [$"{bad.Name}: “{bad.BaseUrl}” isn't a valid URL."] });
            }

            ServerConfigStore.Update(current.DataDirectory, config =>
            {
                if (update.Transcoding is { } transcoding)
                {
                    config["transcoding"] = JsonSerializer.SerializeToNode(transcoding, Json);
                }

                if (update.AuthenticationRequired is { } auth)
                {
                    config["authenticationRequired"] = auth.ToString();
                }

                if (update.PathMappings is { } mappings)
                {
                    config["pathMappings"] = JsonSerializer.SerializeToNode(mappings.Where(m => m.From.Length > 0 && m.To.Length > 0), Json);
                }

                if (update.Seerr is { } seerr)
                {
                    config["seerr"] = seerr.BaseUrl.Length == 0 ? null : new JsonObject
                    {
                        ["baseUrl"] = seerr.BaseUrl.Trim(),
                        ["apiKey"] = string.IsNullOrWhiteSpace(seerr.ApiKey) ? current.Seerr?.ApiKey ?? "" : seerr.ApiKey.Trim(),
                    };
                }

                if (update.Arr is { } arr)
                {
                    // An omitted API key keeps the saved one, so the UI never needs to read keys back.
                    var saved = current.Arr.ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);
                    config["arr"] = new JsonArray([.. arr.Select(a => (JsonNode)new JsonObject
                    {
                        ["name"] = a.Name.Trim(),
                        ["kind"] = a.Kind.ToString(),
                        ["baseUrl"] = a.BaseUrl.Trim(),
                        ["apiKey"] = string.IsNullOrWhiteSpace(a.ApiKey) ? saved.GetValueOrDefault(a.Name)?.ApiKey ?? "" : a.ApiKey.Trim(),
                    })]);
                }
            });

            // The file watcher reloads options shortly after the write; wait briefly so the response reflects it.
            await Task.Delay(400, ct);
            return Results.Ok(ToDto(options.CurrentValue, await AvailableAsync(options.CurrentValue, ct)));
        });

        group.MapGet("/api-key", (IOptionsMonitor<FlexarrOptions> options) => new { apiKey = options.CurrentValue.ApiKey });

        api.MapGet("/arr/status", async (IOptionsMonitor<FlexarrOptions> options, ArrClient client, CancellationToken ct) =>
            await Task.WhenAll(options.CurrentValue.Arr.Select(a => StatusAsync(client, a, ct)))).WithTags("Settings").RequireAuthorization(FlexarrClaims.AdminPolicy);

        api.MapPost("/arr/test", async (ArrConnectionUpdate connection, IOptionsMonitor<FlexarrOptions> options, ArrClient client, CancellationToken ct) =>
        {
            var key = string.IsNullOrWhiteSpace(connection.ApiKey)
                ? options.CurrentValue.Arr.FirstOrDefault(a => a.Name.Equals(connection.Name, StringComparison.OrdinalIgnoreCase))?.ApiKey
                : connection.ApiKey;
            if (!Uri.TryCreate(connection.BaseUrl, UriKind.Absolute, out var url) || key is null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["arr"] = ["Enter a URL and API key."] });
            }

            return Results.Ok(await StatusAsync(client, new ArrInstance { Name = connection.Name, Kind = connection.Kind, BaseUrl = url, ApiKey = key }, ct));
        }).WithTags("Settings").RequireAuthorization(FlexarrClaims.AdminPolicy);

        api.MapGet("/system/activity", async (FlexarrDbContext db, ScanProgress progress, PlaybackSessions sessions, CancellationToken ct) =>
        {
            var episodes = db.MediaFiles.Where(f => f.MediaItem!.Kind == MediaKind.Episode && f.ProbeError == null);
            var topLevel = db.MediaItems.Where(i => i.ParentId == null);
            var streams = new List<StreamActivityDto>();
            foreach (var session in sessions.Active.Where(s => DateTime.UtcNow - s.LastSeen < TimeSpan.FromMinutes(2)))
            {
                var item = await db.MediaItems.Where(i => i.Id == session.ItemId).Summaries(session.ProfileId).FirstOrDefaultAsync(ct);
                if (item is null)
                {
                    continue;
                }

                var series = item.Kind == MediaKind.Episode
                    ? await db.MediaItems.Where(i => i.Id == item.ParentId).Select(s => s.Parent!.Title).FirstOrDefaultAsync(ct)
                    : null;
                var plan = session.Transcode?.Plan;
                streams.Add(new StreamActivityDto(session.Id, item, series, session.Method,
                    plan is null ? null : $"{plan.TargetHeight}p at {plan.VideoBitrate / 1_000_000.0:0.#} Mbps",
                    session.LastPosition, session.DurationSeconds, session.Paused));
            }

            var scan = progress.Current is { } p ? new ScanStatusDto(p.Path, p.Total, p.Processed) : null;
            return new ActivityDto(
                scan,
                await episodes.CountAsync(f => f.MarkersScannedAt != null, ct),
                await episodes.CountAsync(ct),
                await topLevel.CountAsync(i => i.MetadataUpdatedAt != null && i.PosterUrl != null, ct),
                await topLevel.CountAsync(ct),
                streams);
        }).WithTags("System").RequireAuthorization(FlexarrClaims.AdminPolicy);

        return api;
    }

    private static SettingsDto ToDto(FlexarrOptions o, IReadOnlyList<HardwareAcceleration> available) => new(
        o.Transcoding,
        available,
        o.AuthenticationRequired,
        o.PathMappings,
        o.Arr.Select(a => new ArrConnectionDto(a.Name, a.Kind, a.BaseUrl.ToString().TrimEnd('/'), !string.IsNullOrEmpty(a.ApiKey))).ToList(),
        o.Seerr is { BaseUrl.Length: > 0 } s ? new SeerrConnectionDto(s.BaseUrl, s.ApiKey.Length > 0) : null,
        Path.GetFullPath(o.DataDirectory),
        o.FfmpegPath,
        o.FfprobePath);

    private static async Task<ArrStatusDto> StatusAsync(ArrClient client, ArrInstance instance, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var status = await client.GetSystemStatusAsync(instance, timeout.Token);
            return new ArrStatusDto(instance.Name, instance.Kind, true, status.Version, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            var reason = ex switch
            {
                HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized } => "The API key was rejected.",
                HttpRequestException { StatusCode: not null } http => $"It answered with HTTP {(int)http.StatusCode.Value}.",
                HttpRequestException => "Nothing answered at that address.",
                TaskCanceledException => "It didn't answer within 8 seconds.",
                _ => "That doesn't look like a Sonarr, Radarr or Lidarr server.",
            };
            return new ArrStatusDto(instance.Name, instance.Kind, false, null, reason);
        }
    }

    /// <summary>Hardware encoders this ffmpeg build actually has (checked once).</summary>
    private static async Task<IReadOnlyList<HardwareAcceleration>> AvailableAsync(FlexarrOptions options, CancellationToken ct)
    {
        if (_available is not null)
        {
            return _available;
        }

        var found = new List<HardwareAcceleration> { HardwareAcceleration.Auto, HardwareAcceleration.None };
        try
        {
            var start = new ProcessStartInfo(options.FfmpegPath, "-hide_banner -encoders") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using var process = Process.Start(start)!;
            var encoders = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            if (encoders.Contains("h264_videotoolbox", StringComparison.Ordinal)) found.Add(HardwareAcceleration.VideoToolbox);
            if (encoders.Contains("h264_vaapi", StringComparison.Ordinal)) found.Add(HardwareAcceleration.Vaapi);
            if (encoders.Contains("h264_qsv", StringComparison.Ordinal)) found.Add(HardwareAcceleration.Qsv);
            if (encoders.Contains("h264_nvenc", StringComparison.Ordinal)) found.Add(HardwareAcceleration.Nvenc);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // ffmpeg missing: only software is listed, and playback reports the problem.
        }

        return _available = found;
    }
}
