using System.ComponentModel;
using Flexarr.Api.Authentication;
using Flexarr.Api.Devices;
using Flexarr.Api.LiveTv;
using Flexarr.Api.Profiles;
using Flexarr.Core.Data;
using Flexarr.Core.LiveTv;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace Flexarr.Api.Mcp;

/// <param name="Recording">"scheduled" or "recording" when the DVR has it; otherwise null.</param>
public sealed record McpProgramme(long Id, string Title, string? Episode, DateTime Start, DateTime End, string? Recording);

public sealed record McpChannel(Guid Id, double Number, string Name, string? Group, bool IsFavourite, McpProgramme? Now, McpProgramme? Next);

public sealed record McpRecording(Guid Id, string Title, string Channel, DateTime Start, DateTime End, string State, string? Reason, bool Clashes);

public sealed record McpRecordings(IReadOnlyList<McpRecording> Upcoming, IReadOnlyList<McpRecording> Recorded, IReadOnlyList<string> Series, int Streams);

/// <summary>Live TV and the DVR for an assistant: what's on, put a channel on a device, and record.</summary>
[McpServerToolType]
public static class LiveTvTools
{
    [McpServerTool(Name = "live_channels", ReadOnly = true), Description("The Live TV channels with what's on now and next on each (and whether the DVR is recording either). Narrow it with words from the channel's name or a programme's title. Use a channel's id with watch_channel_on_device, and a programme's id with record_programme.")]
    public static async Task<IReadOnlyList<McpChannel>> ChannelsAsync(FlexarrDbContext db, CurrentProfile profile, TimeProvider clock, CancellationToken ct,
        [Description("Words from a channel's name (\"bbc one\") or a programme's title; empty for every channel")] string? query = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(profile);
        var now = clock.GetUtcNow().UtcDateTime;
        var channels = await db.LiveChannels.AsNoTracking().OrderBy(c => c.Number).ThenBy(c => c.Name).ToListAsync(ct);
        var favourites = (await db.LiveFavourites.AsNoTracking().Where(f => f.ProfileId == profile.Id).Select(f => f.ChannelId).ToListAsync(ct)).ToHashSet();
        var upcoming = (await db.LiveProgrammes.AsNoTracking().Where(p => p.End > now && p.Start < now.AddHours(12)).OrderBy(p => p.Start).ToListAsync(ct))
            .ToLookup(p => p.ChannelId);
        var marks = await DvrEndpoints.Marks.LoadAsync(db, now, now.AddHours(12), ct);
        McpProgramme? Of(LiveProgramme? p) => p is null ? null
            : new McpProgramme(p.Id, p.Title, p.Season is { } s && p.Episode is { } e ? $"S{s} E{e}" + (p.Subtitle is { } t ? $" {t}" : "") : p.Subtitle,
                p.Start, p.End, marks.StateOf(p));
        var words = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return [.. channels.Select(c =>
            {
                var list = upcoming[c.Id].ToList();
                return new McpChannel(c.Id, c.Number, c.Name, c.Group, favourites.Contains(c.Id), Of(list.FirstOrDefault(p => p.Start <= now)),
                    Of(list.FirstOrDefault(p => p.Start > now)));
            })
            .Where(c => words.All(w => c.Name.Contains(w, StringComparison.OrdinalIgnoreCase)
                || (c.Now?.Title.Contains(w, StringComparison.OrdinalIgnoreCase) ?? false)
                || (c.Next?.Title.Contains(w, StringComparison.OrdinalIgnoreCase) ?? false)))
            .OrderByDescending(c => c.IsFavourite)];
    }

    [McpServerTool(Name = "watch_channel_on_device"), Description("Puts a Live TV channel on a device (an Apple TV, iPhone or iPad from list_devices). The result says whether the device took it (Flexarr must be open on it; otherwise the command waits two minutes).")]
    public static async Task<McpCommandResult> WatchOnDeviceAsync([Description("The device's id, from list_devices")] string deviceId,
        [Description("The channel's id, from live_channels")] Guid channelId, SignIns signIns, DeviceCommands commands, FlexarrDbContext db, CurrentProfile profile,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!profile.IsTrusted)
        {
            throw new InvalidOperationException("This sign-in may not play on other devices.");
        }

        var outcome = await DeviceEndpoints.WatchAsync(deviceId, channelId, signIns, commands, db, profile, ct);
        if (outcome.Result is not { } result)
        {
            throw new InvalidOperationException(outcome.Error ?? "No device with that id is signed in.");
        }

        return new McpCommandResult(result.Delivered, result.DeviceName, null, result.Delivered
            ? $"{result.DeviceName} is putting on {result.Channel}."
            : $"Flexarr isn't open on {result.DeviceName}; {result.Channel} will go on when it's opened in the next two minutes.");
    }

    [McpServerTool(Name = "record_programme"), Description("Sets a programme to record (its id from live_channels), or with series every showing of its title (on its channel, or with anyChannel on any). Says if it clashes: with recordings already wanting every stream then, it won't record unless one comes off.")]
    public static async Task<string> RecordAsync([Description("The programme's id, from live_channels")] long programmeId, FlexarrDbContext db, DvrService dvr,
        DvrSignal signal, CurrentProfile profile, CancellationToken ct, [Description("Record every showing of its title")] bool series = false,
        [Description("With series: on any channel, not just this one")] bool anyChannel = false)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(dvr);
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(profile);
        var programme = await db.LiveProgrammes.AsNoTracking().FirstOrDefaultAsync(p => p.Id == programmeId, ct)
            ?? throw new InvalidOperationException("There's no programme with that id (the guide may have moved on).");
        var channel = await db.LiveChannels.AsNoTracking().FirstAsync(c => c.Id == programme.ChannelId, ct);
        if (series)
        {
            var channelId = anyChannel ? (Guid?)null : channel.Id;
            var title = programme.Title.Trim();
            if (!await db.RecordingRules.AnyAsync(r => r.Title == title && r.ChannelId == channelId, ct))
            {
                db.RecordingRules.Add(new RecordingRule { Title = title, ChannelId = channelId, CreatedBy = profile.Id });
                await db.SaveChangesAsync(ct);
            }

            signal.Request();
            return $"Every showing of {title} {(anyChannel ? "on any channel" : $"on {channel.Name}")} will be recorded.";
        }

        var recording = await db.Recordings.FirstOrDefaultAsync(r => r.ChannelId == channel.Id && r.Start == programme.Start, ct);
        if (recording is null)
        {
            recording = DvrPlanner.From(programme, channel, null, profile.Id);
            db.Recordings.Add(recording);
        }
        else if (recording.State == RecordingState.Cancelled)
        {
            recording.State = RecordingState.Scheduled;
        }

        await db.SaveChangesAsync(ct);
        signal.Request();
        var upcoming = await db.Recordings.AsNoTracking().Where(r => r.State == RecordingState.Scheduled || r.State == RecordingState.Recording).ToListAsync(ct);
        var clashes = DvrPlanner.Clashing(upcoming, await dvr.StreamsAsync(ct), dvr.Before, dvr.After).Contains(recording.Id);
        var when = TimeZoneInfo.ConvertTimeFromUtc(programme.Start, TimeZoneInfo.Local);
        return clashes
            ? $"{programme.Title} on {channel.Name} at {when:HH:mm} is on the list, but it clashes: recordings scheduled before it want every stream then."
            : $"{programme.Title} on {channel.Name} at {when:HH:mm} will be recorded.";
    }

    [McpServerTool(Name = "recordings", ReadOnly = true), Description("The DVR: what's set to record (and whether it clashes), the series being recorded, and what's been recorded lately (and why anything didn't).")]
    public static async Task<McpRecordings> RecordingsAsync(FlexarrDbContext db, DvrService dvr, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(dvr);
        var streams = await dvr.StreamsAsync(ct);
        var upcoming = await db.Recordings.AsNoTracking()
            .Where(r => r.State == RecordingState.Scheduled || r.State == RecordingState.Recording).OrderBy(r => r.Start).ToListAsync(ct);
        var clashing = DvrPlanner.Clashing(upcoming, streams, dvr.Before, dvr.After);
        var recent = await db.Recordings.AsNoTracking()
            .Where(r => r.State != RecordingState.Scheduled && r.State != RecordingState.Recording && r.State != RecordingState.Cancelled)
            .OrderByDescending(r => r.Start).Take(20).ToListAsync(ct);
        var series = await db.RecordingRules.AsNoTracking().OrderBy(r => r.Title).Select(r => r.Title).ToListAsync(ct);
        static McpRecording Of(Recording r, bool clashes) =>
            new(r.Id, r.Title, r.ChannelName, r.Start, r.End, r.State.ToString().ToLowerInvariant(), r.Reason, clashes);
        return new McpRecordings([.. upcoming.Select(r => Of(r, clashing.Contains(r.Id)))], [.. recent.Select(r => Of(r, false))], series, streams);
    }
}
