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
    string? WebhookSecret = null,
    PodcastSettingsDto? Podcasts = null,
    LiveTvSettingsDto? LiveTv = null,
    string? HostName = null);

/// <summary>The Live TV source; the password is never sent back, only whether there is one.</summary>
public sealed record LiveTvSettingsDto(string BaseUrl, string Username, bool HasPassword, int RewindMinutes, string? DvrFolder = null,
    string DvrFolderInUse = "", int? MaxStreams = null, int PaddingBeforeMinutes = 1, int PaddingAfterMinutes = 5, int MinFreeGigabytes = 100);

/// <param name="BaseUrl">Empty to remove the source (its channels and guide go with it).</param>
/// <param name="Password">Omitted to keep the saved one, which is only kept for the same server.</param>
public sealed record LiveTvSettingsUpdate(string BaseUrl, string? Username, string? Password, int? RewindMinutes = null, string? DvrFolder = null,
    int? MaxStreams = null, int? PaddingBeforeMinutes = null, int? PaddingAfterMinutes = null, int? MinFreeGigabytes = null);

public sealed record LiveTvTestDto(bool Ok, int? Channels, int? MaxConnections, DateTime? ExpiresAt, string? Error);

/// <param name="Country">The chosen country, or null for the server's region (<paramref name="ResolvedCountry"/>).</param>
public sealed record PodcastSettingsDto(string? Country, string ResolvedCountry);

public sealed record SeerrConnectionDto(string BaseUrl, bool HasApiKey);

public sealed record SeerrConnectionUpdate(string BaseUrl, string? ApiKey);

public sealed record SettingsUpdate(
    TranscodingOptions? Transcoding,
    AuthenticationRequired? AuthenticationRequired,
    IReadOnlyList<PathMapping>? PathMappings,
    IReadOnlyList<ArrConnectionUpdate>? Arr,
    SeerrConnectionUpdate? Seerr = null,
    PodcastSettingsUpdate? Podcasts = null,
    LiveTvSettingsUpdate? LiveTv = null);

/// <param name="Country">A two-letter country code; empty for the server's region.</param>
public sealed record PodcastSettingsUpdate(string? Country);

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

        group.MapGet("/", async (IOptionsMonitor<FlexarrOptions> options, FlexarrDbContext db, CancellationToken ct) =>
            ToDto(options.CurrentValue, await AvailableAsync(options.CurrentValue, ct), await LiveTv.DvrFolders.ResolveAsync(db, options.CurrentValue, ct)));

        group.MapPut("/", async (SettingsUpdate update, IOptionsMonitor<FlexarrOptions> options, LiveTv.LiveTvRefreshSignal liveTvRefresh, FlexarrDbContext db,
            CancellationToken ct) =>
        {
            var current = options.CurrentValue;
            if (update.LiveTv is { BaseUrl.Length: > 0 } badLive && (WebAddress(badLive.BaseUrl) is null || string.IsNullOrWhiteSpace(badLive.Username)))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["liveTv"] = ["Enter Dispatcharr's http or https address and a user name."] });
            }

            if (update.Arr?.FirstOrDefault(a => WebAddress(a.BaseUrl) is null) is { } bad)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["arr"] = [$"{bad.Name}: “{bad.BaseUrl}” isn't a valid http or https address."] });
            }

            if (update.Seerr is { BaseUrl.Length: > 0 } badSeerr && WebAddress(badSeerr.BaseUrl) is null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["seerr"] = [$"“{badSeerr.BaseUrl}” isn't a valid http or https address."] });
            }

            if (update.Podcasts is { Country: { Length: > 0 } country } && Podcasts.PodcastCountries.Known(country) is null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["podcasts"] = [$"Apple has no podcast charts for “{country}”."] });
            }

            // The file watcher reloads options shortly after the write; the response waits for that (a fixed pause was
            // too short on slower machines, and the reply showed the old settings).
            var reloaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var watch = options.OnChange(_ => reloaded.TrySetResult());
            ServerConfigStore.Update(current.DataDirectory, config =>
            {
                if (update.Podcasts is { } podcasts)
                {
                    config["podcasts"] = new JsonObject { ["country"] = Podcasts.PodcastCountries.Known(podcasts.Country) };
                }

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

                if (update.LiveTv is { } live)
                {
                    var savedPassword = current.LiveTv is { } saved && SameServer(saved.BaseUrl, live.BaseUrl) ? saved.Password : "";
                    config["liveTv"] = live.BaseUrl.Length == 0 ? null : new JsonObject
                    {
                        ["baseUrl"] = live.BaseUrl.Trim().TrimEnd('/'),
                        ["username"] = live.Username?.Trim() ?? "",
                        ["password"] = string.IsNullOrEmpty(live.Password) ? savedPassword : live.Password,
                        ["rewindMinutes"] = Math.Clamp(live.RewindMinutes ?? current.LiveTv?.RewindMinutes ?? 60, 1, 240),
                        // The DVR's settings: an omitted one keeps what's saved; an empty folder (or a limit of 0) goes
                        // back to the default.
                        ["dvrFolder"] = live.DvrFolder is null ? current.LiveTv?.DvrFolder : live.DvrFolder.Trim() is { Length: > 0 } folder ? folder : null,
                        ["maxStreams"] = live.MaxStreams is null ? current.LiveTv?.MaxStreams : live.MaxStreams > 0 ? Math.Min(live.MaxStreams.Value, 20) : null,
                        ["paddingBeforeMinutes"] = Math.Clamp(live.PaddingBeforeMinutes ?? current.LiveTv?.PaddingBeforeMinutes ?? 1, 0, 30),
                        ["paddingAfterMinutes"] = Math.Clamp(live.PaddingAfterMinutes ?? current.LiveTv?.PaddingAfterMinutes ?? 5, 0, 60),
                        ["minFreeGigabytes"] = Math.Clamp(live.MinFreeGigabytes ?? current.LiveTv?.MinFreeGigabytes ?? 100, 0, 10_000),
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

            try
            {
                await reloaded.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
                // The watcher can fire more than once for one write; a moment more lets the last of them land.
                await Task.Delay(100, ct);
            }
            catch (TimeoutException)
            {
                // Nothing changed on disk (an unchanged save), or the watcher is slow: answer with what there is.
            }

            // Only a new source (or login) needs the channels again: the rewind or the DVR's settings don't.
            if (update.LiveTv is { } savedLive && (!SameServer(current.LiveTv?.BaseUrl, savedLive.BaseUrl) || savedLive.BaseUrl.Length == 0
                || (savedLive.Username is { } user && user.Trim() != current.LiveTv?.Username) || !string.IsNullOrEmpty(savedLive.Password)))
            {
                // The refresh reads the options, so it waits until they hold the new source (or its removal): asked for
                // before then, it found none and cleared the channels instead.
                var wanted = savedLive.BaseUrl.Trim().TrimEnd('/');
                for (var i = 0; i < 50 && (options.CurrentValue.LiveTv?.BaseUrl ?? "") != wanted; i++)
                {
                    await Task.Delay(100, ct);
                }

                liveTvRefresh.Request();
            }

            return Results.Ok(ToDto(options.CurrentValue, await AvailableAsync(options.CurrentValue, ct), await LiveTv.DvrFolders.ResolveAsync(db, options.CurrentValue, ct)));
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

        api.MapPost("/settings/livetv/test", async (LiveTvSettingsUpdate connection, IOptionsMonitor<FlexarrOptions> options, LiveTv.XtreamClient client,
            CancellationToken ct) =>
        {
            // The saved password is only tried against the server it was saved for.
            var saved = options.CurrentValue.LiveTv;
            var password = string.IsNullOrEmpty(connection.Password) ? saved is not null && SameServer(saved.BaseUrl, connection.BaseUrl) ? saved.Password : null : connection.Password;
            if (WebAddress(connection.BaseUrl) is not { } url || string.IsNullOrWhiteSpace(connection.Username) || string.IsNullOrEmpty(password))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["liveTv"] = ["Enter Dispatcharr's address, a user name and its password."] });
            }

            var account = new LiveTv.XtreamAccount(url, connection.Username.Trim(), password);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                var info = await client.CheckAsync(account, timeout.Token);
                if (!info.SignedIn)
                {
                    return Results.Ok(new LiveTvTestDto(false, null, null, null, "The Live TV source refused the user name or password."));
                }

                var channels = await client.GetChannelsAsync(account, timeout.Token);
                return Results.Ok(new LiveTvTestDto(true, channels.Count, info.MaxConnections, info.ExpiresAt, null));
            }
            catch (LiveTv.XtreamException ex)
            {
                return Results.Ok(new LiveTvTestDto(false, null, null, null, ex.Message));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return Results.Ok(new LiveTvTestDto(false, null, null, null, "The Live TV source didn't answer in time."));
            }
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

    private static SettingsDto ToDto(FlexarrOptions o, IReadOnlyList<HardwareAcceleration> available, string dvrFolder) => new(
        o.Transcoding,
        available,
        o.AuthenticationRequired,
        o.PathMappings,
        o.Arr.Select(a => new ArrConnectionDto(a.Name, a.Kind, a.BaseUrl.ToString().TrimEnd('/'), !string.IsNullOrEmpty(a.ApiKey))).ToList(),
        o.Seerr is { BaseUrl.Length: > 0 } s ? new SeerrConnectionDto(s.BaseUrl, s.ApiKey.Length > 0) : null,
        Path.GetFullPath(o.DataDirectory),
        o.FfmpegPath,
        o.FfprobePath,
        o.WebhookSecret,
        new PodcastSettingsDto(Podcasts.PodcastCountries.Known(o.Podcasts.Country), o.Podcasts.ResolvedCountry),
        o.LiveTv is { BaseUrl.Length: > 0 } l
            ? new LiveTvSettingsDto(l.BaseUrl, l.Username, l.Password.Length > 0, l.RewindMinutes, l.DvrFolder, dvrFolder, l.MaxStreams,
                l.PaddingBeforeMinutes, l.PaddingAfterMinutes, l.MinFreeGigabytes)
            : null,
        HostName());

    /// <summary>
    /// This machine's name on the network ("Dans-Mac-Mini"), which the web app puts in place of localhost in links to
    /// apps set up there. None in a container, whose name means nothing anywhere else.
    /// </summary>
    private static string? HostName()
    {
        if (Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true")
        {
            return null;
        }

        var name = System.Net.Dns.GetHostName();
        name = name.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ? name[..^".local".Length] : name;
        return name.Length > 0 && !name.Equals("localhost", StringComparison.OrdinalIgnoreCase) ? name : null;
    }

    /// <summary>
    /// Showing or replacing the API key asks again, so a script that found its way into the page can't just take it:
    /// the main profile's PIN when it has one (a script can't know it), otherwise an explicit "yes" from the web app
    /// (which only stops a page that wasn't written for this). Callers holding the key already needn't.
    /// </summary>
    /// <summary>
    /// Asks again before something sensitive (the API key, a restore): the main profile's PIN when it has one, or a yes.
    /// Null when that's given; otherwise the refusal. Signing in with the key itself is enough.
    /// </summary>
    internal static async Task<IResult?> ConfirmKeyAsync(KeyRequest? request, HttpContext http, FlexarrDbContext db, CurrentProfile current, PinGuard guard,
        CancellationToken ct, string action = "see the API key")
    {
        if (http.User.FindFirst(FlexarrClaims.Method)?.Value == ApiKeyAuthenticationHandler.Methods.ApiKey)
        {
            return null;
        }

        var main = await db.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == PlaybackState.DefaultUser, ct);
        if (main?.PinHash is null)
        {
            return request?.Confirm == true ? null : Results.Problem($"Confirm that you want to {action}.", statusCode: 400);
        }

        if (string.IsNullOrEmpty(request?.Pin))
        {
            return Results.Problem($"Enter the main profile's PIN to {action}.", statusCode: 403);
        }

        var (callers, browser) = ProfileEndpoints.PinCaller(http, current);
        var (result, retryAfter) = await guard.CheckAsync(main.Id, callers, () => main.CheckPin(request.Pin), ct, mayManage: true, browser);
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

    /// <summary>Hardware encoders this ffmpeg build has and can reach (checked once).</summary>
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
            // On Linux, VA-API and Quick Sync go through a render device, which a container only has when it's passed
            // in (--device /dev/dri): an ffmpeg with the encoders but no device can't use them, so they aren't offered.
            if (encoders.Contains("h264_vaapi", StringComparison.Ordinal) && File.Exists(options.Transcoding.VaapiDevice)) found.Add(HardwareAcceleration.Vaapi);
            if (encoders.Contains("h264_qsv", StringComparison.Ordinal) && (!OperatingSystem.IsLinux() || HasRenderDevice())) found.Add(HardwareAcceleration.Qsv);
            if (encoders.Contains("h264_nvenc", StringComparison.Ordinal)) found.Add(HardwareAcceleration.Nvenc);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // ffmpeg missing: only software is listed, and playback reports the problem.
        }

        return _available = found;
    }

    private static bool HasRenderDevice() =>
        Directory.Exists("/dev/dri") && Directory.EnumerateFileSystemEntries("/dev/dri", "renderD*").Any();
}
