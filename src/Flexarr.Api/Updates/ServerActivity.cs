using Flexarr.Api.LiveTv;
using Flexarr.Api.Playback;
using Flexarr.Core.Data;
using Flexarr.Core.Localization;
using Flexarr.Core.LiveTv;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Api.Updates;

/// <summary>What an update's restart would interrupt: playback, Live TV, and recordings now or about to start.</summary>
public sealed class ServerActivity(PlaybackSessions playback, LiveTvViewers live, DvrService dvr, IServiceScopeFactory scopes, TimeProvider time)
{
    /// <summary>A recording starting this soon counts as one under way: an update takes a minute or two.</summary>
    public static readonly TimeSpan RecordingLeadTime = TimeSpan.FromMinutes(30);

    /// <summary>Each thing going on, in words; empty when nothing would notice a restart.</summary>
    public async Task<IReadOnlyList<string>> InUseAsync(CancellationToken ct)
    {
        var reasons = new List<string>();
        var streams = playback.Playing.Count(s => !s.Finished);
        if (streams > 0)
        {
            reasons.Add(Loc.T("{count, plural, one {Something is playing.} other {# things are playing.}}", new { count = streams }));
        }

        var channels = live.All.Count;
        if (channels > 0)
        {
            reasons.Add(Loc.T("{count, plural, one {Someone is watching Live TV.} other {# people are watching Live TV.}}", new { count = channels }));
        }

        foreach (var recording in dvr.Active)
        {
            reasons.Add(Loc.T("Recording {title}.", new { title = recording.Title }));
        }

        var soon = time.GetUtcNow().UtcDateTime + RecordingLeadTime + dvr.Before;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var next = await db.Recordings.AsNoTracking()
            .Where(r => r.State == RecordingState.Scheduled && r.Start <= soon && r.End > time.GetUtcNow().UtcDateTime)
            .OrderBy(r => r.Start)
            .Select(r => r.Title)
            .FirstOrDefaultAsync(ct);
        if (next is not null)
        {
            reasons.Add(Loc.T("{title} is about to record.", new { title = next }));
        }

        return reasons;
    }
}
