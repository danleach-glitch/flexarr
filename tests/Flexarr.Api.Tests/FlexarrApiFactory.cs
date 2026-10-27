using System.Collections.Concurrent;
using System.Net;
using Flexarr.Api.Security;
using Flexarr.Core.Scanning;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
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

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Flexarr:ApiKey", ApiKey);
        builder.UseSetting("Flexarr:DataDirectory", DataDirectory);
        builder.UseSetting("Flexarr:AuthenticationRequired", "Enabled");
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
        });
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
