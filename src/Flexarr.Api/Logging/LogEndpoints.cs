using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Flexarr.Api.Authentication;
using Flexarr.Api.Configuration;
using Flexarr.Api.Profiles;
using Flexarr.Api.Security;
using Flexarr.Core.Data;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Serilog.Events;

namespace Flexarr.Api.Logging;

public sealed record LogAreaSetting(string Key, string? Name, LogEventLevel? Level);

/// <param name="ErrorReports">Send errors to Flexarr's developer through Sentry: off unless turned on here.</param>
public sealed record LogSettingsDto(LogEventLevel? Level, IReadOnlyList<LogAreaSetting>? Areas, int? RetentionDays, int? MaxSizeMb, bool? ErrorReports = null);

public static class LogEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(20);

    public static RouteGroupBuilder MapLogEndpoints(this RouteGroupBuilder api)
    {
        var admin = api.MapGroup("/logs").WithTags("Logs").RequireAuthorization(FlexarrClaims.AdminPolicy);

        admin.MapGet("/", (LogEventLevel? level, string? area, string? q, string? before, int? limit, LogReader reader) =>
            reader.Read(new LogQuery(level, area, q?.Trim(), before, Math.Clamp(limit ?? 200, 1, 1000))));

        admin.MapGet("/stream", StreamAsync);

        admin.MapGet("/settings", (IOptionsMonitor<FlexarrOptions> options) => ToDto(options.CurrentValue.Logs));

        admin.MapPut("/settings", async (LogSettingsDto update, IOptionsMonitor<FlexarrOptions> options) =>
        {
            if (update.Areas?.FirstOrDefault(a => LogAreas.Find(a.Key) is null) is { } unknown)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["areas"] = [$"There's no area called “{unknown.Key}”."] });
            }

            var current = options.CurrentValue;
            var areas = new Dictionary<string, LogEventLevel>(current.Logs.Areas, StringComparer.OrdinalIgnoreCase);
            foreach (var area in update.Areas ?? [])
            {
                // Only the areas sent change; null puts one back to the server's level.
                if (area.Level is { } level)
                {
                    areas[LogAreas.Find(area.Key)!.Key] = level;
                }
                else
                {
                    areas.Remove(area.Key);
                }
            }

            var reloaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var watch = options.OnChange(_ => reloaded.TrySetResult());
            ServerConfigStore.Update(current.DataDirectory, config =>
            {
                var logs = config["logs"] as JsonObject ?? [];
                if (update.Level is { } level)
                {
                    logs["level"] = level.ToString();
                }

                logs["areas"] = JsonSerializer.SerializeToNode(areas.ToDictionary(a => a.Key, a => a.Value.ToString()));
                if (update.RetentionDays is { } days)
                {
                    logs["retentionDays"] = Math.Clamp(days, 1, 365);
                }

                if (update.MaxSizeMb is { } size)
                {
                    logs["maxSizeMb"] = Math.Clamp(size, 10, 10_000);
                }

                if (update.ErrorReports is { } reports)
                {
                    logs["errorReports"] = reports;

                    // A random id, made the first time it's turned on: it lets one server's repeats be told apart, and
                    // says nothing about the server or its owner.
                    if (reports && string.IsNullOrEmpty(current.Logs.ErrorReportsId))
                    {
                        logs["errorReportsId"] = Guid.NewGuid().ToString("N");
                    }
                }

                config["logs"] = logs;
            });
            await Task.WhenAny(reloaded.Task, Task.Delay(TimeSpan.FromSeconds(3)));
            return Results.Ok(ToDto(options.CurrentValue.Logs));
        });

        admin.MapGet("/diagnostics", async (HttpContext http, Diagnostics diagnostics, TimeProvider clock, CancellationToken ct) =>
        {
            http.Response.ContentType = "application/zip";
            http.Response.Headers.ContentDisposition = $"attachment; filename=\"flexarr-diagnostics-{clock.GetUtcNow():yyyyMMdd-HHmm}.zip\"";

            // Zipped to a temporary file first: ZipArchive needs to seek, and a failure part way shouldn't send half a zip.
            var temp = Path.GetTempFileName();
            try
            {
                await using (var file = File.Create(temp))
                {
                    await diagnostics.WriteAsync(file, ct);
                }

                await using var read = File.OpenRead(temp);
                await read.CopyToAsync(http.Response.Body, ct);
            }
            finally
            {
                File.Delete(temp);
            }
        });

        admin.MapGet("/uploads", (ClientLogs clients) => clients.Uploads());

        admin.MapGet("/uploads/{id}", (string id, ClientLogs clients) =>
            clients.UploadPath(id) is { } path && File.Exists(path)
                ? Results.File(path, "text/plain; charset=utf-8", $"flexarr-device-{id}.log")
                : Results.NotFound());

        // Any signed-in app or browser may report; it's filed under its own sign-in and profile.
        var clientLogs = api.MapGroup("/logs/client").WithTags("Logs");

        clientLogs.MapPost("/", async (ClientLogBatch batch, HttpContext http, ClientLogs clients, SignIns signIns, CurrentProfile profile, FlexarrDbContext db,
            CancellationToken ct) =>
        {
            if (batch.Entries is not { Count: > 0 and <= ClientLogs.MaxBatch })
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["entries"] = [$"Send between 1 and {ClientLogs.MaxBatch} entries."] });
            }

            return clients.Write(await IdentifyAsync(http, signIns, profile, db, ct), batch)
                ? Results.Accepted()
                : Results.StatusCode(StatusCodes.Status429TooManyRequests);
        });

        // The web app's error reports, on their way to Sentry (its content security policy only lets it reach this server).
        api.MapPost("/error-reports/web", async (HttpContext http, ErrorReportTunnel tunnel, IOptionsMonitor<FlexarrOptions> options, CancellationToken ct) =>
            options.CurrentValue.Logs.ErrorReports
                ? Results.StatusCode(await tunnel.ForwardAsync(http.Request.Body, ct))
                : Results.NotFound()).WithTags("Logs");

        clientLogs.MapPost("/diagnostics", async (string platform, string? appVersion, string? note, HttpContext http, ClientLogs clients, SignIns signIns,
            CurrentProfile profile, FlexarrDbContext db, CancellationToken ct) =>
        {
            if (http.Request.ContentLength > ClientLogs.MaxUploadBytes * 2L)
            {
                return Results.Problem("Diagnostics are limited to 5 MB.", statusCode: StatusCodes.Status413PayloadTooLarge);
            }

            var upload = await clients.SaveUploadAsync(await IdentifyAsync(http, signIns, profile, db, ct), platform, appVersion, note, http.Request.Body, ct);
            return Results.Created($"/api/v1/logs/uploads/{upload.Id}", new { upload.Id });
        });

        return api;
    }

    private static async Task StreamAsync(LogEventLevel? level, string? area, HttpContext http, LogFeed feed, CancellationToken ct)
    {
        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.Headers.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        await http.Response.WriteAsync(": connected\n\n", ct);
        await http.Response.Body.FlushAsync(ct);

        using var watcher = feed.Watch();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(Heartbeat);
                try
                {
                    await watcher.Entries.WaitToReadAsync(wait.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    await http.Response.WriteAsync(": ping\n\n", ct);
                    await http.Response.Body.FlushAsync(ct);
                    continue;
                }

                while (watcher.Entries.TryRead(out var entry))
                {
                    if ((level is null || entry.Level >= level) && (string.IsNullOrEmpty(area) || entry.Area.Equals(area, StringComparison.OrdinalIgnoreCase)))
                    {
                        await http.Response.WriteAsync($"data: {JsonSerializer.Serialize(entry, Json)}\n\n", ct);
                    }
                }

                await http.Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
    }

    private static async Task<ClientIdentity> IdentifyAsync(HttpContext http, SignIns signIns, CurrentProfile profile, FlexarrDbContext db, CancellationToken ct)
    {
        var signIn = profile.SignInId is { } id ? signIns.List().FirstOrDefault(s => s.Id == id) : null;
        // Sign-ins have names ("Living room Apple TV"); the API key itself, or a browser let in without one, doesn't.
        var device = signIn?.Name ?? $"Unnamed device ({http.Connection.RemoteIpAddress?.ToString() ?? "unknown address"})";
        var profileName = await db.Profiles.AsNoTracking().Where(p => p.Id == profile.Id).Select(p => p.Name).FirstOrDefaultAsync(ct);
        return new ClientIdentity(signIn?.Id ?? http.Connection.RemoteIpAddress?.ToString() ?? "unknown", device, profileName);
    }

    private static LogSettingsDto ToDto(LogOptions logs) => new(
        logs.Level,
        [.. LogAreas.All.Select(a => new LogAreaSetting(a.Key, a.Name,
            logs.Areas.FirstOrDefault(x => x.Key.Equals(a.Key, StringComparison.OrdinalIgnoreCase)) is { Key: not null } set ? set.Value : null))],
        logs.RetentionDays,
        logs.MaxSizeMb,
        logs.ErrorReports);
}
