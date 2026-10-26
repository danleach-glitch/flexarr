using System.Globalization;
using System.Text;

namespace Flexarr.Core.LiveTv;

public enum RecordingState
{
    Scheduled,
    Recording,
    Completed,

    /// <summary>It started but didn't finish, or never started (the channel wouldn't play, no space).</summary>
    Failed,

    /// <summary>Taken off the schedule by someone; a series doesn't put it back.</summary>
    Cancelled,

    /// <summary>Not recorded because every stream was in use by the time it was due.</summary>
    Skipped,
}

/// <summary>
/// A series to record: every showing of a title (on one channel, or any), optionally only new episodes. Recordings
/// are the household's, not a profile's.
/// </summary>
public sealed class RecordingRule
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Title { get; set; }

    /// <summary>Only on this channel; null for any channel.</summary>
    public Guid? ChannelId { get; set; }

    public bool NewOnly { get; set; }

    /// <summary>How many recordings to keep, the oldest going first; 0 keeps them all.</summary>
    public int KeepLatest { get; set; }

    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One showing to record, being recorded, or recorded: what the guide said about it (kept, as the guide moves on and
/// the channel may go), and what became of it.
/// </summary>
public sealed class Recording
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>The series it was scheduled for; null when it was chosen on its own.</summary>
    public Guid? RuleId { get; set; }

    public Guid ChannelId { get; set; }
    public required string ChannelName { get; set; }
    public required string Title { get; set; }
    public string? Subtitle { get; set; }
    public string? Description { get; set; }
    public string? Category { get; set; }
    public int? Season { get; set; }
    public int? Episode { get; set; }
    public bool IsNew { get; set; }

    /// <summary>When the programme is on, as the guide had it (recording starts a little before and runs on after).</summary>
    public DateTime Start { get; set; }

    public DateTime End { get; set; }

    public RecordingState State { get; set; }

    /// <summary>Why it failed or was skipped, in words for the viewer.</summary>
    public string? Reason { get; set; }

    /// <summary>The finished file; while recording, where it will go.</summary>
    public string? FilePath { get; set; }

    public long? Size { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public bool IsActive => State is RecordingState.Scheduled or RecordingState.Recording;
}

/// <summary>A programme in the guide, with its channel, as the planner sees it.</summary>
public sealed record GuideShowing(LiveProgramme Programme, LiveChannel Channel);

/// <summary>
/// Turning series into scheduled recordings, and working out which recordings can't all happen with the streams
/// there are. Pure, so it's tested without a clock or a database.
/// </summary>
public static class DvrPlanner
{
    /// <summary>
    /// The recordings the series call for that aren't scheduled yet: each showing of the title (on the series'
    /// channel, if it has one) that hasn't finished, isn't already a recording (whatever became of it: one someone
    /// cancelled stays cancelled), and isn't an episode already recorded or scheduled. A repeat of an episode is
    /// recorded once, at its first showing.
    /// </summary>
    public static List<Recording> Expand(IEnumerable<RecordingRule> rules, IEnumerable<GuideShowing> guide, IEnumerable<Recording> existing, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(guide);
        ArgumentNullException.ThrowIfNull(existing);
        var known = existing.ToList();
        var taken = known.Select(r => (r.ChannelId, r.Start)).ToHashSet();
        var episodes = known.Where(r => r.State is not (RecordingState.Cancelled or RecordingState.Failed or RecordingState.Skipped))
            .Select(EpisodeKey).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var showings = guide.OrderBy(s => s.Programme.Start).ToList();
        var added = new List<Recording>();
        foreach (var rule in rules)
        {
            var title = Normalise(rule.Title);
            foreach (var (programme, channel) in showings)
            {
                if (programme.End <= now || Normalise(programme.Title) != title
                    || rule.ChannelId is { } only && only != channel.Id
                    || rule.NewOnly && !programme.IsNew
                    || taken.Contains((channel.Id, programme.Start)))
                {
                    continue;
                }

                var recording = From(programme, channel, rule.Id, rule.CreatedBy);
                if (EpisodeKey(recording) is { } key && !episodes.Add(key))
                {
                    continue;
                }

                taken.Add((channel.Id, programme.Start));
                added.Add(recording);
            }
        }

        return added;
    }

    /// <summary>A recording of this showing, as the guide describes it.</summary>
    public static Recording From(LiveProgramme programme, LiveChannel channel, Guid? ruleId, string? createdBy)
    {
        ArgumentNullException.ThrowIfNull(programme);
        ArgumentNullException.ThrowIfNull(channel);
        return new Recording
        {
            RuleId = ruleId,
            ChannelId = channel.Id,
            ChannelName = channel.Name,
            Title = programme.Title,
            Subtitle = programme.Subtitle,
            Description = programme.Description,
            Category = programme.Category,
            Season = programme.Season,
            Episode = programme.Episode,
            IsNew = programme.IsNew,
            Start = programme.Start,
            End = programme.End,
            State = RecordingState.Scheduled,
            CreatedBy = createdBy,
        };
    }

    /// <summary>
    /// The recordings that won't happen because, at some point while they're on, more channels would be wanted at
    /// once than there are streams. One already recording keeps its stream; otherwise the one scheduled first wins.
    /// Two recordings on the same channel share a stream. <paramref name="padding"/> widens each, as the recorder
    /// does.
    /// </summary>
    public static HashSet<Guid> Clashing(IEnumerable<Recording> recordings, int streams, TimeSpan before, TimeSpan after)
    {
        ArgumentNullException.ThrowIfNull(recordings);
        var active = recordings.Where(r => r.IsActive)
            .OrderByDescending(r => r.State == RecordingState.Recording).ThenBy(r => r.CreatedAt).ThenBy(r => r.Start).ToList();
        var kept = new List<Recording>();
        var clashing = new HashSet<Guid>();
        foreach (var recording in active)
        {
            var (start, end) = (recording.Start - before, recording.End + after);
            // At each moment this one is on, the channels the kept ones already need there, with this one's added.
            var overlapping = kept.Where(k => k.Start - before < end && k.End + after > start).ToList();
            var moments = overlapping.Select(k => k.Start - before).Append(start).Where(t => t >= start && t < end).Distinct();
            var fits = moments.All(moment => overlapping
                .Where(k => k.Start - before <= moment && k.End + after > moment)
                .Select(k => k.ChannelId).Append(recording.ChannelId).Distinct().Count() <= Math.Max(1, streams));
            if (fits)
            {
                kept.Add(recording);
            }
            else
            {
                clashing.Add(recording.Id);
            }
        }

        return clashing;
    }

    /// <summary>What makes two showings the same episode: its number, else its own title; null when the guide gives neither.</summary>
    public static string? EpisodeKey(Recording recording)
    {
        ArgumentNullException.ThrowIfNull(recording);
        var title = Normalise(recording.Title);
        if (recording.Season is { } season && recording.Episode is { } episode)
        {
            return $"{title}|s{season}e{episode}";
        }

        return string.IsNullOrWhiteSpace(recording.Subtitle) ? null : $"{title}|{Normalise(recording.Subtitle)}";
    }

    private static string Normalise(string text) => string.Join(' ', text.Trim().ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
}

/// <summary>Where a recording goes in the DVR folder: laid out as a TV library, so it's scanned like one.</summary>
public static class RecordingNames
{
    /// <summary>
    /// "Show/Season 2/Show - S02E05 - Episode.mkv" when the guide numbers it, else by the day it was on
    /// ("Show/Show - 2026-10-26 - Episode.mkv", with the time when the guide gives no episode title).
    /// </summary>
    public static string RelativePath(Recording recording, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(recording);
        ArgumentNullException.ThrowIfNull(zone);
        var show = Clean(recording.Title);
        var subtitle = string.IsNullOrWhiteSpace(recording.Subtitle) ? null : Clean(recording.Subtitle);
        if (recording.Season is { } season && recording.Episode is { } episode)
        {
            var name = $"{show} - S{season:00}E{episode:00}" + (subtitle is null ? "" : $" - {subtitle}");
            return Path.Combine(show, $"Season {season}", name + ".mkv");
        }

        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(recording.Start, DateTimeKind.Utc), zone);
        var day = local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var rest = subtitle ?? local.ToString("HH.mm", CultureInfo.InvariantCulture);
        return Path.Combine(show, $"{show} - {day} - {rest}.mkv");
    }

    /// <summary>A name that's safe as a file or folder on macOS, Linux and Windows shares.</summary>
    public static string Clean(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Trim())
        {
            builder.Append(c switch
            {
                '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' => '-',
                _ when char.IsControl(c) => ' ',
                _ => c,
            });
        }

        var cleaned = string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim('.', ' ');
        if (cleaned.Length > 120)
        {
            cleaned = cleaned[..120].TrimEnd('.', ' ');
        }

        return cleaned.Length == 0 ? "Recording" : cleaned;
    }
}
