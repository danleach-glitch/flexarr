using Flexarr.Core.Books;

namespace Flexarr.Core.Tests;

/// <summary>Genres that only name the format, dropped from books.</summary>
public sealed class BookGenresTests
{
    [Theory]
    [InlineData("Audiobook")]
    [InlineData("Audiobooks")]
    [InlineData("audio book")]
    [InlineData("Spoken Word")]
    [InlineData("Audiobook - Unabridged")]
    public void FormatWordsAreNotGenres(string name) => Assert.True(BookGenres.IsFormat(name));

    [Fact]
    public void KeepsRealGenresOnceEach() =>
        Assert.Equal(["Fantasy", "Science Fiction"], BookGenres.Clean(["Audiobook", " Fantasy ", "fantasy", "Audiobooks", "Science Fiction", ""]));
}
