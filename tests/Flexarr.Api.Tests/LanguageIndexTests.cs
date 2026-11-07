using System.Net;
using System.Security.Cryptography;
using System.Text;
using Flexarr.Api.Configuration;
using Flexarr.Api.Localization;
using Flexarr.Core.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Tests;

public sealed class LanguageIndexTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "flexarr-lang-" + Guid.NewGuid().ToString("N"));
    private readonly FakeSite _site = new();

    public void Dispose()
    {
        _site.Dispose();
        if (Directory.Exists(_data))
        {
            Directory.Delete(_data, recursive: true);
        }
    }

    private sealed class FakeSite : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Files { get; } = [];
        public List<string> Asked { get; } = [];
        public bool Down { get; set; }

        public TimeSpan Delay { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            lock (Asked)
            {
                Asked.Add(path);
            }

            if (Down)
            {
                throw new HttpRequestException("offline");
            }

            await Task.Delay(Delay, cancellationToken);
            return (Files.TryGetValue(path, out var bytes)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class Clients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StaticOptions(FlexarrOptions value) : IOptionsMonitor<FlexarrOptions>
    {
        public FlexarrOptions CurrentValue => value;
        public FlexarrOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<FlexarrOptions, string?> listener) => null;
    }

    private LanguageIndex Index(LanguageCatalog catalog, string url = "https://flexarr.io/languages/index.json") =>
        new(new Clients(_site), new StaticOptions(new FlexarrOptions { DataDirectory = _data, Languages = new() { IndexUrl = url } }),
            TimeProvider.System, NullLogger<LanguageIndex>.Instance) { Catalog = catalog };

    private static byte[] Pack(string code, long version, string play) => Encoding.UTF8.GetBytes(
        $$$"""{"format":1,"code":"{{{code}}}","name":"Deutsch","direction":"ltr","version":{{{version}}},"coverage":{},"messages":{"Play":"{{{play}}}"},"scopes":{"Play":"ws"}}""");

    private void Publish(string code, long version, string play, string? sha = null)
    {
        var pack = Pack(code, version, play);
        _site.Files[$"/languages/{code}.json"] = pack;
        _site.Files["/languages/index.json"] = Encoding.UTF8.GetBytes(
            $$$"""{"format":1,"languages":[{"code":"{{{code}}}","name":"Deutsch","direction":"ltr","version":{{{version}}},"sha256":"{{{sha ?? Convert.ToHexStringLower(SHA256.HashData(pack))}}}","size":{{{pack.Length}}},"coverage":{"web":1}}]}""");
    }

    [Fact]
    public async Task Downloads_a_pack_on_first_request_and_keeps_it()
    {
        Publish("de", 5, "Abspielen");
        var catalog = new LanguageCatalog();
        using var index = Index(catalog);

        var pack = await index.EnsureAsync("de-AT", CancellationToken.None);

        Assert.Equal("Abspielen", pack!.Messages["Play"]);
        Assert.True(File.Exists(Path.Combine(_data, "languages", "de.json")));

        // A restarted server, offline, still has it.
        _site.Down = true;
        var again = new LanguageCatalog();
        using var restarted = Index(again);
        restarted.LoadKept();
        Assert.Equal(5, again.Find("de")!.Version);
        Assert.Single(restarted.Entries);
    }

    [Fact]
    public async Task Refuses_a_pack_whose_hash_doesnt_match()
    {
        Publish("de", 5, "Abspielen", sha: new string('0', 64));
        var catalog = new LanguageCatalog();
        using var index = Index(catalog);

        Assert.Null(await index.EnsureAsync("de", CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(_data, "languages", "de.json")));
    }

    [Fact]
    public async Task A_pack_that_failed_isnt_downloaded_again_on_every_request()
    {
        Publish("de", 5, "Abspielen", sha: new string('0', 64));
        var catalog = new LanguageCatalog();
        using var index = Index(catalog);

        for (var i = 0; i < 5; i++)
        {
            Assert.Null(await index.EnsureAsync("de", CancellationToken.None));
        }

        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => index.EnsureAsync("de", CancellationToken.None)));
        Assert.Single(_site.Asked, "/languages/de.json");
    }

    [Fact]
    public async Task Requests_at_once_for_a_language_download_its_pack_once()
    {
        Publish("de", 5, "Abspielen");
        _site.Delay = TimeSpan.FromMilliseconds(200);
        var catalog = new LanguageCatalog();
        using var index = Index(catalog);
        await index.RefreshAsync(CancellationToken.None);

        var packs = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => index.EnsureAsync("de", CancellationToken.None)));

        Assert.All(packs, p => Assert.Equal("Abspielen", p!.Messages["Play"]));
        Assert.Single(_site.Asked, "/languages/de.json");
    }

    [Fact]
    public async Task Keeps_a_built_in_pack_that_is_newer()
    {
        Publish("de", 5, "Alt");
        var catalog = new LanguageCatalog();
        catalog.Install(LanguagePack.Read(Pack("de", 9, "Neu")));
        using var index = Index(catalog);

        Assert.Equal("Neu", (await index.EnsureAsync("de", CancellationToken.None))!.Messages["Play"]);
        Assert.DoesNotContain("/languages/de.json", _site.Asked);
    }

    [Fact]
    public async Task Updates_kept_packs_when_the_index_has_a_newer_one()
    {
        Publish("de", 5, "Alt");
        var catalog = new LanguageCatalog();
        using (var first = Index(catalog))
        {
            await first.EnsureAsync("de", CancellationToken.None);
        }

        Publish("de", 6, "Neu");
        File.SetLastWriteTimeUtc(Path.Combine(_data, "languages", "index.json"), DateTime.UtcNow.AddDays(-2));
        using var index = Index(catalog);
        index.LoadKept();
        await index.UpdateKeptAsync(CancellationToken.None);

        Assert.Equal("Neu", catalog.Find("de")!.Messages["Play"]);
    }

    [Fact]
    public async Task A_blank_index_address_downloads_nothing()
    {
        Publish("de", 5, "Abspielen");
        using var index = Index(new LanguageCatalog(), url: "");

        Assert.Null(await index.EnsureAsync("de", CancellationToken.None));
        Assert.Empty(_site.Asked);
    }
}
