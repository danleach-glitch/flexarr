using Flexarr.Api.LiveTv;

namespace Flexarr.Api.Tests;

public class LiveTvViewerLimitTests
{
    private static LiveViewer Viewer(string id, string profile, int minutesAgo) =>
        new(id, Guid.NewGuid(), "Channel", profile, null, null!) { StartedAt = DateTime.UtcNow.AddMinutes(-minutesAgo) };

    [Fact]
    public void A_profile_at_its_limit_makes_way_with_the_channel_it_started_longest_ago()
    {
        var viewers = new LiveTvViewers();
        for (var i = 0; i < LiveTvViewers.MaxPerProfile; i++)
        {
            viewers.Add(Viewer($"kid-{i}", "kid", minutesAgo: 10 + i));
        }

        viewers.Add(Viewer("owner-0", "owner", minutesAgo: 60));

        Assert.Equal(["kid-3"], viewers.MakingWayFor("kid").Select(v => v.Id));
        Assert.Empty(viewers.MakingWayFor("owner"));
        Assert.Empty(viewers.MakingWayFor("guest"));
    }

    [Fact]
    public void Under_the_limit_nothing_stops()
    {
        var viewers = new LiveTvViewers();
        for (var i = 0; i < LiveTvViewers.MaxPerProfile - 1; i++)
        {
            viewers.Add(Viewer($"kid-{i}", "kid", minutesAgo: i));
        }

        Assert.Empty(viewers.MakingWayFor("kid"));
    }
}
