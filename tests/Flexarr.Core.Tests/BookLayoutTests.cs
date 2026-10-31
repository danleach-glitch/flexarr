using Flexarr.Core.Books;

namespace Flexarr.Core.Tests;

/// <summary>Which book a file belongs to, and what its path says about it, for every layout the scanner accepts.</summary>
public sealed class BookLayoutTests
{
    private const string Root = "/books";

    private static BookPlace Locate(string relative, params string[] siblings) =>
        BookLayout.Locate(Root, $"{Root}/{relative}", _ => siblings.Length > 0 ? siblings : [Path.GetFileName(relative)]);

    [Fact]
    public void AFolderOfFilesIsOneBook()
    {
        var place = Locate("Alan Watts/Out of Your Mind/1-01 Introduction.mp3", "1-01 Introduction.mp3", "1-02 Our Image Of The World.mp3");

        Assert.Equal($"{Root}/Alan Watts/Out of Your Mind", place.Key);
        Assert.Equal("Out of Your Mind", place.Title);
        Assert.Equal(["Alan Watts"], place.Authors);
        Assert.Null(place.Series);
        Assert.Equal(1, place.Disc);
    }

    [Fact]
    public void DiscFoldersArePartsOfTheBookAbove()
    {
        var place = Locate("Neil Gaiman/American Gods/CD2/03.mp3", "03.mp3", "04.mp3");

        Assert.Equal($"{Root}/Neil Gaiman/American Gods", place.Key);
        Assert.Equal("American Gods", place.Title);
        Assert.Equal(2, place.Disc);
    }

    [Theory]
    [InlineData("Disc 1")]
    [InlineData("disk_3")]
    [InlineData("Part 2")]
    [InlineData("CD 10")]
    public void DiscFolderSpellings(string folder)
    {
        Assert.Equal($"{Root}/A/Book", Locate($"A/Book/{folder}/01.mp3", "01.mp3", "02.mp3").Key);
    }

    [Fact]
    public void AuthorSeriesNumberedTitle()
    {
        var place = Locate("Brandon Sanderson/Mistborn/1 - The Final Empire/a.m4b");

        Assert.Equal("The Final Empire", place.Title);
        Assert.Equal("Mistborn", place.Series);
        Assert.Equal("1", place.Sequence);
        Assert.Equal(["Brandon Sanderson"], place.Authors);
    }

    [Fact]
    public void VolumeYearTitleNarratorAndAsin()
    {
        var place = Locate("Terry Pratchett/Discworld/Vol. 1.5 - 2004 - Going Postal {Stephen Briggs} [B002V0QK4C]/a.m4b");

        Assert.Equal("Going Postal", place.Title);
        Assert.Equal("Discworld", place.Series);
        Assert.Equal("1.5", place.Sequence);
        Assert.Equal(2004, place.Year);
        Assert.Equal("Stephen Briggs", place.Narrator);
        Assert.Equal("B002V0QK4C", place.Asin);
    }

    [Fact]
    public void SingleFileBooksSideBySideAreSeparateBooks()
    {
        var place = Locate("Kurt Vonnegut/Cat's Cradle.m4b", "Cat's Cradle.m4b", "Timequake.m4b");

        Assert.Equal($"{Root}/Kurt Vonnegut/Cat's Cradle.m4b", place.Key);
        Assert.Equal("Cat's Cradle", place.Title);
        Assert.Equal(["Kurt Vonnegut"], place.Authors);
    }

    [Fact]
    public void AFileStraightInTheLibraryFolderIsABook()
    {
        var place = Locate("Andy Weir - Project Hail Mary.m4b");

        Assert.Equal($"{Root}/Andy Weir - Project Hail Mary.m4b", place.Key);
        Assert.Equal("Project Hail Mary", place.Title);
        Assert.Equal(["Andy Weir"], place.Authors);
    }

    [Fact]
    public void AuthorAndAsinInTheFileNameOfABookFolder()
    {
        // The Mini: Lee Child, Andrew Child/Exit Strategy/Lee Child, Andrew Child - Exit Strategy [B0DPXK6Y7B].m4b
        var place = BookLayout.Describe(Root, $"{Root}/Lee Child, Andrew Child/Exit Strategy - Lee Child [B0DPXK6Y7B].m4b", isFile: true);

        Assert.Equal(["Lee Child", "Andrew Child"], place.Authors);
        Assert.Equal("B0DPXK6Y7B", place.Asin);
    }

    [Fact]
    public void SeveralAuthorsInOneFolderName()
    {
        Assert.Equal(["Héctor García", "Francesc Miralles"], Locate("Héctor García, Francesc Miralles/Ikigai/Ikigai.m4b").Authors);
        Assert.Equal(["Terry Pratchett", "Neil Gaiman"], Locate("Terry Pratchett & Neil Gaiman/Good Omens/a.mp3").Authors);
    }

    [Fact]
    public void SurnameFirstIsOnePerson()
    {
        Assert.Equal(["Tolkien, J.R.R."], BookLayout.SplitAuthors("Tolkien, J.R.R."));
    }

    [Fact]
    public void ABookFolderInsideAnotherBooksFolder()
    {
        // The Mini: Gregory David Roberts/Shantaram/The Mountain Shadow/The Mountain Shadow Shantaram, Book 2.m4b
        var place = Locate("Gregory David Roberts/Shantaram/The Mountain Shadow/The Mountain Shadow Shantaram, Book 2.m4b");

        Assert.Equal($"{Root}/Gregory David Roberts/Shantaram/The Mountain Shadow", place.Key);
        Assert.Equal("The Mountain Shadow", place.Title);
        Assert.Equal("Shantaram", place.Series);
    }

    [Fact]
    public void TitleWithYearInBrackets()
    {
        var place = Locate("Frank Herbert/Dune (1965)/01.mp3", "01.mp3", "02.mp3");

        Assert.Equal("Dune", place.Title);
        Assert.Equal(1965, place.Year);
    }

    [Fact]
    public void TitlesThatStartWithANumberKeepIt()
    {
        Assert.Equal("1984", Locate("George Orwell/1984/a.mp3", "a.mp3", "b.mp3").Title);
        Assert.Equal("11-22-63", Locate("Stephen King/11-22-63/a.mp3", "a.mp3", "b.mp3").Title);
    }

    [Theory]
    [InlineData("1.5", 1.5)]
    [InlineData("0", 0.0)]
    [InlineData("1-3", 1.0)]
    [InlineData("2,5", 2.5)]
    [InlineData("Prequel", null)]
    public void SequenceSortsAsANumber(string sequence, double? order)
    {
        Assert.Equal(order, BookAssembler.SequenceOrder(sequence));
    }
}
