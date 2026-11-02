using Flexarr.Api.Books;
using Flexarr.Api.Endpoints;
using Flexarr.Api.Profiles;
using Flexarr.Core.Media;

namespace Flexarr.Api.Tests;

/// <summary>A title another profile can't see, as household now-playing shows it: nothing that could name it.</summary>
public class PrivateTitleTests
{
    [Fact]
    public void EveryOptionalDetail_IsCleared()
    {
        var full = new ItemSummaryDto(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), MediaKind.Audiobook, "Dracula", 1897, 1, 2, 3, new DateOnly(1897, 5, 26), 4, 3600,
            "1080p", DateTime.UtcNow, new ArtworkDto("p", "b", "l", "t"), "#123456", 7.5, 88, "15", 120, new DateOnly(1897, 5, 26),
            new UserDataDto(60, true, 2, DateTime.UtcNow, 1), "Series", 1, 2, Guid.NewGuid(), "Album", "Artist", HomeVideo: true,
            Book: new BookSummaryDto("Bram Stoker", 1, "Narrator", "Series", "1", 3600, null), ImdbRating: 7.9,
            RottenTomatoes: new RottenTomatoesDto(90, RottenTomatoesState.CertifiedFresh));

        var shown = full.Private();

        // Every property that can be empty is; a new field added to the DTO fails here until Private() clears it.
        var left = typeof(ItemSummaryDto).GetProperties()
            .Where(p => p.Name != nameof(ItemSummaryDto.Kind))
            .Where(p => Nullable.GetUnderlyingType(p.PropertyType) is not null || !p.PropertyType.IsValueType)
            .Where(p => p.Name is not (nameof(ItemSummaryDto.Title) or nameof(ItemSummaryDto.Artwork) or nameof(ItemSummaryDto.UserData)))
            .Where(p => p.GetValue(shown) is not null)
            .Select(p => p.Name).ToList();
        Assert.Empty(left);
        Assert.Equal(LibraryVisibility.PrivateTitle, shown.Title);
        Assert.False(shown.HomeVideo);
    }
}
