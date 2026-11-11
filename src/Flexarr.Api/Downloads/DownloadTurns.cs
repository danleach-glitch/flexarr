namespace Flexarr.Api.Downloads;

/// <summary>
/// Whose download is converted next. Conversions run one at a time and a season takes hours, so it's not simply the
/// oldest: the profile served longest ago (or never) goes first, and of its downloads, the batch (a show, an album, a
/// lone film) served longest ago, oldest first within it. A parent's film for tomorrow's flight goes next, not after
/// someone's ten seasons, and so does a film queued after the same profile's own season. Ties go to the oldest.
/// Kept in memory: after a restart the queue starts oldest first again.
/// </summary>
internal sealed class DownloadTurns
{
    /// <summary>A queued download.</summary>
    /// <param name="Batch">What it was asked for with: its show (an episode), its album (a track), else itself.</param>
    internal readonly record struct Waiting(Guid Id, string ProfileId, Guid Batch, DateTime CreatedAt);

    /// <summary>When each profile, and each batch of a profile's, last had a download started: a count, the higher the later.</summary>
    private readonly Dictionary<string, long> _profiles = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Profile, Guid Batch), long> _batches = [];
    private long _served;

    /// <summary>The download to start of those <paramref name="queued"/>, noted as served; null when none is.</summary>
    public Guid? Next(IReadOnlyCollection<Waiting> queued)
    {
        // Only what's still waiting is remembered: a profile or batch that comes back later starts afresh, at the front.
        var profiles = queued.Select(d => d.ProfileId).ToHashSet(StringComparer.Ordinal);
        var batches = queued.Select(d => (d.ProfileId, d.Batch)).ToHashSet();
        foreach (var key in _profiles.Keys.Where(k => !profiles.Contains(k)).ToList())
        {
            _profiles.Remove(key);
        }

        foreach (var key in _batches.Keys.Where(k => !batches.Contains(k)).ToList())
        {
            _batches.Remove(key);
        }

        var profile = queued.GroupBy(d => d.ProfileId, StringComparer.Ordinal)
            .OrderBy(g => _profiles.GetValueOrDefault(g.Key)).ThenBy(g => g.Min(d => d.CreatedAt)).ThenBy(g => g.Key, StringComparer.Ordinal)
            .FirstOrDefault();
        if (profile is null)
        {
            return null;
        }

        var next = profile.GroupBy(d => d.Batch)
            .OrderBy(g => _batches.GetValueOrDefault((profile.Key, g.Key))).ThenBy(g => g.Min(d => d.CreatedAt)).ThenBy(g => g.Key)
            .First().OrderBy(d => d.CreatedAt).ThenBy(d => d.Id).First();
        _served++;
        _profiles[next.ProfileId] = _served;
        _batches[(next.ProfileId, next.Batch)] = _served;
        return next.Id;
    }
}
