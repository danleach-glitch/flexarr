using Flexarr.Core.Scanning;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Flexarr.Api.Tests;

public sealed class FlexarrApiFactory : WebApplicationFactory<Program>
{
    public const string ApiKey = "test-api-key";

    public RecordingScanQueue ScanQueue { get; } = new();

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
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        // Both Dispose and DisposeAsync end up here; only the first call finds the directory.
        if (Directory.Exists(DataDirectory))
        {
            Directory.Delete(DataDirectory, recursive: true);
        }
    }
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
