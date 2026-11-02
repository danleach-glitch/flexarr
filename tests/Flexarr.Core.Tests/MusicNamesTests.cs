using Flexarr.Metadata;

namespace Flexarr.Core.Tests;

public class MusicNamesTests
{
    [Theory]
    [InlineData("The Beatles", "beatles")]
    [InlineData("Beatles!", "beatles")]
    [InlineData("Måneskin", "maneskin")]
    [InlineData("Simon & Garfunkel", "simon and garfunkel")]
    [InlineData("  AC/DC ", "ac dc")]
    [InlineData("The The", "the")]
    public void ArtistNames(string name, string expected) => Assert.Equal(expected, MusicNames.Normalise(name));

    [Theory]
    [InlineData("Rumours (Super Deluxe)", "rumours")]
    [InlineData("Rumours - 2004 Remaster", "rumours")]
    [InlineData("Rumours [Expanded Edition]", "rumours")]
    [InlineData("Live at Leeds (Live)", "live at leeds live")]
    [InlineData("(What's the Story) Morning Glory?", "what s the story morning glory")]
    [InlineData("moisturizer", "moisturizer")]
    [InlineData("(Deluxe)", "deluxe")]
    public void AlbumTitles(string title, string expected) => Assert.Equal(expected, MusicNames.NormaliseTitle(title));
}
