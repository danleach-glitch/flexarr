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

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Flexarr:ApiKey", ApiKey);
        builder.UseSetting("Flexarr:AuthenticationRequired", "Enabled");
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<ILibraryScanQueue>(ScanQueue);
            services.RemoveAll<IHostedService>();
        });
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

    public IAsyncEnumerable<LibraryScanRequest> ReadAllAsync(CancellationToken cancellationToken) =>
        AsyncEnumerable.Empty<LibraryScanRequest>();
}
