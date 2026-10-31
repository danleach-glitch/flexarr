using System.Text.Json;
using System.Text.Json.Serialization;
using Flexarr.Integrations.Arr;
using Flexarr.Integrations.Arr.Models;

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
}
