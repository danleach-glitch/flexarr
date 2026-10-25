using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace Flexarr.Api.Podcasts;

/// <summary>A show in Apple's podcast directory.</summary>
public sealed record DirectoryPodcast(long ITunesId, string Title, string? Author, string? FeedUrl, string? ArtworkUrl, string? Genre, bool Explicit, int? EpisodeCount);

/// <summary>A top-level category of Apple's podcast charts.</summary>
public sealed record PodcastGenre(int Id, string Name);

/// <summary>
/// Apple's podcast directory, which needs no key: search (iTunes Search), the top charts by country and category, and
/// looking shows up by id for their feeds. Answers are kept a while, as the charts change daily and searches repeat.
/// </summary>
public sealed partial class PodcastDirectory(HttpClient http, IMemoryCache cache, ILogger<PodcastDirectory> logger)
{
    /// <summary>The categories Apple's charts come in.</summary>
    public static readonly IReadOnlyList<PodcastGenre> Genres =
    [
        new(1301, "Arts"), new(1321, "Business"), new(1303, "Comedy"), new(1304, "Education"), new(1483, "Fiction"),
        new(1511, "Government"), new(1512, "Health & Fitness"), new(1487, "History"), new(1305, "Kids & Family"),
        new(1502, "Leisure"), new(1310, "Music"), new(1489, "News"), new(1314, "Religion & Spirituality"),
        new(1533, "Science"), new(1324, "Society & Culture"), new(1545, "Sports"), new(1318, "Technology"),
        new(1488, "True Crime"), new(1309, "TV & Film"),
    ];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<DirectoryPodcast>> SearchAsync(string term, string country, CancellationToken ct)
    {
        var url = $"https://itunes.apple.com/search?media=podcast&entity=podcast&limit=40&country={Uri.EscapeDataString(country)}&term={Uri.EscapeDataString(term)}";
        return await CachedAsync($"podcast-search:{country}:{term.ToLowerInvariant()}", TimeSpan.FromMinutes(30), () => LookupResultsAsync(url, ct)).ConfigureAwait(false);
    }

    /// <summary>One show, by its id in Apple's directory; null when Apple doesn't know it.</summary>
    public async Task<DirectoryPodcast?> LookupAsync(long id, string country, CancellationToken ct) =>
        await LookupManyAsync([id], country, ct).ConfigureAwait(false) is [var found, ..] ? found : null;

    /// <summary>The top shows in a country, overall or in one category, with their feeds.</summary>
    public Task<IReadOnlyList<DirectoryPodcast>> TopAsync(string country, int? genre, CancellationToken ct) =>
        CachedAsync($"podcast-top:{country}:{genre}", TimeSpan.FromHours(6), async () =>
        {
            var url = genre is { } g
                ? $"https://itunes.apple.com/{Uri.EscapeDataString(country)}/rss/toppodcasts/limit=50/genre={g.ToString(CultureInfo.InvariantCulture)}/json"
                : $"https://itunes.apple.com/{Uri.EscapeDataString(country)}/rss/toppodcasts/limit=50/json";
            using var chart = await GetJsonAsync(url, ct).ConfigureAwait(false);
            var ids = new List<long>();
            if (chart.RootElement.TryGetProperty("feed", out var feed) && feed.TryGetProperty("entry", out var entries))
            {
                // One entry comes as an object rather than a list of one.
                foreach (var entry in entries.ValueKind == JsonValueKind.Array ? entries.EnumerateArray() : Enumerable.Repeat(entries, 1))
                {
                    if (entry.TryGetProperty("id", out var id) && id.TryGetProperty("attributes", out var attributes)
                        && attributes.TryGetProperty("im:id", out var imId) && long.TryParse(imId.GetString(), CultureInfo.InvariantCulture, out var value))
                    {
                        ids.Add(value);
                    }
                }
            }

            // The chart has no feeds in it: look them up, in its order.
            var found = (await LookupManyAsync(ids, country, ct).ConfigureAwait(false)).ToDictionary(p => p.ITunesId);
            return (IReadOnlyList<DirectoryPodcast>)ids.Where(found.ContainsKey).Select(id => found[id]).ToList();
        });

    private async Task<IReadOnlyList<DirectoryPodcast>> LookupManyAsync(List<long> ids, string country, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var url = $"https://itunes.apple.com/lookup?entity=podcast&country={Uri.EscapeDataString(country)}&id={string.Join(',', ids.Select(i => i.ToString(CultureInfo.InvariantCulture)))}";
        return await LookupResultsAsync(url, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<DirectoryPodcast>> LookupResultsAsync(string url, CancellationToken ct)
    {
        using var document = await GetJsonAsync(url, ct).ConfigureAwait(false);
        var results = document.RootElement.TryGetProperty("results", out var r) ? r.Deserialize<List<ITunesResult>>(Json) ?? [] : [];
        return results.Where(x => x.Kind == "podcast" && x.CollectionId > 0 && !string.IsNullOrWhiteSpace(x.CollectionName))
            .Select(x => new DirectoryPodcast(x.CollectionId, x.CollectionName!.Trim(), x.ArtistName?.Trim(), Core.Podcasts.FeedParser.WebUrl(x.FeedUrl),
                x.ArtworkUrl600 ?? x.ArtworkUrl100, x.PrimaryGenreName, x.CollectionExplicitness == "explicit", x.TrackCount))
            .ToList();
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(PodcastFetcher.UserAgent);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            LogDirectoryFailed(url, (int)response.StatusCode);
            throw new HttpRequestException("Apple's podcast directory isn't answering. Try again in a minute.");
        }

        await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(body, cancellationToken: ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<DirectoryPodcast>> CachedAsync(string key, TimeSpan keep, Func<Task<IReadOnlyList<DirectoryPodcast>>> fetch)
    {
        if (cache.TryGetValue(key, out IReadOnlyList<DirectoryPodcast>? hit) && hit is not null)
        {
            return hit;
        }

        var result = await fetch().ConfigureAwait(false);
        cache.Set(key, result, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = keep, Size = 1 });
        return result;
    }

    private sealed record ITunesResult(
        string? Kind,
        long CollectionId,
        string? CollectionName,
        string? ArtistName,
        string? FeedUrl,
        string? ArtworkUrl100,
        string? ArtworkUrl600,
        string? PrimaryGenreName,
        string? CollectionExplicitness,
        int? TrackCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Apple's podcast directory answered {Status} for {Url}")]
    private partial void LogDirectoryFailed(string url, int status);
}
