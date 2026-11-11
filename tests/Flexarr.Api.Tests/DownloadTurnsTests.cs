using Flexarr.Api.Downloads;

namespace Flexarr.Api.Tests;

/// <summary>Whose download is converted next: turns by profile, then by show, rather than oldest first.</summary>
public sealed class DownloadTurnsTests
{
    private static readonly DateTime Start = new(2026, 11, 1, 20, 0, 0, DateTimeKind.Utc);
    private static readonly Guid BigShow = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid OtherShow = Guid.Parse("00000000-0000-0000-0000-0000000000a2");

    private static DownloadTurns.Waiting Episode(string profile, Guid show, int minute) =>
        new(Guid.Parse($"00000000-0000-0000-0000-{minute:D12}"), profile, show, Start.AddMinutes(minute));

    private static DownloadTurns.Waiting Film(string profile, int minute)
    {
        var id = Guid.Parse($"00000000-0000-0000-0000-{minute:D12}");
        return new(id, profile, id, Start.AddMinutes(minute));
    }

    /// <summary>Runs the queue to the end, as the preparer would, one download at a time.</summary>
    private static List<Guid> Order(List<DownloadTurns.Waiting> queued)
    {
        var turns = new DownloadTurns();
        var order = new List<Guid>();
        while (turns.Next(queued) is { } next)
        {
            order.Add(next);
            queued.RemoveAll(d => d.Id == next);
        }

        return order;
    }

    [Fact]
    public void Nothing_Queued_IsNull() => Assert.Null(new DownloadTurns().Next([]));

    [Fact]
    public void OneProfile_OneShow_GoesOldestFirst()
    {
        var season = Enumerable.Range(1, 5).Select(m => Episode("kid", BigShow, m)).ToList();
        Assert.Equal(season.Select(e => e.Id), Order([.. season.AsEnumerable().Reverse()]));
    }

    [Fact]
    public void AnotherProfilesFilm_GoesNext_NotAfterAWholeShow()
    {
        var season = Enumerable.Range(1, 10).Select(m => Episode("kid", BigShow, m)).ToList();
        var film = Film("parent", 30);

        var order = Order([.. season, film]);

        Assert.Equal(season[0].Id, order[0]); // already oldest
        Assert.Equal(film.Id, order[1]);
    }

    [Fact]
    public void Profiles_TakeTurns()
    {
        var a = Enumerable.Range(1, 3).Select(m => Episode("a", BigShow, m)).ToList();
        var b = Enumerable.Range(10, 3).Select(m => Episode("b", OtherShow, m)).ToList();

        Assert.Equal([a[0].Id, b[0].Id, a[1].Id, b[1].Id, a[2].Id, b[2].Id], Order([.. a, .. b]));
    }

    [Fact]
    public void AFilmQueuedAfterTheSameProfilesSeason_GoesBetweenItsEpisodes()
    {
        var season = Enumerable.Range(1, 4).Select(m => Episode("me", BigShow, m)).ToList();
        var film = Film("me", 30);

        var order = Order([.. season, film]);

        Assert.Equal([season[0].Id, film.Id, season[1].Id, season[2].Id, season[3].Id], order);
    }

    [Fact]
    public void AProfileThatComesBack_GoesToTheFront()
    {
        var turns = new DownloadTurns();
        var a = Enumerable.Range(1, 3).Select(m => Episode("a", BigShow, m)).ToList();
        var b = Film("b", 20);
        var queued = new List<DownloadTurns.Waiting>(a) { b };

        Assert.Equal(a[0].Id, turns.Next(queued));
        queued.Remove(a[0]);
        Assert.Equal(b.Id, turns.Next(queued));
        queued.Remove(b);
        Assert.Equal(a[1].Id, turns.Next(queued));
        queued.Remove(a[1]);

        // B asks for another film later: it was served last time round, but has had nothing waiting since.
        var again = Film("b", 40);
        queued.Add(again);
        Assert.Equal(again.Id, turns.Next(queued));
    }

    [Fact]
    public void ADownloadThatGaveWay_KeepsItsPlace_AmongItsOwnProfiles()
    {
        // Picked, then put back (someone started watching): it's still its profile's oldest, so it's next for that profile.
        var turns = new DownloadTurns();
        var a = Enumerable.Range(1, 2).Select(m => Episode("a", BigShow, m)).ToList();
        Assert.Equal(a[0].Id, turns.Next(a));
        Assert.Equal(a[0].Id, turns.Next(a));
    }
}
