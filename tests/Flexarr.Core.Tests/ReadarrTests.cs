using System.Text.Json;
using System.Text.Json.Serialization;
using Flexarr.Integrations.Arr;
using Flexarr.Integrations.Arr.Models;
using Flexarr.Integrations.Arr.Webhooks;

namespace Flexarr.Core.Tests;

/// <summary>The Readarr family (Readarr, Bookshelf, Chaptarr): one connection kind, told apart, read tolerantly.</summary>
public sealed class ReadarrTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Readarr", name));

    [Fact]
    public void Bookshelf_CallsItselfReadarr_ButItsPackageGivesItAway()
    {
        var status = JsonSerializer.Deserialize<ArrSystemStatus>(Fixture("status-bookshelf.json"), Json)!;

        Assert.Equal("Readarr", status.AppName);
        Assert.Equal(ReadarrFlavour.Bookshelf, ReadarrFlavours.Of(status));
        Assert.Equal(ReadarrFlavour.Chaptarr, ReadarrFlavours.Of(new ArrSystemStatus("Chaptarr", "0.9", null)));
        Assert.Equal(ReadarrFlavour.Readarr, ReadarrFlavours.Of(new ArrSystemStatus("Readarr", "0.4.18", null, "Team Readarr")));
    }

    [Fact]
    public void TheReadarrFamily_TalksApiV1()
    {
        var instance = new ArrInstance { Name = "Bookshelf", Kind = ArrKind.Readarr, BaseUrl = new Uri("http://bookshelf:8787"), ApiKey = "k" };

        Assert.Equal("v1", instance.ApiVersion);
    }

    [Fact]
    public void Ids_AreKeptAsText_NumbersOrChaptarrsPrefixedOnes()
    {
        const string books = """
            [{"id": 12, "authorId": 3, "title": "The Thursday Murder Club", "foreignBookId": "hc:428812", "unknownField": {"a": 1},
              "editions": [{"foreignEditionId": 54825869, "asin": "B07QL7C4NL", "monitored": true, "format": "Audiobook"},
                           {"foreignEditionId": "gr:1", "isEbook": true}]}]
            """;

        var book = Assert.Single(JsonSerializer.Deserialize<List<ReadarrBook>>(books, Json)!);

        Assert.Equal(("12", "3", "hc:428812"), (book.Id, book.AuthorId, book.ForeignBookId));
        Assert.Equal(("54825869", "B07QL7C4NL"), (book.Edition!.ForeignEditionId, book.Edition.Asin));
    }

    private const string Book = "/data/audiobooks/Richard Osman/A Thursday Murder Club Mystery/The Thursday Murder Club";

    [Theory]
    [InlineData("webhook-download.json", "Readarr Download", new[] { Book })]
    [InlineData("webhook-retag.json", "Readarr Retag", new[] { Book })]
    [InlineData("webhook-bookfiledelete.json", "Readarr BookFileDelete", new[] { Book })]
    [InlineData("webhook-rename.json", "Readarr Rename", new[] { Book, "/data/audiobooks/Richard Osman/The Thursday Murder Club" })]
    [InlineData("webhook-bookdelete.json", "Readarr BookDelete", new[] { "/data/audiobooks/Richard Osman" })]
    [InlineData("webhook-authoradded.json", "Readarr AuthorAdded", new[] { "/data/audiobooks/Richard Osman" })]
    [InlineData("webhook-authordelete.json", "Readarr AuthorDelete", new[] { "/data/audiobooks/Richard Osman" })]
    public void Webhooks_RescanTheBookOrTheAuthor(string fixture, string reason, string[] paths)
    {
        var payload = JsonSerializer.Deserialize<ArrWebhookPayload>(Fixture(fixture), Json)!;

        var requests = ArrWebhookTranslator.ToScanRequests(ArrKind.Readarr, payload);

        Assert.Equal(paths, requests.Select(r => r.Path));
        Assert.All(requests, r => Assert.Equal(reason, r.Reason));
        Assert.Equal(fixture.Contains("delete", StringComparison.Ordinal), requests[0].Deleted);
    }

    [Theory]
    [InlineData("webhook-test.json")]
    [InlineData("webhook-health.json")]
    public void TestAndHealth_ScanNothing(string fixture)
    {
        var payload = JsonSerializer.Deserialize<ArrWebhookPayload>(Fixture(fixture), Json)!;

        Assert.Empty(ArrWebhookTranslator.ToScanRequests(ArrKind.Readarr, payload));
        Assert.Equal(fixture == "webhook-test.json", ArrWebhookTranslator.IsTest(payload));
    }

    [Fact]
    public void Chaptarr_DownloadWithAnEbook_RescansBothFolders_TheLibraryOnlyHasOne()
    {
        var payload = JsonSerializer.Deserialize<ArrWebhookPayload>(Fixture("webhook-download-chaptarr.json"), Json)!;

        var requests = ArrWebhookTranslator.ToScanRequests(ArrKind.Readarr, payload);

        // The ebook's folder isn't in any audiobooks library, so its scan finds nothing to do.
        Assert.Equal([Book, "/data/books/Richard Osman/The Thursday Murder Club"], requests.Select(r => r.Path));
    }
}
