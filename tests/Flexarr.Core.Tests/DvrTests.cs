using Flexarr.Core.LiveTv;

namespace Flexarr.Core.Tests;

public sealed class DvrPlannerTests
{
    private static readonly DateTime Day = new(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc);
    private static readonly LiveChannel Bbc = new() { Name = "BBC One", Number = 101 };
    private static readonly LiveChannel Itv = new() { Name = "ITV1", Number = 105 };
    private static readonly TimeSpan Before = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan After = TimeSpan.FromMinutes(5);

    private static GuideShowing On(LiveChannel channel, string title, double start, double end, int? season = null, int? episode = null,
        string? subtitle = null, bool isNew = false) =>
        new(new LiveProgramme
        {
            ChannelId = channel.Id, Title = title, Start = Day.AddHours(start), End = Day.AddHours(end),
            Season = season, Episode = episode, Subtitle = subtitle, IsNew = isNew,
        }, channel);

    [Fact]
    public void A_series_records_each_showing_once_per_episode()
    {
        var rule = new RecordingRule { Title = "Bargain Hunt" };
        var guide = new[]
        {
            On(Bbc, "Bargain Hunt", 12, 12.75, 70, 12),
            On(Bbc, "Bargain Hunt", 13, 13.75, 70, 13),
            // A repeat of episode 12: recorded once, at its first showing.
            On(Itv, "Bargain Hunt", 20, 20.75, 70, 12),
            On(Bbc, "Homes Under the Hammer", 10, 11),
        };

        var planned = DvrPlanner.Expand([rule], guide, [], Day.AddHours(9));

        Assert.Equal([12, 13], planned.Select(r => r.Episode ?? 0));
        Assert.All(planned, r => Assert.Equal(rule.Id, r.RuleId));
        Assert.All(planned, r => Assert.Equal(RecordingState.Scheduled, r.State));
    }

    [Fact]
    public void A_series_keeps_to_its_channel_and_new_episodes_when_asked()
    {
        var rule = new RecordingRule { Title = "bargain  hunt", ChannelId = Bbc.Id, NewOnly = true };
        var guide = new[]
        {
            On(Bbc, "Bargain Hunt", 12, 12.75, subtitle: "Bath", isNew: true),
            On(Bbc, "Bargain Hunt", 13, 13.75, subtitle: "Ripley"),
            On(Itv, "Bargain Hunt", 14, 14.75, subtitle: "Newark", isNew: true),
        };

        var planned = DvrPlanner.Expand([rule], guide, [], Day.AddHours(9));

        Assert.Equal("Bath", Assert.Single(planned).Subtitle);
    }

    [Fact]
    public void A_series_leaves_alone_what_is_already_on_the_list_or_over()
    {
        var rule = new RecordingRule { Title = "The News" };
        var guide = new[] { On(Bbc, "The News", 8, 8.5), On(Bbc, "The News", 18, 18.5), On(Bbc, "The News", 22, 22.5) };
        // Someone took 18:00 off the list: it stays off.
        var cancelled = DvrPlanner.From(guide[1].Programme, Bbc, rule.Id, null);
        cancelled.State = RecordingState.Cancelled;

        var planned = DvrPlanner.Expand([rule], guide, [cancelled], Day.AddHours(9));

        // 08:00 is over by 09:00; no episode numbers or titles, so every other showing is its own.
        Assert.Equal(Day.AddHours(22), Assert.Single(planned).Start);
    }

    [Fact]
    public void An_episode_already_recorded_isnt_recorded_again()
    {
        var rule = new RecordingRule { Title = "Bargain Hunt" };
        var done = DvrPlanner.From(On(Bbc, "Bargain Hunt", -24, -23, 70, 12).Programme, Bbc, rule.Id, null);
        done.State = RecordingState.Completed;

        Assert.Empty(DvrPlanner.Expand([rule], [On(Bbc, "Bargain Hunt", 12, 12.75, 70, 12)], [done], Day));
    }

    [Fact]
    public void With_one_stream_the_first_scheduled_wins_a_clash()
    {
        var first = Scheduled(Bbc, 20, 21, created: 1);
        var second = Scheduled(Itv, 20.5, 21.5, created: 2);
        // The same channel shares the stream.
        var sameChannel = Scheduled(Bbc, 20.5, 21, created: 3);

        var clashing = DvrPlanner.Clashing([second, first, sameChannel], streams: 1, Before, After);

        Assert.Equal([second.Id], clashing);
    }

    [Fact]
    public void One_recording_now_keeps_its_stream()
    {
        var later = Scheduled(Bbc, 20, 21, created: 1);
        var now = Scheduled(Itv, 19.5, 20.5, created: 2);
        now.State = RecordingState.Recording;

        Assert.Equal([later.Id], DvrPlanner.Clashing([later, now], streams: 1, Before, After));
    }

    [Fact]
    public void More_streams_let_recordings_run_side_by_side()
    {
        var a = Scheduled(Bbc, 20, 21, created: 1);
        var b = Scheduled(Itv, 20, 21, created: 2);
        var c = Scheduled(new LiveChannel { Name = "Channel 4" }, 20.5, 21, created: 3);

        Assert.Empty(DvrPlanner.Clashing([a, b], streams: 2, Before, After));
        Assert.Equal([c.Id], DvrPlanner.Clashing([a, b, c], streams: 2, Before, After));
    }

    [Fact]
    public void The_padding_counts_back_to_back_on_different_channels_clash()
    {
        // 20:00–21:00 runs on to 21:05, into the 21:00 on another channel.
        var a = Scheduled(Bbc, 20, 21, created: 1);
        var b = Scheduled(Itv, 21, 22, created: 2);

        Assert.Equal([b.Id], DvrPlanner.Clashing([a, b], streams: 1, Before, After));
        Assert.Empty(DvrPlanner.Clashing([a, b], streams: 1, TimeSpan.Zero, TimeSpan.Zero));
    }

    private static Recording Scheduled(LiveChannel channel, double start, double end, int created)
    {
        var recording = DvrPlanner.From(On(channel, "Programme", start, end).Programme, channel, null, null);
        recording.CreatedAt = Day.AddMinutes(created);
        return recording;
    }
}

public sealed class RecordingNamesTests
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    [Fact]
    public void A_numbered_episode_goes_in_its_season()
    {
        var recording = new Recording
        {
            ChannelName = "BBC One", Title = "Bargain Hunt", Subtitle = "Bath 12", Season = 70, Episode = 5,
            Start = new DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc),
        };

        Assert.Equal(Path.Combine("Bargain Hunt", "Season 70", "Bargain Hunt - S70E05 - Bath 12.mkv"), RecordingNames.RelativePath(recording, London));
    }

    [Fact]
    public void Otherwise_it_goes_by_the_day_it_was_on()
    {
        var recording = new Recording
        {
            ChannelName = "BBC One", Title = "The News: Tonight?", Start = new DateTime(2026, 7, 1, 21, 30, 0, DateTimeKind.Utc),
        };

        // 22:30 in London (summer time); the colon and question mark aren't safe in a file name.
        Assert.Equal(Path.Combine("The News- Tonight-", "The News- Tonight- - 2026-07-01 - 22.30.mkv"), RecordingNames.RelativePath(recording, London));
    }
}
