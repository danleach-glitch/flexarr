using Flexarr.Core.Media;

namespace Flexarr.Core.Tests;

/// <summary>MusicBrainz's two-part release types, as Lidarr passes them on, boiled down to the one word the UI shelves by.</summary>
public sealed class AlbumKindTests
{
    [Theory]
    [InlineData("Album", new string[0], AlbumKind.Album)]
    [InlineData("EP", new string[0], AlbumKind.Ep)]
    [InlineData("Single", new string[0], AlbumKind.SingleRelease)]
    [InlineData("Broadcast", new string[0], AlbumKind.Other)]
    [InlineData("Other", new string[0], AlbumKind.Other)]
    [InlineData("album", new string[0], AlbumKind.Album)] // however it's spelt
    [InlineData(" Album ", new[] { "" }, AlbumKind.Album)]
    public void ThePrimaryType_WhenThatIsAllThereIs(string primary, string[] secondary, string expected) =>
        Assert.Equal(expected, AlbumKind.From(primary, secondary));

    [Theory]
    [InlineData("Album", new[] { "Live" }, AlbumKind.Live)]
    [InlineData("EP", new[] { "Live" }, AlbumKind.Live)] // a live EP is live before it's an EP
    [InlineData("Album", new[] { "Compilation" }, AlbumKind.Compilation)]
    [InlineData("Album", new[] { "Compilation", "Live" }, AlbumKind.Live)] // a live compilation is live
    [InlineData("Album", new[] { "Soundtrack" }, AlbumKind.Soundtrack)]
    [InlineData("Album", new[] { "Soundtrack", "Compilation" }, AlbumKind.Compilation)]
    [InlineData(null, new[] { "live" }, AlbumKind.Live)] // the secondary type settles it on its own
    public void ASecondaryType_Overrides_ThePrimary(string? primary, string[] secondary, string expected) =>
        Assert.Equal(expected, AlbumKind.From(primary, secondary));

    [Fact]
    public void ARemixAlbum_IsStillAnAlbum() => Assert.Equal(AlbumKind.Album, AlbumKind.From("Album", ["Remix"]));

    [Theory]
    [InlineData("Demo")]
    [InlineData("Mixtape/Street")]
    [InlineData("DJ-mix")]
    [InlineData("Interview")]
    [InlineData("Spokenword")]
    [InlineData("Audiobook")]
    public void DemosMixtapesAndTheLike_AreOther(string secondary) => Assert.Equal(AlbumKind.Other, AlbumKind.From("Album", [secondary]));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Boxset")] // not a MusicBrainz type: say nothing rather than guess
    public void Null_WhenNobodySays(string? primary)
    {
        Assert.Null(AlbumKind.From(primary, []));
        Assert.Null(AlbumKind.From(primary, null));
    }
}
