using System.Collections.Concurrent;
using System.Net;
using Flexarr.Integrations.Arr;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

/// <summary>Stands in for Sonarr and Radarr: answers by method and path, and records what was asked.</summary>
public sealed class FakeArr : HttpMessageHandler
{
    /// <summary>
    /// Bodies by "METHOD /api/v3/path?query", or "METHOD //host/api/v3/path?query" for one instance only (Sonarr and
    /// Radarr share some paths); anything else is a 404.
    /// </summary>
    public ConcurrentDictionary<string, string> Responses { get; } = new();

    public ConcurrentQueue<string> Requests { get; } = new();

    /// <summary>Runs as each request arrives, before it's answered.</summary>
    public Action<HttpRequestMessage>? OnRequest { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var key = $"{request.Method} {request.RequestUri!.PathAndQuery}";
        Requests.Enqueue(key);
        OnRequest?.Invoke(request);
        return Task.FromResult(Responses.TryGetValue($"{request.Method} //{request.RequestUri.Host}{request.RequestUri.PathAndQuery}", out var body) || Responses.TryGetValue(key, out body)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") }
            : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    /// <summary>The test server, with a Sonarr and a Radarr (both named after their kind) answered by this.</summary>
    /// <param name="factory">The server under test.</param>
    /// <param name="bookshelf">Add a Readarr-family instance named "Bookshelf" at bookshelf.test.</param>
    /// <param name="bookRequests">With it, book requests (a preview) are switched on.</param>
    public WebApplicationFactory<Program> Serve(FlexarrApiFactory factory, bool bookshelf = false, bool bookRequests = true) => factory.WithWebHostBuilder(builder =>
    {
        foreach (var (kind, i) in new[] { (ArrKind.Sonarr, 0), (ArrKind.Radarr, 1) })
        {
            builder.UseSetting($"Flexarr:Arr:{i}:Name", kind.ToString());
            builder.UseSetting($"Flexarr:Arr:{i}:Kind", kind.ToString());
            builder.UseSetting($"Flexarr:Arr:{i}:BaseUrl", $"http://{kind.ToString().ToLowerInvariant()}.test");
            builder.UseSetting($"Flexarr:Arr:{i}:ApiKey", "key");
        }

        if (bookshelf)
        {
            builder.UseSetting("Flexarr:Arr:2:Name", "Bookshelf");
            builder.UseSetting("Flexarr:Arr:2:Kind", nameof(ArrKind.Readarr));
            builder.UseSetting("Flexarr:Arr:2:BaseUrl", "http://bookshelf.test");
            builder.UseSetting("Flexarr:Arr:2:ApiKey", "key");
            builder.UseSetting("Flexarr:BookRequests:Enabled", bookRequests ? "true" : "false");
        }

        builder.ConfigureTestServices(services => services.AddHttpClient<ArrClient>().ConfigurePrimaryHttpMessageHandler(() => this));
    });
}
