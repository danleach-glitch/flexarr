using System.Text.Json;
using Flexarr.Api.Configuration;
using Flexarr.Core.LiveTv;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.LiveTv;

/// <summary>
/// The community logo collection's file list (github.com/tv-logo/tv-logos), fetched with one call to GitHub at most
/// once a week and kept on disk. Without it (GitHub can't be reached, and nothing kept), channels keep the source's
/// own logos.
/// </summary>
public sealed partial class CommunityLogoIndex(IHttpClientFactory clients, IOptionsMonitor<FlexarrOptions> options, TimeProvider clock, ILogger<CommunityLogoIndex> logger)
    : IDisposable
{
    public const string ClientName = "community-logos";

    private const string TreeUrl = "https://api.github.com/repos/tv-logo/tv-logos/git/trees/main?recursive=1";
    private static readonly TimeSpan KeptFor = TimeSpan.FromDays(7);

    private readonly SemaphoreSlim _loading = new(1, 1);
    private CommunityLogos? _logos;
    private DateTime _loadedAt;

    public async Task<CommunityLogos?> GetAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        if (_logos is not null && now - _loadedAt < KeptFor)
        {
            return _logos;
        }

        await _loading.WaitAsync(ct);
        try
        {
            if (_logos is not null && now - _loadedAt < KeptFor)
            {
                return _logos;
            }

            var file = Path.Combine(options.CurrentValue.ResolvedCacheDirectory, "livetv-logos", "community-index.json");
            var fresh = File.Exists(file) && now - File.GetLastWriteTimeUtc(file) < KeptFor;
            if (!fresh && await DownloadAsync(file, ct) is { } downloaded)
            {
                (_logos, _loadedAt) = (downloaded, now);
                return _logos;
            }

            // Kept from before (stale, perhaps, but logos rarely change): better than none.
            if (File.Exists(file))
            {
                _logos = new CommunityLogos(JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(file, ct)) ?? []);
                _loadedAt = fresh ? File.GetLastWriteTimeUtc(file) : now - KeptFor + TimeSpan.FromHours(6);
            }

            return _logos;
        }
        finally
        {
            _loading.Release();
        }
    }

    private async Task<CommunityLogos?> DownloadAsync(string file, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, TreeUrl);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await clients.CreateClient(ClientName).SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                LogFailed($"GitHub answered {(int)response.StatusCode}");
                return null;
            }

            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var paths = doc.RootElement.GetProperty("tree").EnumerateArray()
                .Where(e => e.GetProperty("type").GetString() == "blob")
                .Select(e => e.GetProperty("path").GetString())
                .OfType<string>()
                .Where(p => p.StartsWith("countries/", StringComparison.Ordinal) && p.EndsWith(".png", StringComparison.Ordinal))
                .ToArray();
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(paths), ct);
            var logos = new CommunityLogos(paths);
            LogLoaded(logos.Count);
            return logos;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException
            or TaskCanceledException && !ct.IsCancellationRequested)
        {
            LogFailed(ex.Message);
            return null;
        }
    }

    public void Dispose() => _loading.Dispose();

    [LoggerMessage(Level = LogLevel.Information, Message = "Community channel logos: {Count} in the collection")]
    private partial void LogLoaded(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't fetch the community channel logo list: {Reason}")]
    private partial void LogFailed(string reason);
}
