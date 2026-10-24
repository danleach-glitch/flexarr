using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flexarr.Api.Configuration;
using Flexarr.Api.Authentication;
using Flexarr.Api.Playback;
using Flexarr.Api.Profiles;
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
    string FfprobePath,
    string? WebhookSecret = null);

public sealed record SeerrConnectionDto(string BaseUrl, bool HasApiKey);

public sealed record SeerrConnectionUpdate(string BaseUrl, string? ApiKey);

public sealed record SettingsUpdate(
    TranscodingOptions? Transcoding,
    AuthenticationRequired? AuthenticationRequired,
    IReadOnlyList<PathMapping>? PathMappings,
    IReadOnlyList<ArrConnectionUpdate>? Arr,
    SeerrConnectionUpdate? Seerr = null);

/// <param name="Pin">The main profile's PIN, when it has one.</param>
/// <param name="Confirm">The person said yes, when the main profile has no PIN.</param>
/// <param name="SignOutOthers">On rotating: sign out every other browser and device too.</param>
public sealed record KeyRequest(string? Pin = null, bool Confirm = false, bool SignOutOthers = false);

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
            if (update.Arr?.FirstOrDefault(a => WebAddress(a.BaseUrl) is null) is { } bad)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["arr"] = [$"{bad.Name}: “{bad.BaseUrl}” isn't a valid http or https address."] });
            }

            if (update.Seerr is { BaseUrl.Length: > 0 } badSeerr && WebAddress(badSeerr.BaseUrl) is null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["seerr"] = [$"“{badSeerr.BaseUrl}” isn't a valid http or https address."] });
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
                    // An omitted key keeps the saved one, but only for the server it was saved for.
                    var savedKey = current.Seerr is { } saved && SameServer(saved.BaseUrl, seerr.BaseUrl) ? saved.ApiKey : "";
                    config["seerr"] = seerr.BaseUrl.Length == 0 ? null : new JsonObject
                    {
                        ["baseUrl"] = seerr.BaseUrl.Trim(),
                        ["apiKey"] = string.IsNullOrWhiteSpace(seerr.ApiKey) ? savedKey : seerr.ApiKey.Trim(),
                    };
                }

                if (update.Arr is { } arr)
                {
                    // An omitted API key keeps the saved one, so the UI never needs to read keys back; but only for the
                    // server it was saved for, so changing the address can't send the key somewhere else.
                    var saved = current.Arr.ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);
                    config["arr"] = new JsonArray([.. arr.Select(a => (JsonNode)new JsonObject
                    {
                        ["name"] = a.Name.Trim(),
                        ["kind"] = a.Kind.ToString(),
                        ["baseUrl"] = a.BaseUrl.Trim(),
                        ["apiKey"] = string.IsNullOrWhiteSpace(a.ApiKey) ? SavedKey(saved.GetValueOrDefault(a.Name), a.BaseUrl) ?? "" : a.ApiKey.Trim(),
                    })]);
                }
            });

            // The file watcher reloads options shortly after the write; wait briefly so the response reflects it.
            await Task.Delay(400, ct);
            return Results.Ok(ToDto(options.CurrentValue, await AvailableAsync(options.CurrentValue, ct)));
        });

        // Showing the key is a POST, so it needs the web app's own header (see RequestForgery) and can't be read by a
        // cross-site <script> or <img>. It also asks again (see ConfirmKeyAsync).
        group.MapPost("/api-key/reveal", async (KeyRequest? request, HttpContext http, IOptionsMonitor<FlexarrOptions> options, FlexarrDbContext db,
            CurrentProfile current, PinGuard guard, CancellationToken ct) =>
            await ConfirmKeyAsync(request, http, db, current, guard, ct) ?? Results.Ok(new { apiKey = options.CurrentValue.ApiKey }));

        // A new key. Browsers and devices with their own sign-in carry on, unless asked to sign them all out too (if the
        // key got out, someone could have used it to sign in); anything given the old key itself (Sonarr and Radarr's
        // Emby connection, scripts, Apple TVs set up before per-device sign-ins) needs the new one.
        group.MapPost("/api-key/rotate", async (KeyRequest? request, HttpContext http, IOptionsMonitor<FlexarrOptions> options, FlexarrDbContext db,
            CurrentProfile current, PinGuard guard, SignIns signIns, CancellationToken ct) =>
        {
            if (await ConfirmKeyAsync(request, http, db, current, guard, ct) is { } refused)
            {
                return refused;
            }

            var key = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
            ServerConfigStore.Update(options.CurrentValue.DataDirectory, config => config["apiKey"] = key);
            var signedOut = request?.SignOutOthers == true ? signIns.RevokeAllExcept(current.SignInId) : 0;
            for (var i = 0; i < 20 && options.CurrentValue.ApiKey != key; i++)
            {
                await Task.Delay(100, ct);
            }

            return Results.Ok(new { apiKey = key, signedOut });
        });

        // A new webhook secret, if the addresses got out. Sonarr, Radarr and Lidarr need the new addresses.
        group.MapPost("/webhook-secret/rotate", async (IOptionsMonitor<FlexarrOptions> options, CancellationToken ct) =>
        {
            var secret = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
            ServerConfigStore.Update(options.CurrentValue.DataDirectory, config => config["webhookSecret"] = secret);
            for (var i = 0; i < 20 && options.CurrentValue.WebhookSecret != secret; i++)
            {
                await Task.Delay(100, ct);
            }

            return Results.Ok(new { webhookSecret = secret });
        });

        api.MapGet("/arr/status", async (IOptionsMonitor<FlexarrOptions> options, ArrClient client, CancellationToken ct) =>
            await Task.WhenAll(options.CurrentValue.Arr.Select(a => StatusAsync(client, a, ct)))).WithTags("Settings").RequireAuthorization(FlexarrClaims.AdminPolicy);

        api.MapPost("/arr/test", async (ArrConnectionUpdate connection, IOptionsMonitor<FlexarrOptions> options, ArrClient client, CancellationToken ct) =>
        {
            // The saved key is only tried against the server it was saved for.
            var key = string.IsNullOrWhiteSpace(connection.ApiKey)
                ? SavedKey(options.CurrentValue.Arr.FirstOrDefault(a => a.Name.Equals(connection.Name, StringComparison.OrdinalIgnoreCase)), connection.BaseUrl)
                : connection.ApiKey;
            if (WebAddress(connection.BaseUrl) is not { } url || key is null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["arr"] = ["Enter an http or https address and the API key."] });
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
        o.FfprobePath,
        o.WebhookSecret);

    /// <summary>
    /// Showing or replacing the API key asks again, so a script that found its way into the page can't just take it:
    /// the main profile's PIN when it has one (a script can't know it), otherwise an explicit "yes" from the web app
    /// (which only stops a page that wasn't written for this). Callers holding the key already needn't.
    /// </summary>
    private static async Task<IResult?> ConfirmKeyAsync(KeyRequest? request, HttpContext http, FlexarrDbContext db, CurrentProfile current, PinGuard guard,
        CancellationToken ct)
    {
        if (http.User.FindFirst(FlexarrClaims.Method)?.Value == ApiKeyAuthenticationHandler.Methods.ApiKey)
        {
            return null;
        }

        var main = await db.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == PlaybackState.DefaultUser, ct);
        if (main?.PinHash is null)
        {
            return request?.Confirm == true ? null : Results.Problem("Confirm that you want to see the API key.", statusCode: 400);
        }

        if (string.IsNullOrEmpty(request?.Pin))
        {
            return Results.Problem("Enter the main profile's PIN to see the API key.", statusCode: 403);
        }

        var (caller, browser) = ProfileEndpoints.PinCaller(http, current);
        var (result, retryAfter) = await guard.CheckAsync(main.Id, caller, () => main.CheckPin(request.Pin), ct, mayManage: true, browser);
        return result switch
        {
            PinCheck.Right => null,
            PinCheck.LockedOut => ProfileEndpoints.TooManyPins(http, retryAfter),
            _ => Results.Problem("That PIN isn't right.", statusCode: 403),
        };
    }

    /// <summary>An absolute http or https address; anything else (file:, ftp:) is refused.</summary>
    private static Uri? WebAddress(string? text) =>
        Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var url) && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps) ? url : null;

    /// <summary>Same scheme, host and port: where a saved key may be sent.</summary>
    private static bool SameServer(string? saved, string? given) =>
        WebAddress(saved) is { } a && WebAddress(given) is { } b
        && a.Scheme == b.Scheme && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port;

    private static string? SavedKey(ArrInstance? saved, string? baseUrl) =>
        saved is not null && SameServer(saved.BaseUrl.ToString(), baseUrl) ? saved.ApiKey : null;

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
