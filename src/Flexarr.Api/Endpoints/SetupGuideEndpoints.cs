using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Flexarr.Api.Authentication;
using Flexarr.Api.Configuration;
using Flexarr.Api.Security;
using Flexarr.Api.Updates;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Integrations.Arr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Endpoints;

/// <summary>
/// Where a server is in its first setup, step by step, read from what's set up rather than from the guide itself. A
/// step in <paramref name="Skipped"/> was passed over and counts as done.
/// </summary>
public sealed record SetupGuideDto(bool Opened, bool Dismissed, IReadOnlyList<string> Skipped, bool Apps, bool Libraries, bool Profiles,
    bool Devices, bool Away);

public sealed record SetupGuideUpdate(bool? Opened = null, bool? Dismissed = null, IReadOnlyList<string>? Skipped = null);

/// <summary>An app answering at an address, before it's connected.</summary>
public sealed record FoundArrDto(ArrKind Kind, string BaseUrl);

public sealed record ArrWebhookRequest(string Name);

/// <param name="Address">The address the app reaches Flexarr on, when it worked.</param>
public sealed record ArrWebhookDto(bool Ok, string? Address, string? Error);

public static partial class SetupGuideEndpoints
{
    /// <summary>The steps a guide can have; others are dropped.</summary>
    private static readonly HashSet<string> Steps = ["apps", "libraries", "profiles", "devices", "away"];

    /// <summary>Where each kind usually answers: its container names and port.</summary>
    private static readonly (ArrKind Kind, string[] Names, int Port)[] UsualPlaces =
    [
        (ArrKind.Sonarr, ["sonarr"], 8989), (ArrKind.Radarr, ["radarr"], 7878), (ArrKind.Lidarr, ["lidarr"], 8686),
    ];

    /// <summary>The triggers (as the apps' schemas name them) that mean a file came, went or moved: what Flexarr scans for.</summary>
    private static readonly HashSet<string> Triggers = new(StringComparer.OrdinalIgnoreCase)
    {
        "onDownload", "onUpgrade", "onImportComplete", "onRename",
        "onSeriesAdd", "onSeriesDelete", "onEpisodeFileDelete", "onEpisodeFileDeleteForUpgrade",
        "onMovieAdded", "onMovieDelete", "onMovieFileDelete", "onMovieFileDeleteForUpgrade",
        "onReleaseImport", "onArtistAdd", "onArtistDelete", "onAlbumDelete", "onTrackRetag",
    };

    public static RouteGroupBuilder MapSetupGuideEndpoints(this RouteGroupBuilder api)
    {
        // The main profile's, locked or not: Home lists what's left without asking for the PIN, and the guide's own
        // changes (to settings) ask for it as they're made.
        var group = api.MapGroup("/setup-guide").WithTags("Settings").RequireAuthorization(FlexarrClaims.MainProfilePolicy);

        group.MapGet("/", async (HttpContext http, IOptionsMonitor<FlexarrOptions> options, FlexarrDbContext db, SignIns signIns, TailscaleService tailscale,
            CancellationToken ct) =>
        {
            var current = options.CurrentValue;
            var guide = current.SetupGuide;
            // Put away for good: Home asks on every visit, and there's nothing left to work out.
            if (guide.Dismissed)
            {
                return new SetupGuideDto(guide.Opened, true, guide.Skipped, false, false, false, false, false);
            }

            return new SetupGuideDto(
                guide.Opened,
                guide.Dismissed,
                guide.Skipped,
                current.Arr.Count > 0,
                await db.Libraries.AnyAsync(l => l.Type != LibraryType.Podcasts, ct),
                await db.Profiles.CountAsync(ct) > 1,
                signIns.List().Any(s => s.Kind == SignInKind.Device),
                await tailscale.RemoteUrlAsync(LocalPort(http), ct) is not null);
        });

        group.MapPut("/", async (SetupGuideUpdate update, IOptionsMonitor<FlexarrOptions> options, CancellationToken ct) =>
        {
            var guide = options.CurrentValue.SetupGuide;
            // Answered once the options hold the change, so the guide read straight after (Home, after Hide) has it.
            await ServerConfigStore.UpdateAndWaitAsync(options, options.CurrentValue.DataDirectory, config => config["setupGuide"] = new JsonObject
            {
                ["opened"] = update.Opened ?? guide.Opened,
                ["dismissed"] = update.Dismissed ?? guide.Dismissed,
                ["skipped"] = new JsonArray([.. (update.Skipped ?? guide.Skipped).Where(Steps.Contains).Distinct().Select(s => (JsonNode)s)]),
            }, ct: ct);

            return Results.NoContent();
        });

        // Sonarr, Radarr and Lidarr (those not connected yet) answering at their usual ports nearby, to fill in the address:
        // by their own name (Docker containers on one network), at the address this browser reached Flexarr on, on this
        // machine, and on the machine running Docker.
        api.MapGet("/arr/discover", async (HttpContext http, IOptionsMonitor<FlexarrOptions> options, ArrClient client, CancellationToken ct) =>
        {
            var hosts = new List<string>();
            if (http.Request.Host.Host is { Length: > 0 } reached && !IsLoopback(reached))
            {
                hosts.Add(reached);
            }

            hosts.AddRange(["localhost", "host.docker.internal"]);
            var connected = options.CurrentValue.Arr.Select(a => a.Kind).ToHashSet();
            var candidates = UsualPlaces.Where(p => !connected.Contains(p.Kind))
                .SelectMany(p => p.Names.Concat(hosts).Select(host => (p.Kind, Url: new Uri($"http://{host}:{p.Port}"))))
                .DistinctBy(c => c.Url)
                .ToList();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var answers = await Task.WhenAll(candidates.Select(async c => (c.Kind, c.Url, Ok: await client.PingAsync(c.Url, timeout.Token).ConfigureAwait(false))));
            // One address per app, the first that answered in the order above.
            return answers.Where(a => a.Ok)
                .GroupBy(a => a.Kind)
                .Select(g => new FoundArrDto(g.Key, g.First().Url.ToString().TrimEnd('/')))
                .ToList();
        }).WithTags("Settings").RequireAuthorization(FlexarrClaims.AdminPolicy);

        // Adds Flexarr's webhook to a connected app (or brings one there is up to date), so new downloads show straight away.
        // The app has to reach Flexarr, which may be by another address than the browser's: each likely one is tried with
        // the app's own Test until one gets through.
        api.MapPost("/arr/webhook", async (ArrWebhookRequest request, HttpContext http, IOptionsMonitor<FlexarrOptions> options, IInstallEnvironment install,
            ArrClient client, ILoggerFactory loggers, CancellationToken ct) =>
        {
            var current = options.CurrentValue;
            if (current.Arr.FirstOrDefault(a => a.Name.Equals(request.Name, StringComparison.OrdinalIgnoreCase)) is not { } arr)
            {
                return Results.NotFound();
            }

            if (string.IsNullOrEmpty(current.WebhookSecret))
            {
                return Results.Ok(new ArrWebhookDto(false, null, "The server has no webhook secret yet. Restart it and try again."));
            }

            var logger = loggers.CreateLogger(typeof(SetupGuideEndpoints));
            JsonObject notification;
            try
            {
                var path = $"/api/v1/webhooks/{arr.Kind.ToString().ToLowerInvariant()}";
                // Only this server's own webhook (it has this secret) is brought up to date: another Flexarr's is left alone.
                var notifications = (await client.GetNotificationsAsync(arr, ct)).OfType<JsonObject>().ToList();
                var secret = $"secret={Uri.EscapeDataString(current.WebhookSecret)}";
                var existing = notifications.FirstOrDefault(n => IsWebhook(n) && Field(n, "url")?.GetValue<string>() is { } url
                    && url.Contains(path, StringComparison.OrdinalIgnoreCase) && url.Contains(secret, StringComparison.Ordinal));
                notification = existing ?? (await client.GetNotificationSchemaAsync(arr, ct)).OfType<JsonObject>().FirstOrDefault(IsWebhook)
                    ?? throw new InvalidOperationException($"{arr.Name} has no Webhook connection to add.");
                if (existing is null)
                {
                    // The apps want names of their own.
                    var taken = notifications.Any(n => string.Equals(n["name"]?.GetValue<string>(), "Flexarr", StringComparison.OrdinalIgnoreCase));
                    notification["name"] = taken ? $"Flexarr ({Dns.GetHostName()})" : "Flexarr";
                }

                foreach (var (key, value) in notification.ToList())
                {
                    var supported = notification[$"supports{char.ToUpperInvariant(key[0])}{key[1..]}"]?.GetValue<bool>() != false;
                    if (supported && Triggers.Contains(key) && value?.GetValueKind() is System.Text.Json.JsonValueKind.False or System.Text.Json.JsonValueKind.True)
                    {
                        notification[key] = true;
                    }
                }

                // Every likely address is tested at once (an unreachable one can keep the app waiting for seconds), and the
                // likeliest that worked is the one saved.
                var known = existing is null ? null : Field(existing, "url")?.GetValue<string>();
                var addresses = FlexarrAddresses(http, arr, install).Prepend(known is null ? null : new Uri(known).GetLeftPart(UriPartial.Authority))
                    .OfType<string>().Distinct().ToList();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                var trials = addresses.Select(async address =>
                {
                    var attempt = WebhookAt(notification, $"{address}{path}?{secret}");
                    try
                    {
                        await client.TestNotificationAsync(arr, attempt, timeout.Token);
                        return attempt;
                    }
                    catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
                    {
                        return null;
                    }
                }).ToList();
                var tried = new List<string>();
                for (var i = 0; i < addresses.Count; i++)
                {
                    if (await trials[i] is not { } working)
                    {
                        tried.Add(addresses[i]);
                        continue;
                    }

                    await client.SaveNotificationAsync(arr, working, ct);
                    LogWebhookAdded(logger, arr.Name, addresses[i]);
                    return Results.Ok(new ArrWebhookDto(true, addresses[i], null));
                }

                LogWebhookUnreachable(logger, arr.Name, string.Join(", ", tried));
                return Results.Ok(new ArrWebhookDto(false, null, $"{arr.Name} couldn't reach Flexarr. Add the webhook by hand: see Settings › Connections › Webhooks."));
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or System.Text.Json.JsonException
                || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                LogWebhookFailed(logger, ex, arr.Name);
                return Results.Ok(new ArrWebhookDto(false, null, ex.Message));
            }
        }).WithTags("Settings").RequireAuthorization(FlexarrClaims.AdminPolicy);

        return api;
    }

    private static int LocalPort(HttpContext http) => http.Connection.LocalPort > 0 ? http.Connection.LocalPort : 8383;

    private static bool IsLoopback(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip));

    private static bool IsWebhook(JsonObject n) => string.Equals(n["implementation"]?.GetValue<string>(), "Webhook", StringComparison.OrdinalIgnoreCase);

    private static JsonNode? Field(JsonObject n, string name) =>
        n["fields"]?.AsArray().OfType<JsonObject>().FirstOrDefault(f => f["name"]?.GetValue<string>() == name)?["value"];

    /// <summary>A copy of the webhook connection, calling <paramref name="url"/> by POST.</summary>
    private static JsonObject WebhookAt(JsonObject notification, string url)
    {
        var copy = notification.DeepClone().AsObject();
        SetField(copy, "url", url);
        SetField(copy, "method", 1);
        return copy;
    }

    private static void SetField(JsonObject n, string name, JsonNode value)
    {
        var fields = n["fields"] as JsonArray ?? [];
        n["fields"] = fields;
        if (fields.OfType<JsonObject>().FirstOrDefault(f => f["name"]?.GetValue<string>() == name) is { } field)
        {
            field["value"] = value;
        }
        else
        {
            fields.Add(new JsonObject { ["name"] = name, ["value"] = value });
        }
    }

    /// <summary>
    /// The addresses an app might reach Flexarr on, likeliest first: this machine's own name when both are native on it;
    /// Flexarr's container name on a shared Docker network; the address the browser used; the machine running Docker;
    /// and this machine's addresses on the home network.
    /// </summary>
    private static IEnumerable<string> FlexarrAddresses(HttpContext http, ArrInstance arr, IInstallEnvironment install)
    {
        var port = LocalPort(http);
        if (IsLoopback(arr.BaseUrl.Host))
        {
            yield return $"http://localhost:{port}";
        }

        if (install.Kind == InstallKind.Docker)
        {
            yield return $"http://{Dns.GetHostName()}:{port}";
        }

        if (http.Request.Host.Host is { Length: > 0 } reached && !IsLoopback(reached))
        {
            // HostString already brackets an IPv6 address.
            yield return $"http://{reached}:{http.Request.Host.Port ?? port}";
        }

        yield return $"http://host.docker.internal:{port}";
        foreach (var address in NetworkInterface.GetAllNetworkInterfaces()
                     .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                     .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                     .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address)))
        {
            yield return $"http://{address.Address}:{port}";
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Added Flexarr's webhook to {App}, reaching Flexarr at {Address}")]
    private static partial void LogWebhookAdded(ILogger logger, string app, string address);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{App} couldn't reach Flexarr's webhook at any of {Addresses}")]
    private static partial void LogWebhookUnreachable(ILogger logger, string app, string addresses);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't add Flexarr's webhook to {App}")]
    private static partial void LogWebhookFailed(ILogger logger, Exception exception, string app);
}
