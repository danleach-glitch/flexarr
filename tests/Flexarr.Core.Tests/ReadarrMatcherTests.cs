using Flexarr.Core.Paths;
using Flexarr.Integrations.Arr.Models;
using Flexarr.Metadata;

namespace Flexarr.Core.Tests;

/// <summary>Which Readarr-compatible app has a book, when a household runs more than one (ebooks and audiobooks).</summary>
public sealed class ReadarrMatcherTests
{
    private static readonly ReadarrAuthor Osman = new() { Id = "3", AuthorName = "Richard Osman" };

    /// <summary>An ebook Readarr with the book as an ebook (its book 12), and an audiobook one with it as book 7.</summary>
    private static readonly ReadarrShelf Ebooks = new("Ebooks",
        [new ReadarrBook { Id = "12", AuthorId = "3", Title = "The Thursday Murder Club" }, new ReadarrBook { Id = "7", AuthorId = "3", Title = "Some Other Book" }],
        [new ReadarrBookFile { Id = "1", BookId = "12", Path = "/ebooks/Richard Osman/The Thursday Murder Club/book.epub" }],
        [Osman]);

    private static readonly ReadarrShelf Audiobooks = new("Audiobooks",
        [new ReadarrBook { Id = "7", AuthorId = "3", Title = "The Thursday Murder Club" }],
        [new ReadarrBookFile { Id = "2", BookId = "7", Path = "/audiobooks/Richard Osman/The Thursday Murder Club/book.m4b" }],
        [Osman]);

    private static readonly ReadarrShelf[] Both = [Ebooks, Audiobooks];

    private static BookToMatch Ours(string? arrId = null, string? instance = null, params string[] authors) =>
        new("/audiobooks/Richard Osman/The Thursday Murder Club", "The Thursday Murder Club", authors.Length > 0 ? authors : ["Richard Osman"], null, null, arrId, instance);

    private static (string Instance, string? Id)? Match(BookToMatch book, params ReadarrShelf[] shelves) =>
        ReadarrMatcher.Match(book, shelves, []) is { } hit ? (shelves[hit.Shelf].Instance, hit.Book.Id) : null;

    [Fact]
    public void AFileInOneApp_BeatsATitleInAnother()
    {
        Assert.Equal(("Audiobooks", "7"), Match(Ours(), Both));
    }

    [Fact]
    public void AKnownId_IsOnlyTakenFromItsOwnApp()
    {
        // Book 7 in the ebook app is another book altogether.
        Assert.Equal(("Audiobooks", "7"), Match(Ours("7", "Audiobooks"), Both));
        Assert.Equal(("Audiobooks", "7"), Match(Ours("7", "audiobooks"), Both));
        Assert.Equal(("Audiobooks", "7"), Match(Ours("7", instance: null), Both));
    }

    [Fact]
    public void AKnownIdItsAppNoLongerHas_IsMatchedAfresh()
    {
        Assert.Equal(("Audiobooks", "7"), Match(Ours("99", "Audiobooks"), Both));
    }

    [Fact]
    public void ABookWithNoAuthor_IsNeverMatchedByTitleAlone()
    {
        var elsewhere = new BookToMatch("/elsewhere/TMC", "The Thursday Murder Club", [], null, null, null);

        Assert.Null(Match(elsewhere, Both));
        Assert.Equal(("Ebooks", "12"), Match(elsewhere with { Authors = ["Richard Osman"] }, Both));
    }

    [Fact]
    public void FilesAreMatchedThroughPathMappings()
    {
        var hit = ReadarrMatcher.Match(Ours() with { Path = "/media/books/Richard Osman/The Thursday Murder Club" }, Both,
            [new PathMapping { From = "/audiobooks", To = "/media/books" }]);

        Assert.Equal(("Audiobooks", "7"), (Both[hit!.Value.Shelf].Instance, hit.Value.Book.Id));
    }

    [Fact]
    public void AFileInsideTheFolder_ButNotTheBooks_IsNoMatch()
    {
        // Author/ is One's folder (its file is the only one straight in it); Author/Two/ is another book.
        var shelf = new ReadarrShelf("Audiobooks",
            [new ReadarrBook { Id = "8", AuthorId = "3", Title = "Two" }, new ReadarrBook { Id = "9", AuthorId = "3", Title = "One" }],
            [new ReadarrBookFile { Id = "3", BookId = "8", Path = "/audiobooks/Richard Osman/Two/two.mp3" },
             new ReadarrBookFile { Id = "4", BookId = "9", Path = "/audiobooks/Richard Osman/One.m4b" }],
            [Osman]);
        var one = new BookToMatch("/audiobooks/Richard Osman", "One", ["Richard Osman"], null, null, null, Files: ["/audiobooks/Richard Osman/One.m4b"]);

        Assert.Equal(("Audiobooks", "9"), Match(one, shelf));
        Assert.Null(Match(one with { Title = "Three", Files = ["/audiobooks/Richard Osman/Three.m4b"] }, shelf));
    }
}
