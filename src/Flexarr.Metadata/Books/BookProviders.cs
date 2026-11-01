using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flexarr.Core.Books;

namespace Flexarr.Metadata.Books;

/// <summary>How the server looks books up online. Admins set the order and Audible's region; keys are optional.</summary>
/// <param name="Region">Audible's store: "us", "uk", "ca", "au", "de", "fr", "it", "es", "in", "jp".</param>
/// <param name="Providers">The providers to ask, in order: "audible", "openlibrary", "google", "hardcover".</param>
public sealed record BookMetadataSettings(string Region, IReadOnlyList<string> Providers, string? GoogleBooksKey = null, string? HardcoverToken = null)
{
    public static readonly IReadOnlyList<string> AllProviders = ["audible", "openlibrary", "google", "hardcover"];

    public static BookMetadataSettings Default { get; } = new("us", AllProviders);

    public static readonly IReadOnlyList<string> Regions = ["us", "uk", "ca", "au", "de", "fr", "it", "es", "in", "jp"];
}

/// <summary>A book as an online provider describes it.</summary>
/// <param name="Provider">"audible", "openlibrary", "google" or "hardcover".</param>
/// <param name="Id">Its id there (an ASIN for Audible).</param>
public sealed record BookMatch(
    string Provider, string Id, string Title, string? Subtitle, IReadOnlyList<string> Authors, IReadOnlyList<string> Narrators,
    IReadOnlyList<SeriesPart> Series, string? Description, string? Publisher, DateOnly? ReleaseDate, string? Language, string? Isbn, string? Asin,
    string? Cover, IReadOnlyList<string> Genres, double? DurationSeconds, bool? Abridged = null, IReadOnlyList<string>? AuthorAsins = null)
{
    public BookInfo ToInfo() => new()
    {
        Title = Title, Subtitle = Subtitle, Authors = Authors, Narrators = Narrators, Series = Series, Description = Description,
        Publisher = Publisher, ReleaseDate = ReleaseDate, Year = ReleaseDate?.Year, Language = Language, Isbn = Isbn, Asin = Asin,
        Genres = Genres, Abridged = Abridged,
    };
}

/// <summary>An author's photo and biography.</summary>
public sealed record BookAuthorInfo(string Name, string? Biography, string? Image);

/// <summary>Audible's chapters for an edition, with how long its opening and closing branding run.</summary>
public sealed record AudibleChapters(IReadOnlyList<BookChapter> Chapters, double RuntimeSeconds, bool Accurate, double IntroSeconds);

/// <summary>
/// Keeps Flexarr polite to the book providers: a couple of requests at a time to each host, and a host that answers
/// 429 (too many requests) left alone until it said to come back (a minute, if it didn't say). One for the whole
/// server; <see cref="BookProviders"/> is made per request.
/// </summary>
public sealed class BookProviderThrottle(TimeProvider? time = null)
{
    public static BookProviderThrottle Shared { get; } = new();

    private const int PerHost = 2;
    private static readonly TimeSpan DefaultBackOff = TimeSpan.FromMinutes(1), LongestBackOff = TimeSpan.FromHours(1);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _coolingUntil = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether a host asked to be left alone, and still is.</summary>
    public bool IsCooling(string host) => _coolingUntil.TryGetValue(host, out var until) && until > _time.GetUtcNow();

    /// <summary>Sends a request when its host has room; null, unsent, while the host is backing Flexarr off.</summary>
    /// <param name="backOff">
    /// Whether a 429 backs every request to the host off. Authors' lookups don't: they stop themselves for the rest of
    /// the refresh, and a 429 on an author shouldn't keep a minute of books from being matched.
    /// </param>
    public async Task<HttpResponseMessage?> SendAsync(HttpClient http, HttpRequestMessage request, CancellationToken ct, bool backOff = true)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(request);
        var host = request.RequestUri!.Host;
        if (IsCooling(host))
        {
            return null;
        }

        var gate = _gates.GetOrAdd(host, _ => new SemaphoreSlim(PerHost));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsCooling(host))
            {
                return null; // backed off while this one waited its turn
            }

            var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests && backOff)
            {
                var now = _time.GetUtcNow();
                var wait = response.Headers.RetryAfter is { } retry
                    ? retry.Delta ?? (retry.Date is { } date ? date - now : DefaultBackOff)
                    : DefaultBackOff;
                _coolingUntil[host] = now + TimeSpan.FromTicks(Math.Clamp(wait.Ticks, TimeSpan.FromSeconds(5).Ticks, LongestBackOff.Ticks));
            }

            return response;
        }
        finally
        {
            gate.Release();
        }
    }
}

/// <summary>
/// Looks books up online: Audible (through Audnexus, and Audible's own catalogue for searching), Open Library, Google
/// Books and Hardcover. All of them are someone else's service, Audnexus and the catalogue unofficially so, so answers
/// are kept on disk for a month and nothing depends on any of them staying up: a provider that fails is skipped, and
/// one that says it's had too many requests is left alone for a while (see <see cref="BookProviderThrottle"/>).
/// </summary>
public sealed partial class BookProviders(HttpClient http, Func<IMetadataSettings> settings, BookProviderThrottle? throttle = null)
{
    private readonly BookProviderThrottle _throttle = throttle ?? BookProviderThrottle.Shared;

    private static readonly TimeSpan CacheFor = TimeSpan.FromDays(30);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private BookMetadataSettings Options => settings().Books;

    private IEnumerable<string> Enabled => Options.Providers.Where(p => p switch
    {
        "google" => Options.GoogleBooksKey is { Length: > 0 },
        "hardcover" => Options.HardcoverToken is { Length: > 0 },
        _ => BookMetadataSettings.AllProviders.Contains(p),
    });

    /// <summary>The first provider's answer for a book by its ids: an ASIN through Audible, an ISBN through the rest.</summary>
    public async Task<BookMatch?> LookupAsync(string? asin, string? isbn, CancellationToken ct)
    {
        foreach (var provider in Enabled)
        {
            var match = provider switch
            {
                "audible" when asin is { Length: > 0 } => await TryAsync(() => AudibleBookAsync(asin, ct)).ConfigureAwait(false),
                "openlibrary" when isbn is { Length: > 0 } => await TryAsync(() => OpenLibraryIsbnAsync(isbn, ct)).ConfigureAwait(false),
                "google" when isbn is { Length: > 0 } => (await TryListAsync(() => GoogleSearchAsync($"isbn:{isbn}", ct)).ConfigureAwait(false)).FirstOrDefault(),
                _ => null,
            };
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    /// <summary>Every provider's results for a title and author, in the admin's order: for Fix match.</summary>
    public async Task<List<BookMatch>> SearchAsync(string title, string? author, CancellationToken ct)
    {
        var results = new List<BookMatch>();
        foreach (var provider in Enabled)
        {
            results.AddRange(await SearchOneAsync(provider, title, author, ct).ConfigureAwait(false));
        }

        return results;
    }

    private Task<List<BookMatch>> SearchOneAsync(string provider, string title, string? author, CancellationToken ct) => TryListAsync(() => provider switch
    {
        "audible" => AudibleSearchAsync(title, author, ct),
        "openlibrary" => OpenLibrarySearchAsync(title, author, ct),
        "google" => GoogleSearchAsync($"intitle:{title}" + (author is null ? "" : $"+inauthor:{author}"), ct),
        "hardcover" => HardcoverSearchAsync(title, author, ct),
        _ => Task.FromResult(new List<BookMatch>()),
    });

    /// <summary>
    /// A book we have, found by searching: the same title (ignoring case and punctuation) by one of its authors. The
    /// providers are asked in the admin's order, and the first with such a book answers; the rest aren't asked.
    /// </summary>
    public async Task<BookMatch?> FindAsync(string title, IReadOnlyList<string> authors, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(authors);
        var wanted = Fold(title);
        var main = Fold(MainTitle(title));
        var names = authors.Select(Fold).ToHashSet(StringComparer.Ordinal);
        foreach (var provider in Enabled)
        {
            foreach (var match in await SearchOneAsync(provider, MainTitle(title), authors.Count > 0 ? authors[0] : null, ct).ConfigureAwait(false))
            {
                var theirs = Fold(match.Title);
                if ((theirs == wanted || theirs == main || Fold(MainTitle(match.Title)) == main)
                    && (names.Count == 0 || match.Authors.Any(a => names.Contains(Fold(a)))))
                {
                    return match;
                }
            }
        }

        return null;
    }

    // ---- Audible, through Audnexus and Audible's catalogue ----

    private string AudibleHost => Options.Region switch
    {
        "uk" => "api.audible.co.uk", "ca" => "api.audible.ca", "au" => "api.audible.com.au", "de" => "api.audible.de", "fr" => "api.audible.fr",
        "it" => "api.audible.it", "es" => "api.audible.es", "in" => "api.audible.in", "jp" => "api.audible.co.jp", _ => "api.audible.com",
    };

    private async Task<BookMatch?> AudibleBookAsync(string asin, CancellationToken ct)
    {
        var book = await GetAsync($"https://api.audnex.us/books/{Uri.EscapeDataString(asin)}?region={Options.Region}", ct).ConfigureAwait(false);
        if (book is null || book["asin"] is null)
        {
            return null;
        }

        var series = new List<SeriesPart>();
        foreach (var key in new[] { "seriesPrimary", "seriesSecondary" })
        {
            if (book[key] is JsonObject s && Text(s, "name") is { } name)
            {
                series.Add(new SeriesPart(name, Text(s, "position")));
            }
        }

        return new BookMatch("audible", asin, Text(book, "title") ?? asin, Text(book, "subtitle"), Names(book["authors"]), Names(book["narrators"]), series,
            Text(book, "summary") is { } summary ? BookSidecars.PlainText(summary) : Text(book, "description"), Text(book, "publisherName"),
            Date(Text(book, "releaseDate")), Language(Text(book, "language")), Text(book, "isbn"), asin, Text(book, "image"),
            [.. (book["genres"] as JsonArray ?? []).OfType<JsonObject>().Where(g => Text(g, "type") == "genre").Select(g => Text(g, "name")).OfType<string>()],
            book["runtimeLengthMin"]?.GetValue<double>() * 60, Text(book, "formatType") is { } format ? format == "abridged" : null,
            [.. (book["authors"] as JsonArray ?? []).OfType<JsonObject>().Select(a => Text(a, "asin")).OfType<string>()]);
    }

    private async Task<List<BookMatch>> AudibleSearchAsync(string title, string? author, CancellationToken ct)
    {
        var url = $"https://{AudibleHost}/1.0/catalog/products?title={Uri.EscapeDataString(title)}"
            + (author is null ? "" : $"&author={Uri.EscapeDataString(author)}")
            + "&num_results=10&products_sort_by=Relevance&response_groups=contributors,series,media,product_attrs,product_desc";
        var found = await GetAsync(url, ct).ConfigureAwait(false);
        return [.. (found?["products"] as JsonArray ?? []).OfType<JsonObject>().Where(p => Text(p, "asin") is not null).Select(p => new BookMatch(
            "audible", Text(p, "asin")!, Text(p, "title") ?? "", Text(p, "subtitle"), Names(p["authors"]), Names(p["narrators"]),
            [.. (p["series"] as JsonArray ?? []).OfType<JsonObject>().Where(s => Text(s, "title") is not null).Select(s => new SeriesPart(Text(s, "title")!, Text(s, "sequence")))],
            Text(p, "merchandising_summary") is { } m ? BookSidecars.PlainText(m) : null, Text(p, "publisher_name"), Date(Text(p, "release_date")),
            Language(Text(p, "language")), null, Text(p, "asin"),
            p["product_images"] is JsonObject images ? images.Select(i => i.Value?.GetValue<string>()).LastOrDefault(i => i is not null) : null,
            [], p["runtime_length_min"]?.GetValue<double>() * 60, Text(p, "format_type") is { } f ? f == "abridged" : null))];
    }

    /// <summary>Audible's chapters for an ASIN; null when Audnexus has none.</summary>
    public async Task<AudibleChapters?> ChaptersAsync(string asin, CancellationToken ct)
    {
        var found = await TryAsync(() => GetAsync($"https://api.audnex.us/books/{Uri.EscapeDataString(asin)}/chapters?region={Options.Region}", ct)).ConfigureAwait(false);
        if (found?["chapters"] is not JsonArray list || list.Count == 0)
        {
            return null;
        }

        var chapters = list.OfType<JsonObject>().Select((c, i) =>
        {
            var start = (c["startOffsetMs"]?.GetValue<double>() ?? 0) / 1000;
            return new BookChapter(Text(c, "title") ?? $"Chapter {i + 1}", start, start + ((c["lengthMs"]?.GetValue<double>() ?? 0) / 1000));
        }).ToList();
        return new AudibleChapters(chapters, (found["runtimeLengthMs"]?.GetValue<double>() ?? 0) / 1000, found["isAccurate"]?.GetValue<bool>() ?? false,
            (found["brandIntroDurationMs"]?.GetValue<double>() ?? 0) / 1000);
    }

    // ---- Authors: unlike books, a provider that can't be asked throws, so the author is looked up again later ----

    /// <summary>Whether the admin left a provider switched on (authors need no keys).</summary>
    public bool Uses(string provider) => Options.Providers.Contains(provider, StringComparer.Ordinal);

    /// <summary>
    /// An author's photo and biography from Audible, by their ASIN; null when Audible doesn't know them or is switched
    /// off.
    /// </summary>
    /// <exception cref="HttpRequestException">Audnexus couldn't be asked just now.</exception>
    public async Task<BookAuthorInfo?> AuthorAsync(string asin, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(asin);
        if (!Uses("audible"))
        {
            return null;
        }

        var author = await GetOrThrowAsync($"https://api.audnex.us/authors/{Uri.EscapeDataString(asin)}?region={Options.Region}", ct).ConfigureAwait(false);
        return author is null || Text(author, "name") is not { } name ? null
            : new BookAuthorInfo(name, Text(author, "description"), Text(author, "image"));
    }

    /// <summary>
    /// Audible's ASIN for an author of exactly this name (ignoring case, spaces and punctuation, so "J.R.R. Tolkien" is
    /// "J. R. R. Tolkien"): one a book of ours already lists (<paramref name="known"/>), else the only one of that name.
    /// Null when there's none, when there are several and none is known (a namesake could be taken for them), or when
    /// Audible is switched off.
    /// </summary>
    /// <exception cref="HttpRequestException">Audnexus couldn't be asked just now.</exception>
    public async Task<string?> FindAudibleAuthorAsync(string name, IReadOnlyCollection<string> known, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(known);
        if (!Uses("audible") || Fold(name) is not { Length: > 0 } wanted)
        {
            return null;
        }

        var found = await GetOrThrowAsync($"https://api.audnex.us/authors?name={Uri.EscapeDataString(name.Trim())}&region={Options.Region}", ct, array: true).ConfigureAwait(false);
        var asins = (found as JsonArray ?? []).OfType<JsonObject>()
            .Where(a => Text(a, "name") is { } theirs && Fold(theirs) == wanted).Select(a => Text(a, "asin")).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return asins.FirstOrDefault(a => known.Contains(a, StringComparer.OrdinalIgnoreCase)) ?? (asins.Count == 1 ? asins[0] : null);
    }

    /// <summary>Authors with the same name looked at on Open Library, at most, for one who wrote our books.</summary>
    private const int OpenLibraryNamesakes = 5;

    /// <summary>
    /// An author's biography and photo from Open Library: someone of exactly this name who wrote one of
    /// <paramref name="titles"/> (by their main title, case and punctuation aside), so a namesake isn't taken for them.
    /// The photo only when Open Library has one. Null when there's no such author, or Open Library is switched off.
    /// </summary>
    /// <exception cref="HttpRequestException">Open Library couldn't be asked just now.</exception>
    public async Task<BookAuthorInfo?> OpenLibraryAuthorAsync(string name, IReadOnlyCollection<string> titles, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(titles);
        var ours = titles.Select(t => Fold(MainTitle(t))).Where(t => t.Length > 0).ToHashSet(StringComparer.Ordinal);
        if (!Uses("openlibrary") || Fold(name) is not { Length: > 0 } wanted || ours.Count == 0)
        {
            return null;
        }

        var found = await GetOrThrowAsync($"https://openlibrary.org/search/authors.json?q={Uri.EscapeDataString(name.Trim())}", ct).ConfigureAwait(false);
        var namesakes = (found?["docs"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(d => Text(d, "name") is { } theirs && Fold(theirs) == wanted)
            .Select(d => (Key: Text(d, "key")?.Replace("/authors/", "", StringComparison.Ordinal), Doc: d))
            .Where(d => d.Key is not null && OpenLibraryAuthorKey().IsMatch(d.Key))
            .Take(OpenLibraryNamesakes);
        foreach (var (key, doc) in namesakes)
        {
            var wrote = Text(doc, "top_work") is { } top && ours.Contains(Fold(MainTitle(top)));
            if (!wrote)
            {
                var works = await GetOrThrowAsync($"https://openlibrary.org/authors/{key}/works.json?limit=50", ct).ConfigureAwait(false);
                wrote = (works?["entries"] as JsonArray ?? []).OfType<JsonObject>()
                    .Any(w => Text(w, "title") is { } title && ours.Contains(Fold(MainTitle(title))));
            }

            if (!wrote)
            {
                continue; // someone else of the same name
            }

            var author = await GetOrThrowAsync($"https://openlibrary.org/authors/{key}.json", ct).ConfigureAwait(false);
            if (author is null)
            {
                continue;
            }

            var bio = author["bio"] is JsonObject b ? Text(b, "value") : Text(author, "bio");
            var photo = (author["photos"] as JsonArray ?? []).OfType<JsonValue>()
                .Any(p => p.GetValueKind() == JsonValueKind.Number && p.GetValue<long>() > 0);
            return new BookAuthorInfo(Text(author, "name") ?? Text(doc, "name")!, bio,
                photo ? $"https://covers.openlibrary.org/a/olid/{key}-L.jpg?default=false" : null);
        }

        return null;
    }

    [System.Text.RegularExpressions.GeneratedRegex("^OL[0-9]+A$")]
    private static partial System.Text.RegularExpressions.Regex OpenLibraryAuthorKey();

    // ---- Open Library ----

    private async Task<BookMatch?> OpenLibraryIsbnAsync(string isbn, CancellationToken ct)
    {
        var edition = await GetAsync($"https://openlibrary.org/isbn/{Uri.EscapeDataString(isbn)}.json", ct).ConfigureAwait(false);
        if (edition is null || Text(edition, "title") is not { } title)
        {
            return null;
        }

        var cover = (edition["covers"] as JsonArray)?.FirstOrDefault()?.GetValue<long>();
        return new BookMatch("openlibrary", Text(edition, "key") ?? isbn, title, Text(edition, "subtitle"), [], [], [],
            edition["description"] is JsonObject d ? Text(d, "value") : Text(edition, "description"),
            (edition["publishers"] as JsonArray)?.FirstOrDefault()?.GetValue<string>(), Date(Text(edition, "publish_date")), null, isbn, null,
            cover is { } c ? $"https://covers.openlibrary.org/b/id/{c}-L.jpg" : null, [], null);
    }

    private async Task<List<BookMatch>> OpenLibrarySearchAsync(string title, string? author, CancellationToken ct)
    {
        var url = $"https://openlibrary.org/search.json?title={Uri.EscapeDataString(title)}" + (author is null ? "" : $"&author={Uri.EscapeDataString(author)}")
            + "&limit=5&fields=key,title,subtitle,author_name,first_publish_year,isbn,cover_i,publisher,language,subject";
        var found = await GetAsync(url, ct).ConfigureAwait(false);
        return [.. (found?["docs"] as JsonArray ?? []).OfType<JsonObject>().Where(d => Text(d, "title") is not null).Select(d => new BookMatch(
            "openlibrary", Text(d, "key") ?? "", Text(d, "title")!, Text(d, "subtitle"), Strings(d["author_name"]), [], [], null,
            Strings(d["publisher"]).FirstOrDefault(), d["first_publish_year"] is { } y ? new DateOnly(y.GetValue<int>(), 1, 1) : null, null,
            Strings(d["isbn"]).FirstOrDefault(i => i.Length == 13), null,
            d["cover_i"] is { } c ? $"https://covers.openlibrary.org/b/id/{c.GetValue<long>()}-L.jpg" : null, [.. Strings(d["subject"]).Take(5)], null))];
    }

    // ---- Google Books (with a key) ----

    private async Task<List<BookMatch>> GoogleSearchAsync(string query, CancellationToken ct)
    {
        var url = $"https://www.googleapis.com/books/v1/volumes?q={Uri.EscapeDataString(query)}&maxResults=5&key={Uri.EscapeDataString(Options.GoogleBooksKey ?? "")}";
        var found = await GetAsync(url, ct, cacheKey: $"google:{query}").ConfigureAwait(false);
        return [.. (found?["items"] as JsonArray ?? []).OfType<JsonObject>().Select(i => (Id: Text(i, "id"), Info: i["volumeInfo"] as JsonObject))
            .Where(v => v.Id is not null && v.Info is not null && Text(v.Info, "title") is not null).Select(v => new BookMatch(
                "google", v.Id!, Text(v.Info!, "title")!, Text(v.Info!, "subtitle"), Strings(v.Info!["authors"]), [], [],
                Text(v.Info!, "description") is { } desc ? BookSidecars.PlainText(desc) : null, Text(v.Info!, "publisher"), Date(Text(v.Info!, "publishedDate")),
                Text(v.Info!, "language"),
                (v.Info!["industryIdentifiers"] as JsonArray ?? []).OfType<JsonObject>().Where(x => Text(x, "type") == "ISBN_13").Select(x => Text(x, "identifier")).FirstOrDefault(),
                null, (v.Info!["imageLinks"] as JsonObject) is { } links ? Text(links, "thumbnail")?.Replace("http://", "https://", StringComparison.Ordinal) : null,
                [.. Strings(v.Info!["categories"]).SelectMany(c => c.Split('/', StringSplitOptions.TrimEntries)).Distinct().Take(5)], null))];
    }

    // ---- Hardcover (with a token) ----

    private async Task<List<BookMatch>> HardcoverSearchAsync(string title, string? author, CancellationToken ct)
    {
        const string Query = "query($q: String!) { search(query: $q, query_type: \"Book\", per_page: 5) { results } }";
        var q = author is null ? title : $"{title} {author}";
        var key = $"hardcover:{q}";
        var found = await CachedAsync(key, async () =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.hardcover.app/v1/graphql")
            {
                Content = JsonContent.Create(new { query = Query, variables = new { q } }),
            };
            request.Headers.TryAddWithoutValidation("authorization", Options.HardcoverToken!.StartsWith("Bearer ", StringComparison.Ordinal) ? Options.HardcoverToken : $"Bearer {Options.HardcoverToken}");
            using var response = await _throttle.SendAsync(http, request, ct).ConfigureAwait(false);
            return response?.IsSuccessStatusCode == true ? await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false) : null;
        }).ConfigureAwait(false);
        var hits = found?["data"]?["search"]?["results"]?["hits"] as JsonArray ?? [];
        return [.. hits.OfType<JsonObject>().Select(h => h["document"] as JsonObject).OfType<JsonObject>().Where(d => Text(d, "title") is not null).Select(d => new BookMatch(
            "hardcover", Text(d, "id") ?? Text(d, "slug") ?? "", Text(d, "title")!, Text(d, "subtitle"), Strings(d["author_names"]), [],
            d["featured_series"] is JsonObject fs && fs["series"] is JsonObject s && Text(s, "name") is { } sn ? [new SeriesPart(sn, Text(fs, "position"))] : [],
            Text(d, "description"), null, d["release_year"] is { } ry && ry.GetValueKind() == JsonValueKind.Number ? new DateOnly(ry.GetValue<int>(), 1, 1) : null, null,
            Strings(d["isbns"]).FirstOrDefault(i => i.Length == 13), null, d["image"] is JsonObject img ? Text(img, "url") : null, [.. Strings(d["genres"]).Take(5)], null))];
    }

    // ---- Plumbing ----

    private Task<JsonNode?> GetAsync(string url, CancellationToken ct, string? cacheKey = null) => CachedAsync(cacheKey ?? url, async () =>
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("Flexarr/1.0 (+https://flexarr.io)");
        using var response = await _throttle.SendAsync(http, request, ct).ConfigureAwait(false);
        if (response is null)
        {
            return null; // backing off: asked again later
        }

        // A book a provider doesn't have is an answer worth keeping; a provider that's struggling isn't.
        if (response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.BadRequest)
        {
            return "null";
        }

        return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false) : null;
    });

    /// <summary>As <see cref="GetAsync"/>, but a provider that's down, or backing Flexarr off, throws rather than answering null.</summary>
    private Task<JsonNode?> GetOrThrowAsync(string url, CancellationToken ct, bool array = false) => CachedAsync(url, async () =>
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("Flexarr/1.0 (+https://flexarr.io)");
        using var response = await _throttle.SendAsync(http, request, ct, backOff: false).ConfigureAwait(false)
            ?? throw new HttpRequestException($"{request.RequestUri!.Host} asked to be left alone for a while.");
        if (response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.BadRequest)
        {
            return "null";
        }

        return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)
            : throw new HttpRequestException($"{request.RequestUri!.Host} answered {(int)response.StatusCode}.", null, response.StatusCode);
    }, array);

    /// <summary>
    /// An answer from the disk cache, or fetched and kept; null (not kept) when fetching failed, and null for anything
    /// but a JSON object (or, with <paramref name="array"/>, a list).
    /// </summary>
    private async Task<JsonNode?> CachedAsync(string key, Func<Task<string?>> fetch, bool array = false)
    {
        var directory = Path.Combine(settings().CacheDirectory, "book-metadata");
        var file = Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32] + ".json");
        if (File.Exists(file) && File.GetLastWriteTimeUtc(file) > DateTime.UtcNow - CacheFor)
        {
            return Shape(JsonNode.Parse(await File.ReadAllTextAsync(file).ConfigureAwait(false)));
        }

        var body = await fetch().ConfigureAwait(false);
        if (body is null)
        {
            return null;
        }

        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(file, body).ConfigureAwait(false);
        return Shape(JsonNode.Parse(body)); // every answer worth reading is an object, or a list asked for

        JsonNode? Shape(JsonNode? node) => node is JsonObject || (array && node is JsonArray) ? node : null;
    }

    private static async Task<T?> TryAsync<T>(Func<Task<T?>> call) where T : class
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static async Task<List<BookMatch>> TryListAsync(Func<Task<List<BookMatch>>> call) =>
        await TryAsync(async () => (List<BookMatch>?)await call().ConfigureAwait(false)).ConfigureAwait(false) ?? [];

    private static string? Text(JsonNode? node, string name) =>
        node?[name] is JsonValue v && v.GetValueKind() is JsonValueKind.String or JsonValueKind.Number
            ? v.ToString() is { Length: > 0 } s ? s.Trim() : null
            : null;

    private static List<string> Names(JsonNode? array) =>
        [.. (array as JsonArray ?? []).OfType<JsonObject>().Select(a => Text(a, "name")).OfType<string>()];

    private static List<string> Strings(JsonNode? array) =>
        [.. (array as JsonArray ?? []).OfType<JsonValue>().Select(v => v.ToString()).Where(s => s.Length > 0)];

    /// <summary>"2020-09-03T00:00:00.000Z", "2021-05-04", "May 04, 2021" or "2004".</summary>
    private static DateOnly? Date(string? value)
    {
        if (value is null)
        {
            return null;
        }

        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date))
        {
            return DateOnly.FromDateTime(date);
        }

        return value.Length >= 4 && int.TryParse(value[..4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var year) && year > 1000 ? new DateOnly(year, 1, 1) : null;
    }

    /// <summary>"Sapiens: A Brief History of Humankind" → "Sapiens": the title without its subtitle.</summary>
    private static string MainTitle(string title)
    {
        var cut = title.IndexOfAny([':', '(']);
        return cut > 2 ? title[..cut].Trim() : title;
    }

    /// <summary>"english" → "English".</summary>
    private static string? Language(string? value) =>
        value is { Length: > 1 } ? char.ToUpperInvariant(value[0]) + value[1..] : value;

    private static string Fold(string value) =>
        new([.. value.Normalize(NormalizationForm.FormC).ToLowerInvariant().Where(char.IsLetterOrDigit)]);
}
