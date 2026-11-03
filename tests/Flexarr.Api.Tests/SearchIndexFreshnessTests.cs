using Flexarr.Api.Search;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

/// <summary>
/// Run alone: <see cref="CatalogVersion"/> is shared by the whole process, so other tests' saves would make the index
/// look again and the counts here would wobble.
/// </summary>
[CollectionDefinition(nameof(SearchIndexFreshnessTests), DisableParallelization = true)]
public sealed class SearchIndexFreshnessCollection;

/// <summary>The typo index asks the database whether it's stale once, not on every keystroke search.</summary>
[Collection(nameof(SearchIndexFreshnessTests))]
public sealed class SearchIndexFreshnessTests(FlexarrApiFactory factory) : IClassFixture<FlexarrApiFactory>
{
    private sealed class ManualClock : TimeProvider
    {
        public long Ticks { get; set; }

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Ticks;
    }

    [Fact]
    public async Task Searches_ReuseTheStamp_UntilASaveOrAWhileHasPassed()
    {
        var clock = new ManualClock();
        using var index = new SearchIndex(clock);
        var word = $"zq{Guid.NewGuid().ToString("N")[..8]}";
        var first = await SeedAsync($"Breaking{word} Saga");

        // A search asks for each group, and keystrokes are separate requests: one look at the database between them.
        for (var keystroke = 0; keystroke < 3; keystroke++)
        {
            foreach (var group in Enum.GetValues<SearchIndex.Group>())
            {
                await FindAsync(index, $"braeking{word}", group);
            }
        }

        Assert.Contains(first, await FindAsync(index, $"braeking{word}", SearchIndex.Group.Title));
        Assert.Equal(1, index.StampChecks);

        // A save is seen at once.
        var second = await SeedAsync($"Shoegazing{word} Days");
        Assert.Contains(second, await FindAsync(index, $"shoegazng{word}", SearchIndex.Group.Title));
        Assert.Equal(2, index.StampChecks);

        // A bulk update skips the change tracker, so it's seen once the stamp is old.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            await db.MediaItems.Where(i => i.Id == second).ExecuteUpdateAsync(u => u
                .SetProperty(i => i.Title, $"Renamed{word} Days").SetProperty(i => i.MetadataUpdatedAt, DateTime.UtcNow.AddYears(5)));
        }

        Assert.DoesNotContain(second, await FindAsync(index, $"renamd{word}", SearchIndex.Group.Title));
        Assert.Equal(2, index.StampChecks);

        clock.Ticks += (SearchIndex.Fresh + TimeSpan.FromSeconds(1)).Ticks;
        Assert.Contains(second, await FindAsync(index, $"renamd{word}", SearchIndex.Group.Title));
        Assert.Equal(3, index.StampChecks);
    }

    private async Task<List<Guid>> FindAsync(SearchIndex index, string text, SearchIndex.Group group)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        return [.. (await index.FindAsync(db, text, group, 10, new HashSet<Guid>(), CancellationToken.None)).Select(m => m.Id)];
    }

    private async Task<Guid> SeedAsync(string title)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var library = new Library { Name = $"Fresh {Guid.NewGuid():N}", Type = LibraryType.Movies };
        var item = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Movie, Title = title, SortTitle = title.ToLowerInvariant() };
        db.AddRange(library, item);
        await db.SaveChangesAsync();
        return item.Id;
    }
}
