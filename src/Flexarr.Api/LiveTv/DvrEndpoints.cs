using Flexarr.Api.Profiles;
using Flexarr.Api.Configuration;
using Flexarr.Core.Data;
using Flexarr.Core.LiveTv;
using Flexarr.Core.Scanning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.LiveTv;

/// <param name="Clashes">It won't record as things stand: every stream is wanted by recordings scheduled before it.</param>
/// <param name="ItemId">The recording in the library, once it's been scanned in (to play it).</param>
public sealed record RecordingDto(Guid Id, Guid? RuleId, Guid ChannelId, string ChannelName, string Title, string? Subtitle, string? Description,
    int? Season, int? Episode, bool IsNew, DateTime Start, DateTime End, string State, string? Reason, long? Size, bool Clashes, Guid? ItemId);

public sealed record RecordingRuleDto(Guid Id, string Title, Guid? ChannelId, string? ChannelName, bool NewOnly, int KeepLatest, int Upcoming, int Recorded);

/// <param name="Streams">How many channels can be open at once (recordings and live viewing together).</param>
/// <param name="FreeBytes">Free on the recordings drive; null when it can't be read (not connected).</param>
public sealed record RecordingsDto(IReadOnlyList<RecordingDto> Upcoming, IReadOnlyList<RecordingDto> Recent, IReadOnlyList<RecordingRuleDto> Series,
    int Streams, string Folder, long? FreeBytes);

public sealed record RecordRequest(long ProgrammeId);

/// <param name="AnyChannel">Every showing of the title, whichever channel; otherwise only this programme's channel.</param>
/// <param name="KeepLatest">Keep only this many recordings (the oldest go first); 0 keeps them all.</param>
public sealed record RecordSeriesRequest(long ProgrammeId, bool AnyChannel = false, bool NewOnly = false, int KeepLatest = 0);

public sealed record SeriesUpdate(bool? AnyChannel = null, bool? NewOnly = null, int? KeepLatest = null);

/// <summary>
/// Recording from the guide: one showing, or a series (every showing of the title). Recordings are the household's:
/// anyone can schedule them, stop them or delete them.
/// </summary>
public static class DvrEndpoints
{
    public static RouteGroupBuilder MapDvrEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/livetv/recordings").WithTags("Live TV").RequireAuthorization();

        group.MapGet("/", async (FlexarrDbContext db, DvrService dvr, IOptionsMonitor<FlexarrOptions> options, TimeProvider clock, CancellationToken ct) =>
        {
            var now = clock.GetUtcNow().UtcDateTime;
            var streams = await dvr.StreamsAsync(ct);
            var upcoming = await db.Recordings.AsNoTracking()
                .Where(r => r.State == RecordingState.Scheduled || r.State == RecordingState.Recording).OrderBy(r => r.Start).ToListAsync(ct);
            var recent = await db.Recordings.AsNoTracking()
                .Where(r => r.State != RecordingState.Scheduled && r.State != RecordingState.Recording && r.State != RecordingState.Cancelled)
                .OrderByDescending(r => r.Start).Take(100).ToListAsync(ct);
            var clashing = DvrPlanner.Clashing(upcoming, streams, dvr.Before, dvr.After);
            var items = await ItemsAsync(db, recent, ct);
            var rules = await RulesAsync(db, ct);
            var folder = await DvrFolders.ResolveAsync(db, options.CurrentValue, ct);
            return new RecordingsDto(
                [.. upcoming.Select(r => ToDto(r, clashing.Contains(r.Id), null))],
                [.. recent.Select(r => ToDto(r, false, r.FilePath is { } path ? items.GetValueOrDefault(path) : null))],
                rules, streams, folder, FreeBytes(folder));
        });

        group.MapPost("/", async (RecordRequest request, FlexarrDbContext db, DvrService dvr, DvrSignal signal, CurrentProfile profile, CancellationToken ct) =>
        {
            if (await ShowingAsync(db, request.ProgrammeId, ct) is not { } showing)
            {
                return Results.NotFound();
            }

            if (await db.Recordings.FirstOrDefaultAsync(r => r.ChannelId == showing.Channel.Id && r.Start == showing.Programme.Start, ct) is { } existing)
            {
                // Already on the list (or taken off it once): back on.
                if (existing.State == RecordingState.Cancelled)
                {
                    existing.State = RecordingState.Scheduled;
                    existing.Reason = null;
                    await db.SaveChangesAsync(ct);
                    signal.Request();
                }

                return Results.Ok(await DtoAsync(db, dvr, existing, ct));
            }

            var recording = DvrPlanner.From(showing.Programme, showing.Channel, null, profile.Id);
            db.Recordings.Add(recording);
            await db.SaveChangesAsync(ct);
            signal.Request();
            return Results.Ok(await DtoAsync(db, dvr, recording, ct));
        });

        group.MapDelete("/{id:guid}", async (Guid id, FlexarrDbContext db, DvrService dvr, DvrSignal signal, ILibraryScanQueue scans, CancellationToken ct) =>
        {
            if (await db.Recordings.FirstOrDefaultAsync(r => r.Id == id, ct) is not { } recording)
            {
                return Results.NotFound();
            }

            switch (recording.State)
            {
                case RecordingState.Scheduled:
                    // Off the list; a series won't put it back.
                    recording.State = RecordingState.Cancelled;
                    break;
                case RecordingState.Recording:
                    // Stopped now, keeping what's recorded.
                    dvr.Stop(id);
                    return Results.Accepted();
                default:
                    if (recording.FilePath is { } path && File.Exists(path))
                    {
                        File.Delete(path);
                        await scans.EnqueueAsync(new LibraryScanRequest(path, "Recording deleted", Deleted: true), ct);
                    }

                    db.Recordings.Remove(recording);
                    break;
            }

            await db.SaveChangesAsync(ct);
            signal.Request();
            return Results.NoContent();
        });

        group.MapPost("/series", async (RecordSeriesRequest request, FlexarrDbContext db, DvrSignal signal, CurrentProfile profile, CancellationToken ct) =>
        {
            if (await ShowingAsync(db, request.ProgrammeId, ct) is not { } showing)
            {
                return Results.NotFound();
            }

            var channelId = request.AnyChannel ? (Guid?)null : showing.Channel.Id;
            var title = showing.Programme.Title.Trim();
            var rule = await db.RecordingRules.FirstOrDefaultAsync(r => r.Title == title && r.ChannelId == channelId, ct);
            if (rule is null)
            {
                rule = new RecordingRule { Title = title, ChannelId = channelId, CreatedBy = profile.Id };
                db.RecordingRules.Add(rule);
            }

            rule.NewOnly = request.NewOnly;
            rule.KeepLatest = Math.Clamp(request.KeepLatest, 0, 500);
            await db.SaveChangesAsync(ct);
            signal.Request();
            return Results.Ok((await RulesAsync(db, ct)).First(r => r.Id == rule.Id));
        });

        group.MapPut("/series/{id:guid}", async (Guid id, SeriesUpdate update, FlexarrDbContext db, DvrSignal signal, CancellationToken ct) =>
        {
            if (await db.RecordingRules.FirstOrDefaultAsync(r => r.Id == id, ct) is not { } rule)
            {
                return Results.NotFound();
            }

            if (update.AnyChannel == true)
            {
                rule.ChannelId = null;
            }

            rule.NewOnly = update.NewOnly ?? rule.NewOnly;
            rule.KeepLatest = Math.Clamp(update.KeepLatest ?? rule.KeepLatest, 0, 500);
            await db.SaveChangesAsync(ct);
            signal.Request();
            return Results.Ok((await RulesAsync(db, ct)).First(r => r.Id == rule.Id));
        });

        // A series stops: its scheduled showings come off the list; what it recorded stays.
        group.MapDelete("/series/{id:guid}", async (Guid id, FlexarrDbContext db, DvrSignal signal, CancellationToken ct) =>
        {
            if (await db.RecordingRules.FirstOrDefaultAsync(r => r.Id == id, ct) is not { } rule)
            {
                return Results.NotFound();
            }

            await db.Recordings.Where(r => r.RuleId == id && r.State == RecordingState.Scheduled).ExecuteDeleteAsync(ct);
            db.RecordingRules.Remove(rule);
            await db.SaveChangesAsync(ct);
            signal.Request();
            return Results.NoContent();
        });

        return api;
    }

    /// <summary>What the guide shows on a programme: set to record, recording now, and whether a series covers it.</summary>
    internal sealed record Marks(Dictionary<(Guid Channel, DateTime Start), RecordingState> Recordings, List<RecordingRule> Rules)
    {
        public static async Task<Marks> LoadAsync(FlexarrDbContext db, DateTime from, DateTime until, CancellationToken ct)
        {
            var recordings = await db.Recordings.AsNoTracking()
                .Where(r => (r.State == RecordingState.Scheduled || r.State == RecordingState.Recording) && r.End > from && r.Start < until)
                .Select(r => new { r.ChannelId, r.Start, r.State }).ToListAsync(ct);
            var map = new Dictionary<(Guid, DateTime), RecordingState>();
            foreach (var r in recordings)
            {
                map[(r.ChannelId, r.Start)] = r.State;
            }

            return new Marks(map, await db.RecordingRules.AsNoTracking().ToListAsync(ct));
        }

        public string? StateOf(LiveProgramme p) => Recordings.TryGetValue((p.ChannelId, p.Start), out var state)
            ? state == RecordingState.Recording ? "recording" : "scheduled"
            : null;

        public bool InSeries(LiveProgramme p) => Rules.Any(r =>
            string.Equals(r.Title.Trim(), p.Title.Trim(), StringComparison.OrdinalIgnoreCase) && (r.ChannelId is null || r.ChannelId == p.ChannelId));
    }

    private static async Task<GuideShowing?> ShowingAsync(FlexarrDbContext db, long programmeId, CancellationToken ct)
    {
        var programme = await db.LiveProgrammes.AsNoTracking().FirstOrDefaultAsync(p => p.Id == programmeId, ct);
        var channel = programme is null ? null : await db.LiveChannels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == programme.ChannelId, ct);
        return programme is null || channel is null ? null : new GuideShowing(programme, channel);
    }

    private static async Task<RecordingDto> DtoAsync(FlexarrDbContext db, DvrService dvr, Recording recording, CancellationToken ct)
    {
        var upcoming = await db.Recordings.AsNoTracking()
            .Where(r => r.State == RecordingState.Scheduled || r.State == RecordingState.Recording).ToListAsync(ct);
        var clashing = DvrPlanner.Clashing(upcoming, await dvr.StreamsAsync(ct), dvr.Before, dvr.After);
        return ToDto(recording, clashing.Contains(recording.Id), null);
    }

    private static async Task<List<RecordingRuleDto>> RulesAsync(FlexarrDbContext db, CancellationToken ct)
    {
        var rules = await db.RecordingRules.AsNoTracking().OrderBy(r => r.Title).ToListAsync(ct);
        var names = await db.LiveChannels.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var counts = await db.Recordings.AsNoTracking().Where(r => r.RuleId != null)
            .GroupBy(r => new { r.RuleId, r.State }).Select(g => new { g.Key.RuleId, g.Key.State, Count = g.Count() }).ToListAsync(ct);
        return [.. rules.Select(r => new RecordingRuleDto(r.Id, r.Title, r.ChannelId, r.ChannelId is { } c ? names.GetValueOrDefault(c) : null,
            r.NewOnly, r.KeepLatest,
            counts.Where(x => x.RuleId == r.Id && x.State == RecordingState.Scheduled).Sum(x => x.Count),
            counts.Where(x => x.RuleId == r.Id && x.State == RecordingState.Completed).Sum(x => x.Count)))];
    }

    private static async Task<Dictionary<string, Guid>> ItemsAsync(FlexarrDbContext db, IEnumerable<Recording> recordings, CancellationToken ct)
    {
        var paths = recordings.Select(r => r.FilePath).OfType<string>().ToList();
        if (paths.Count == 0)
        {
            return [];
        }

        return (await db.MediaFiles.AsNoTracking().Where(f => paths.Contains(f.Path)).Select(f => new { f.Path, f.MediaItemId }).ToListAsync(ct))
            .GroupBy(f => f.Path).ToDictionary(g => g.Key, g => g.First().MediaItemId);
    }

    private static long? FreeBytes(string folder)
    {
        try
        {
            return Directory.Exists(folder) ? new DriveInfo(folder).AvailableFreeSpace : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    internal static RecordingDto ToDto(Recording r, bool clashes, Guid? itemId) =>
        new(r.Id, r.RuleId, r.ChannelId, r.ChannelName, r.Title, r.Subtitle, r.Description, r.Season, r.Episode, r.IsNew, r.Start, r.End,
            r.State.ToString().ToLowerInvariant(), r.Reason, r.Size, clashes, itemId);
}
