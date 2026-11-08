using System.Xml;
using Flexarr.Api.Configuration;
using Flexarr.Core.Data;
using Flexarr.Core.LiveTv;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.LiveTv;

/// <summary>How the last refresh of the channels and guide went, for Settings › Connections.</summary>
public sealed class LiveTvStatus
{
    private readonly Lock _lock = new();

    public bool Refreshing { get; private set; }
    public DateTime? RefreshedAt { get; private set; }
    public string? Error { get; private set; }

    /// <summary>What went wrong the last time Dispatcharr was asked to download its guides, until a refresh has moved on from it.</summary>
    public GuideDownloadProblem DownloadProblem { get; private set; }

    /// <summary>When Dispatcharr was last asked to download its guides and took it up.</summary>
    public DateTime? DownloadedAt { get; private set; }

    /// <summary>Goes up as each refresh finishes, however it went: two that fail alike look the same otherwise.</summary>
    public int RefreshesFinished { get; private set; }

    /// <summary>When a refresh last finished, however it went.</summary>
    public DateTime? AttemptedAt { get; private set; }

    /// <summary>How many refreshes in a row have found the same guide.</summary>
    public int QuietRefreshes { get; private set; }

    /// <summary>Which address and key the download stamp is for: a new one owes nothing to the old one's limit.</summary>
    private int _downloadedFor;

    private (int Count, DateTime At)? _missing;

    /// <summary>Whether the sources behind this address and key were asked too recently to ask again.</summary>
    internal bool DownloadedTooRecently(DateTime now, int forWhom, TimeSpan atMost)
    {
        lock (_lock)
        {
            return forWhom == _downloadedFor && now - DownloadedAt < atMost;
        }
    }

    internal void Downloaded(DateTime at, int forWhom, GuideDownload download)
    {
        lock (_lock)
        {
            DownloadProblem = download.Problem;
            // A key refused, or Dispatcharr out of reach before it was asked anything, costs its sources nothing, so
            // trying again straight away is fine. Asked at all, the limit applies, however it went from there.
            if (download.Asked || download.Problem is GuideDownloadProblem.None or GuideDownloadProblem.SourceFailed or GuideDownloadProblem.TimedOut)
            {
                DownloadedAt = at;
                _downloadedFor = forWhom;
            }
        }
    }

    /// <summary>The last download's problem no longer describes what's happening.</summary>
    internal void ForgetDownloadProblem()
    {
        lock (_lock)
        {
            DownloadProblem = GuideDownloadProblem.None;
        }
    }

    internal void GuideRead(bool changed)
    {
        lock (_lock)
        {
            QuietRefreshes = changed ? 0 : QuietRefreshes + 1;
        }
    }

    /// <summary>The count of channels without listings, while it's fresh enough to save asking the database.</summary>
    internal int? MissingCount(DateTime now, TimeSpan keptFor)
    {
        lock (_lock)
        {
            return _missing is { } missing && now - missing.At < keptFor ? missing.Count : null;
        }
    }

    internal void RememberMissing(int count, DateTime at)
    {
        lock (_lock)
        {
            _missing = (count, at);
        }
    }

    internal void Started()
    {
        lock (_lock)
        {
            Refreshing = true;
        }
    }

    internal void Finished(DateTime at, string? error)
    {
        lock (_lock)
        {
            Refreshing = false;
            Error = error;
            RefreshesFinished++;
            AttemptedAt = at;
            _missing = null;
            if (error is null)
            {
                RefreshedAt = at;
            }
        }
    }
}

/// <summary>Brings the channel list and the guide up to date from the Live TV source.</summary>
public sealed partial class LiveTvGuide(FlexarrDbContext db, XtreamClient client, DispatcharrGuides guides, CommunityLogoIndex communityLogos, IOptionsMonitor<FlexarrOptions> options,
    LiveTvStatus status, TimeProvider clock, ILogger<LiveTvGuide> logger)
{
    /// <summary>How often a full refresh comes round.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromHours(6);

    /// <summary>
    /// How often it comes round at first while a channel has nothing listed ahead: a fuller guide may be waiting in
    /// Dispatcharr, and a channel moved to another guide entry there looks just the same in the channel list. Each refresh
    /// that finds the same guide doubles it, up to <see cref="Every"/>: a channel may never have listings.
    /// </summary>
    public static readonly TimeSpan EveryWhileMissing = TimeSpan.FromHours(1);

    /// <summary>However often Refresh is pressed, Dispatcharr's sources (often someone's IPTV provider) are only asked this often.</summary>
    public static readonly TimeSpan DownloadsAtMost = TimeSpan.FromMinutes(15);

    /// <summary>Programmes kept after they end, so the guide shows what's just been on.</summary>
    public static readonly TimeSpan KeptBehind = TimeSpan.FromHours(3);

    /// <summary>How far ahead the guide is kept; sources rarely list more than a week.</summary>
    public static readonly TimeSpan KeptAhead = TimeSpan.FromDays(8);

    /// <summary>A guide bigger than this is cut off rather than filling the database.</summary>
    private const int MaxProgrammes = 500_000;

    private const int Batch = 5_000;

    /// <summary>How long an unchanged guide with some left out at the far end goes without being written again.</summary>
    private static readonly TimeSpan RewriteCutGuideEvery = TimeSpan.FromDays(1);

    /// <summary>
    /// The guide last written to each database (by its connection string): a hash of the downloaded guide and of the
    /// channels it was read for, and whether anything was left out for being too far ahead or past the limit.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Hash, bool CutAhead, DateTime At)> Written = new(StringComparer.Ordinal);

    /// <summary>Whether a full refresh is due; a channel with nothing ahead brings it forward.</summary>
    public async Task<bool> IsDueAsync(DateTime now, CancellationToken ct)
    {
        // After a restart: the channels' own stamp says when the last refresh was.
        var last = status.RefreshedAt ?? await db.LiveChannels.MaxAsync(c => (DateTime?)c.UpdatedAt, ct);
        if (last is null || now - last > Every)
        {
            return true;
        }

        // A channel with no guide entry has nothing a refresh could find, and a refresh that failed isn't tried again every few minutes.
        var wait = EveryWhileMissing * (1 << Math.Min(status.QuietRefreshes, 3));
        return now - last > wait && !(now - status.AttemptedAt < EveryWhileMissing)
            && await db.LiveChannels.AnyAsync(c => c.GuideId != null && !db.LiveProgrammes.Any(p => p.ChannelId == c.Id && p.End > now), ct);
    }

    /// <summary>With an admin key saved, has Dispatcharr download its guides, for the refresh that follows to read.</summary>
    public async Task DownloadSourcesAsync(CancellationToken ct)
    {
        var live = options.CurrentValue.LiveTv;
        var now = clock.GetUtcNow().UtcDateTime;
        if (live?.Account is not { } account || string.IsNullOrEmpty(live.AdminApiKey))
        {
            status.ForgetDownloadProblem();
            return;
        }

        var key = live.AdminApiKey;
        var forWhom = HashCode.Combine(account.BaseUrl, key);
        if (status.DownloadedTooRecently(now, forWhom, DownloadsAtMost))
        {
            // Nothing's being asked this time, so the last one's trouble isn't this refresh's.
            status.ForgetDownloadProblem();
            return;
        }

        status.Started();
        // A new address or key in Settings shouldn't wait for this one to finish.
        using var changed = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var watch = options.OnChange(o =>
        {
            if (o.LiveTv?.Account?.BaseUrl != account.BaseUrl || o.LiveTv?.AdminApiKey != key)
            {
                changed.Cancel();
            }
        });

        GuideDownload download;
        try
        {
            download = await guides.DownloadAsync(account.BaseUrl, key, changed.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return;
        }

        status.Downloaded(now, forWhom, download);
        if (download.Problem != GuideDownloadProblem.None)
        {
            LogDownloadFailed(download.Problem);
        }
    }

    /// <param name="onlyIfChanged">
    /// A quick check: the channel list is read, and the guide only fetched again when channels came, went or moved to
    /// another guide entry.
    /// </param>
    public async Task RefreshAsync(CancellationToken ct, bool onlyIfChanged = false)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        if (options.CurrentValue.LiveTv?.Account is not { } account)
        {
            // No source (or it was removed): its channels and guide go with it.
            await db.LiveChannels.ExecuteDeleteAsync(ct);
            if (status.Refreshing)
            {
                status.Finished(now, null);
            }

            return;
        }

        // Only a refresh that fetches the guide counts as one (for the next being due, and Settings); a quick check that
        // finds nothing new leaves the status as it was.
        var refreshing = !onlyIfChanged;
        if (refreshing)
        {
            status.Started();
        }

        string? error = null;
        try
        {
            var (channels, changed) = await SyncChannelsAsync(account, now, stamp: refreshing, ct);
            if (!refreshing && !changed)
            {
                return;
            }

            refreshing = true;
            status.Started();
            var programmes = await ReplaceGuideAsync(account, channels, now, ct);
            LogRefreshed(channels.Count, programmes);
        }
        catch (XtreamException ex)
        {
            error = ex.Message;
            LogFailed(ex.Message);
        }
        catch (XmlException ex)
        {
            error = "The guide from the Live TV source couldn't be read.";
            LogFailed(ex.Message);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Anything else (the database, the connection dropping part-way) is a failed refresh too; the worker logs it.
            error = "The Live TV refresh didn't finish.";
            throw;
        }
        finally
        {
            if (refreshing || error is not null)
            {
                status.Finished(now, error);
            }
        }
    }

    /// <summary>Brings the channels in line with the source's list; says whether the guide needs fetching for them.</summary>
    /// <param name="stamp">A full refresh: every channel is marked as refreshed now (which says when the next is due).</param>
    private async Task<(List<LiveChannel> Channels, bool Changed)> SyncChannelsAsync(XtreamAccount account, DateTime now, bool stamp, CancellationToken ct)
    {
        var listed = await client.GetChannelsAsync(account, ct);
        var logos = await communityLogos.GetAsync(ct);
        // Channels that name no country are looked for where most of the line-up is, else this server's region.
        var home = CommunityLogos.HomeCountry(listed.Select(c => (c.Name, c.Group))) ?? options.CurrentValue.Podcasts.ResolvedCountry;
        var existing = await db.LiveChannels.ToDictionaryAsync(c => c.SourceId, ct);
        var kept = new List<LiveChannel>();
        var changed = false;
        foreach (var c in listed.DistinctBy(c => c.StreamId))
        {
            if (!existing.Remove(c.StreamId, out var channel))
            {
                channel = new LiveChannel { SourceId = c.StreamId, Name = c.Name };
                db.LiveChannels.Add(channel);
                changed = true;
            }

            changed |= channel.GuideId != c.GuideId;
            channel.Name = c.Name;
            channel.Number = c.Number;
            channel.Group = c.Group;
            // The community collection's current logo by default, at its own proportions; the source's when it has none.
            channel.LogoUrl = logos?.Find(c.Name, c.Group, home) ?? c.LogoUrl;
            channel.GuideId = c.GuideId;
            if (db.Entry(channel).State == EntityState.Added)
            {
                channel.UpdatedAt = now;
            }

            kept.Add(channel);
        }

        // Channels the source no longer lists leave, their guide and favourites with them.
        changed |= existing.Count > 0;
        db.LiveChannels.RemoveRange(existing.Values);
        await db.SaveChangesAsync(ct);
        if (stamp)
        {
            await db.LiveChannels.ExecuteUpdateAsync(u => u.SetProperty(c => c.UpdatedAt, now), ct);
        }

        return (kept, changed);
    }

    private async Task<int> ReplaceGuideAsync(XtreamAccount account, List<LiveChannel> channels, DateTime now, CancellationToken ct)
    {
        // Several channels can share one guide entry (an HD and an SD copy).
        var byGuideId = channels.Where(c => c.GuideId is not null).GroupBy(c => c.GuideId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(c => c.Id).ToList(), StringComparer.Ordinal);
        var from = now - KeptBehind;
        var until = now + KeptAhead;

        // What was read, and for which channels: the same again means the same guide, give or take what's slipped into
        // the past since (see below).
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        foreach (var (guideId, ids) in byGuideId.OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes($"{guideId}={string.Join(',', ids.Order())};"));
        }

        // Kept on disk until it's known to be new, so the same guide again costs a download and not every programme in memory.
        await using var file = new FileStream(Path.Combine(Path.GetTempPath(), $"flexarr-guide-{Guid.NewGuid():N}.xml"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        await using (var guide = await client.OpenGuideAsync(account, ct))
        {
            var buffer = new byte[81920];
            int got;
            while ((got = await guide.ReadAsync(buffer, ct)) > 0)
            {
                hash.AppendData(buffer, 0, got);
                await file.WriteAsync(buffer.AsMemory(0, got), ct);
            }
        }

        // The same guide as last time, for the same channels: what's written already is what would be written again, but
        // for programmes that have since slipped out of the past it keeps, which go on their own. Rewriting it all would
        // hold the database's one write lock for nothing. A guide with some left out at the far end does need writing
        // again as they come into reach, but only every so often: they arrive a day at a time.
        var key = db.Database.GetConnectionString() ?? "";
        var read = Convert.ToHexString(hash.GetHashAndReset());
        if (Written.TryGetValue(key, out var last) && last.Hash == read && (!last.CutAhead || now - last.At < RewriteCutGuideEvery))
        {
            status.GuideRead(changed: false);
            await db.LiveProgrammes.Where(p => p.End < from).ExecuteDeleteAsync(ct);
            return await db.LiveProgrammes.CountAsync(ct);
        }

        status.GuideRead(changed: true);
        file.Position = 0;
        var fresh = new List<LiveProgramme>();
        var cutAhead = false;
        // The limit is shared out, so a huge line-up keeps a shorter guide on every channel, not a full one on those that
        // happen to come first in the file and nothing on the rest.
        var perChannel = Math.Max(1, MaxProgrammes / Math.Max(1, byGuideId.Sum(g => g.Value.Count)));
        var perEntry = new Dictionary<string, int>(StringComparer.Ordinal);
        var overLimit = false;
        await foreach (var p in XmltvParser.ProgrammesAsync(file, ct))
        {
            cutAhead |= p.Start > until;
            if (p.End < from || p.Start > until || !byGuideId.TryGetValue(p.ChannelId, out var ids))
            {
                continue;
            }

            var kept = perEntry.GetValueOrDefault(p.ChannelId);
            if (kept >= perChannel)
            {
                overLimit = true;
                continue;
            }

            perEntry[p.ChannelId] = kept + 1;
            foreach (var id in ids)
            {
                fresh.Add(new LiveProgramme
                {
                    ChannelId = id,
                    Start = p.Start,
                    End = p.End,
                    Title = Cut(p.Title, 500)!,
                    Subtitle = Cut(p.Subtitle, 500),
                    Description = Cut(p.Description, 4000),
                    Category = Cut(p.Category, 100),
                    Season = p.Season,
                    Episode = p.Episode,
                    ImageUrl = p.ImageUrl is { Length: <= 2048 } image ? image : null,
                    IsNew = p.IsNew,
                });
            }
        }

        if (overLimit)
        {
            LogGuideCut(perChannel);
            cutAhead = true;
        }

        Written.TryRemove(key, out _);
        fresh = LiveSchedule.Tidy(fresh);

        // Swapped in one go, so the guide is never half there.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.LiveProgrammes.ExecuteDeleteAsync(ct);
        foreach (var chunk in fresh.Chunk(Batch))
        {
            db.LiveProgrammes.AddRange(chunk);
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }

        await transaction.CommitAsync(ct);
        Written[key] = (read, cutAhead, now);
        return fresh.Count;
    }

    private static string? Cut(string? text, int length) => text is null || text.Length <= length ? text : text[..length];

    [LoggerMessage(Level = LogLevel.Information, Message = "Live TV: {Channels} channels, {Programmes} programmes in the guide")]
    private partial void LogRefreshed(int channels, int programmes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Live TV refresh failed: {Error}")]
    private partial void LogFailed(string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Asking Dispatcharr to download its guides: {Problem}")]
    private partial void LogDownloadFailed(GuideDownloadProblem problem);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Live TV guide cut off at {Count} programmes a channel")]
    private partial void LogGuideCut(int count);
}

/// <summary>Asks for the channels and guide to be refreshed now (Settings saved, or Refresh pressed).</summary>
public sealed class LiveTvRefreshSignal
{
    // One waiting request is enough: asking again while one is pending changes nothing.
    private readonly System.Threading.Channels.Channel<bool> _asked = System.Threading.Channels.Channel.CreateBounded<bool>(
        new System.Threading.Channels.BoundedChannelOptions(1) { FullMode = System.Threading.Channels.BoundedChannelFullMode.DropWrite });

    private int _download;

    /// <param name="download">Have Dispatcharr download its guides first, when there's a key to ask with (Refresh pressed).</param>
    public void Request(bool download = false)
    {
        if (download)
        {
            Interlocked.Exchange(ref _download, 1);
        }

        _asked.Writer.TryWrite(true);
    }

    /// <summary>Whether a download was asked for; asked once, it's forgotten.</summary>
    internal bool TakeDownload() => Interlocked.Exchange(ref _download, 0) == 1;

    /// <summary>True when asked within <paramref name="timeout"/>, false when it passed without.</summary>
    internal async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(timeout);
        try
        {
            return await _asked.Reader.ReadAsync(wait.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }
}

/// <summary>Refreshes the channels and guide every few hours, and when asked to.</summary>
public sealed partial class LiveTvGuideWorker(IServiceScopeFactory scopes, LiveTvRefreshSignal signal, LiveTvStatus status, IOptionsMonitor<FlexarrOptions> options,
    TimeProvider clock, ILogger<LiveTvGuideWorker> logger) : BackgroundService
{
    public static readonly TimeSpan Every = LiveTvGuide.Every;

    /// <summary>How often Dispatcharr's channel list is checked for channels added or removed.</summary>
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var asked = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (options.CurrentValue.LiveTv?.Account is not null)
                {
                    // A full refresh every few hours (or when asked); in between, a quick look for channels added or
                    // removed in Dispatcharr, which has no webhook for that.
                    await using var scope = scopes.CreateAsyncScope();
                    var guide = scope.ServiceProvider.GetRequiredService<LiveTvGuide>();
                    var full = asked || await guide.IsDueAsync(clock.GetUtcNow().UtcDateTime, stoppingToken);
                    if (signal.TakeDownload())
                    {
                        await guide.DownloadSourcesAsync(stoppingToken);
                    }
                    else if (full)
                    {
                        // A refresh nobody pressed: whatever the last pressed one ran into, this is what's there now.
                        status.ForgetDownloadProblem();
                    }

                    await guide.RefreshAsync(stoppingToken, onlyIfChanged: !full);
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                LogFailed(ex);
            }

            asked = await signal.WaitAsync(Tick, stoppingToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Refreshing Live TV failed")]
    private partial void LogFailed(Exception exception);
}
