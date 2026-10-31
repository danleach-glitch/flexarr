using System.Collections.Concurrent;
using System.Net;
using Flexarr.Api.Security;
using Flexarr.Core.Scanning;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Flexarr.Api.Tests;

public sealed class FlexarrApiFactory : WebApplicationFactory<Program>
{
    public const string ApiKey = "test-api-key";

    public RecordingScanQueue ScanQueue { get; } = new();

    public FakeWhois Whois { get; } = new();

    public RecordingRestart Restart { get; } = new();

    public string DataDirectory { get; } = Directory.CreateTempSubdirectory("flexarr-api-").FullName;

    /// <summary>A scratch folder tests can use as a library root.</summary>
    public string MediaDirectory => Directory.CreateDirectory(Path.Combine(DataDirectory, "media")).FullName;

    /// <summary>
    /// A database with every migration applied, made once per test run. Each server starts from a copy, so starting
    /// one doesn't run the migrations again; the rest of startup runs as it does for real.
    /// </summary>
    private static readonly Lazy<string> MigratedDatabase = new(() =>
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("flexarr-api-template-").FullName, "flexarr.db");
        var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<Flexarr.Core.Data.FlexarrDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").Options;
        using (var db = new Flexarr.Core.Data.FlexarrDbContext(options))
        {
            db.Database.Migrate();
        }

        AppDomain.CurrentDomain.ProcessExit += (_, _) => Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        return path;
    });

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // A test that put a database in place (a restore, an older one) keeps its own.
        var database = Path.Combine(DataDirectory, "flexarr.db");
        if (!File.Exists(database))
        {
            File.Copy(MigratedDatabase.Value, database);
        }

        builder.UseEnvironment("Testing");
        // appsettings.json never changes during a test; watching it would only use up Linux's file watchers.
        builder.UseSetting("hostBuilder:reloadConfigOnChange", "false");
        builder.UseSetting("Flexarr:ApiKey", ApiKey);
        builder.UseSetting("Flexarr:DataDirectory", DataDirectory);
        builder.UseSetting("Flexarr:AuthenticationRequired", "Enabled");
        // Never send the tests' errors to Sentry, even with error reports switched on.
        builder.UseSetting("Flexarr:Logs:ErrorReportsDsn", "");
        builder.UseSetting("Flexarr:PathMappings:0:From", "/data");
        builder.UseSetting("Flexarr:PathMappings:0:To", "/Volumes/Media/data");
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<ILibraryScanQueue>(ScanQueue);
            services.RemoveAll<IHostedService>();
        });
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IStartupFilter, PeerFromHeader>();
            services.AddSingleton<ITailnetWhois>(Whois);
            services.AddSingleton<Flexarr.Api.Backups.IServerRestart>(Restart);
            // Tests never reach TVmaze or TMDB: they know no titles unless a test says otherwise.
            services.AddHttpClient<Flexarr.Metadata.TvMazeClient>().ConfigurePrimaryHttpMessageHandler(() => new Offline());
            services.AddHttpClient<Flexarr.Metadata.TmdbClient>().ConfigurePrimaryHttpMessageHandler(() => new Offline());
            services.AddHttpClient<Flexarr.Api.Logging.ErrorReportTunnel>().ConfigurePrimaryHttpMessageHandler(() => new Offline());
            // Nor Trakt, or its relay: Trakt isn't available unless a test stands one in (see FakeTrakt).
            services.AddHttpClient<Flexarr.Api.Trakt.TraktClient>().ConfigurePrimaryHttpMessageHandler(() => new Offline());
        });
    }

    /// <summary>Sets or clears the admin PIN directly (the API only changes it, never takes it off).</summary>
    public Task SetMainPinAsync(string? pin) => ChangeMainAsync(main => main.SetAdminPin(pin));

    /// <summary>Sets or clears the main profile's own PIN, the one choosing it asks for.</summary>
    public Task SetMainProfilePinAsync(string? pin) => ChangeMainAsync(main => main.SetPin(pin));

    private async Task ChangeMainAsync(Action<Flexarr.Core.Media.Profile> change)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Flexarr.Core.Data.FlexarrDbContext>();
        change((await db.Profiles.FindAsync(Flexarr.Core.Media.PlaybackState.DefaultUser))!);
        await db.SaveChangesAsync();
    }

    /// <summary>A web service that knows nothing.</summary>
    private sealed class Offline : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        // Both Dispose and DisposeAsync end up here; only the first call finds the directory. A transcode still
        // tidying its own folder can take part of it away mid-delete, so a vanished part is tried again, not a failure.
        for (var attempt = 0; attempt < 5 && Directory.Exists(DataDirectory); attempt++)
        {
            try
            {
                Directory.Delete(DataDirectory, recursive: true);
            }
            catch (Exception ex) when (ex is DirectoryNotFoundException or IOException)
            {
                Thread.Sleep(100);
            }
        }
    }
}

/// <summary>Sets the connecting address from X-Test-Peer, before any of Flexarr's middleware runs.</summary>
public sealed class PeerFromHeader : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((HttpContext http, RequestDelegate n) =>
        {
            if (http.Request.Headers["X-Test-Peer"].FirstOrDefault() is { } peer)
            {
                http.Connection.RemoteIpAddress = IPAddress.Parse(peer);
            }

            return n(http);
        });
        next(app);
    };
}

/// <summary>Counts restarts asked for (a restore), rather than stopping the test server.</summary>
public sealed class RecordingRestart : Flexarr.Api.Backups.IServerRestart
{
    public int Requests { get; private set; }

    public void Request() => Requests++;
}

/// <summary>Stands in for <c>tailscale whois</c>: tests say which login owns which tailnet address.</summary>
public sealed class FakeWhois : ITailnetWhois
{
    public ConcurrentDictionary<string, string> Owners { get; } = new();

    public Task<string?> LoginAsync(IPAddress address, CancellationToken ct) => Task.FromResult(Owners.GetValueOrDefault(address.ToString()));

    /// <summary>Tagged devices' tags, by address.</summary>
    public ConcurrentDictionary<string, string[]> Tags { get; } = new();

    /// <summary>Addresses Tailscale can't identify just now (whois timing out).</summary>
    public ConcurrentDictionary<string, bool> Unknown { get; } = new();

    public Task<IReadOnlyList<string>?> TagsAsync(IPAddress address, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>?>(Unknown.ContainsKey(address.ToString()) ? null : Tags.GetValueOrDefault(address.ToString()) ?? []);
}

public sealed class RecordingScanQueue : ILibraryScanQueue
{
    public List<LibraryScanRequest> Requests { get; } = [];

    public ValueTask EnqueueAsync(LibraryScanRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        return ValueTask.CompletedTask;
    }

    public bool TryDequeue(out LibraryScanRequest request)
    {
        request = null!;
        return false;
    }

    public IAsyncEnumerable<LibraryScanRequest> ReadAllAsync(CancellationToken cancellationToken) =>
        AsyncEnumerable.Empty<LibraryScanRequest>();
}
