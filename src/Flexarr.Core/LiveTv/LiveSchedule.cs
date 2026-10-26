namespace Flexarr.Core.LiveTv;

/// <summary>
/// One schedule per channel, however the guide gave it. Guides can carry a channel's schedule twice: in October 2026
/// the provider's had BBC One and ITV2 twice, one copy an hour early (worked out before the clocks went back). A
/// programme that clashes with another and is on again under the same title an hour later is that stale copy, and is
/// dropped. Then, of anything still overlapping by more than a few minutes, the first is kept; neighbours that overlap
/// by a minute or two are trimmed to meet.
/// </summary>
public static class LiveSchedule
{
    /// <summary>Overlaps up to this long are trimmed; anything longer is a second schedule.</summary>
    public static readonly TimeSpan Slack = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan Shift = TimeSpan.FromHours(1);

    public static List<LiveProgramme> Tidy(IEnumerable<LiveProgramme> programmes)
    {
        var tidy = new List<LiveProgramme>();
        foreach (var channel in programmes.GroupBy(p => p.ChannelId))
        {
            var all = channel.Where(p => p.End > p.Start).OrderBy(p => p.Start).ThenByDescending(p => p.End).ToList();
            var starts = all.Select(p => (p.Title, p.Start)).ToHashSet();
            bool Clashes(LiveProgramme p) =>
                all.Any(q => !ReferenceEquals(q, p) && Overlap(p, q) > Slack);
            bool StaleCopy(LiveProgramme p) => starts.Contains((p.Title, p.Start + Shift)) && Clashes(p);
            var stale = all.Where(StaleCopy).ToHashSet();

            LiveProgramme? last = null;
            foreach (var programme in all.Where(p => !stale.Contains(p)))
            {
                if (last is not null && programme.Start < last.End)
                {
                    if (last.End - programme.Start > Slack || programme.End <= last.End)
                    {
                        continue;
                    }

                    programme.Start = last.End;
                }

                tidy.Add(programme);
                last = programme;
            }
        }

        return tidy;
    }

    private static TimeSpan Overlap(LiveProgramme a, LiveProgramme b)
    {
        var start = a.Start > b.Start ? a.Start : b.Start;
        var end = a.End < b.End ? a.End : b.End;
        return end > start ? end - start : TimeSpan.Zero;
    }
}
