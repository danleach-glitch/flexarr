using System.Net;
using Flexarr.Core.Paths;
using Flexarr.Integrations.Arr;
using Flexarr.Metadata;
using Flexarr.Metadata.Books;

namespace Flexarr.Core.Tests;

/// <summary>Authors looked up on Audible (through Audnexus) and Open Library, from canned answers.</summary>
public sealed class AuthorProviderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("flexarr-authors-").FullName;
    private readonly Canned _web = new();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private BookProviders Providers() =>
        new(new HttpClient(_web), () => new Settings(_dir), new BookProviderThrottle());

    [Fact]
    public async Task Audible_TakesOnlyAnExactName_InitialsSpacedOrNot()
    {
        _web.Answers[("api.audnex.us", "/authors")] = """
            [{"asin": "B000TOLK01", "name": "J. R. R. Tolkien"}, {"asin": "B000ESTATE", "name": "J.R.R. Tolkien Estate"},
             {"asin": "B000CHRIS1", "name": "Christopher Tolkien"}]
            """;

        Assert.Equal("B000TOLK01", await Providers().FindAudibleAuthorAsync("J.R.R. Tolkien", [], default));
        Assert.Null(await Providers().FindAudibleAuthorAsync("Tolkien", [], default));
    }

    [Fact]
    public async Task Audible_OfSeveralNamesakes_TakesOnlyOneTheBookAlreadyLists()
    {
        _web.Answers[("api.audnex.us", "/authors")] = """
            [{"asin": "B000OSMAN1", "name": "Richard Osman"}, {"asin": "B074KL81KM", "name": "Richard Osman"}]
            """;

        Assert.Equal("B074KL81KM", await Providers().FindAudibleAuthorAsync("Richard Osman", ["b074kl81km"], default));
        Assert.Null(await Providers().FindAudibleAuthorAsync("Richard Osman", [], default)); // either could be the comedian
        Assert.Null(await Providers().FindAudibleAuthorAsync("Richard Osman", ["B000NOBODY"], default));
    }

    [Fact]
    public async Task Audible_ThatsDown_Throws_SoTheAuthorIsTriedAgain()
    {
        _web.Down = true;

        await Assert.ThrowsAsync<HttpRequestException>(() => Providers().AuthorAsync("B074KL81KM", default));
    }

    [Fact]
    public async Task OpenLibrary_SkipsANamesakeWithoutOurBooks()
    {
        _web.Answers[("openlibrary.org", "/search/authors.json")] = """
            {"numFound": 2, "docs": [
              {"key": "OL1A", "name": "Andy Weir", "top_work": "Weir Tales"},
              {"key": "OL2A", "name": "Andy Weir", "top_work": "The Martian"}]}
            """;
        _web.Answers[("openlibrary.org", "/authors/OL1A/works.json")] = """{"entries": [{"title": "Gardening for Beginners"}]}""";
        _web.Answers[("openlibrary.org", "/authors/OL2A/works.json")] = """{"entries": [{"title": "The Martian"}, {"title": "Project Hail Mary: A Novel"}]}""";
        _web.Answers[("openlibrary.org", "/authors/OL2A.json")] = """
            {"key": "/authors/OL2A", "name": "Andy Weir", "bio": {"type": "/type/text", "value": "Andy Weir was a software engineer."}, "photos": [-1, 9876]}
            """;

        var found = await Providers().OpenLibraryAuthorAsync("Andy Weir", ["Project Hail Mary"], default);

        Assert.Equal(("Andy Weir was a software engineer.", "https://covers.openlibrary.org/a/olid/OL2A-L.jpg?default=false"), (found?.Biography, found?.Image));
        Assert.DoesNotContain(_web.Requests, r => r.Contains("/authors/OL1A.json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OpenLibrary_WithoutAPhoto_GivesNone()
    {
        _web.Answers[("openlibrary.org", "/search/authors.json")] = """
            {"numFound": 1, "docs": [{"key": "OL3A", "name": "Ann Leckie", "top_work": "Ancillary Justice"}]}
            """;
        _web.Answers[("openlibrary.org", "/authors/OL3A.json")] = """{"name": "Ann Leckie", "bio": "Ann Leckie writes science fiction.", "photos": [-1]}""";

        var found = await Providers().OpenLibraryAuthorAsync("Ann Leckie", ["Ancillary Justice (Imperial Radch, Book 1)"], default);

        Assert.Equal(("Ann Leckie writes science fiction.", null), (found?.Biography, found?.Image));
        Assert.DoesNotContain(_web.Requests, r => r.Contains("/works.json", StringComparison.Ordinal)); // the top work was enough
    }

    [Fact]
    public async Task OpenLibrary_NobodyOfThatName_IsNobody()
    {
        _web.Answers[("openlibrary.org", "/search/authors.json")] = """{"numFound": 1, "docs": [{"key": "OL4A", "name": "Ann Leckey", "top_work": "Ancillary Justice"}]}""";

        Assert.Null(await Providers().OpenLibraryAuthorAsync("Ann Leckie", ["Ancillary Justice"], default));
    }

    private sealed class Settings(string cache) : IMetadataSettings
    {
        public IReadOnlyList<ArrInstance> ArrInstances { get; } = [];
        public IReadOnlyList<PathMapping> PathMappings { get; } = [];
        public string CacheDirectory => cache;
        public string FfmpegPath => "ffmpeg";
    }

    /// <summary>The providers, answering what a test gave them (by host and path, whatever the query); anything else is a 404.</summary>
    internal sealed class Canned : HttpMessageHandler
    {
        /// <summary>Every host answers 503.</summary>
        public bool Down;

        /// <summary>Hosts that answer 503.</summary>
        public HashSet<string> DownHosts { get; } = [];

        public Dictionary<(string Host, string Path), string> Answers { get; } = [];
        public System.Collections.Concurrent.ConcurrentQueue<string> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!;
            Requests.Enqueue(url.ToString());
            if (Down || DownHosts.Contains(url.Host))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }

            return Task.FromResult(Answers.TryGetValue((url.Host, url.AbsolutePath), out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
