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

    /// <summary>What went wrong the last time Dispatcharr was asked to download its guides.</summary>
    public GuideDownloadProblem DownloadProblem { get; private set; }

    /// <summary>When Dispatcharr was last asked to download its guides and took it up.</summary>
    public DateTime? DownloadedAt { get; private set; }

    internal void Downloaded(DateTime at, GuideDownloadProblem problem)
    {
        lock (_lock)
        {
            DownloadProblem = problem;
            // A key refused, or Dispatcharr out of reach, downloaded nothing, so trying again straight away costs its sources nothing.
            if (problem is GuideDownloadProblem.None or GuideDownloadProblem.SourceFailed or GuideDownloadProblem.TimedOut)
            {
                DownloadedAt = at;
            }
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
    /// How often it comes round while a channel has nothing listed ahead: a fuller guide may be waiting in Dispatcharr, and
    /// a channel moved to another guide entry there looks just the same in the channel list.
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

    /// <summary>
    /// The guide last written to each database (by its connection string): a hash of the downloaded guide and of the
    /// channels it was read for, and whether anything was left out for being too far ahead or past the limit.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Hash, bool CutAhead)> Written = new(StringComparer.Ordinal);

    /// <summary>Whether a full refresh is due; a channel with nothing ahead brings it forward.</summary>
    public async Task<bool> IsDueAsync(DateTime now, CancellationToken ct)
    {
        // After a restart: the channels' own stamp says when the last refresh was.
        var last = status.RefreshedAt ?? await db.LiveChannels.MaxAsync(c => (DateTime?)c.UpdatedAt, ct);
        if (last is null || now - last > Every)
        {
            return true;
        }

        return now - last > EveryWhileMissing && await db.LiveChannels.AnyAsync(c => !db.LiveProgrammes.Any(p => p.ChannelId == c.Id && p.End > now), ct);
    }

    /// <summary>With an admin key saved, has Dispatcharr download its guides, for the refresh that follows to read.</summary>
    public async Task DownloadSourcesAsync(CancellationToken ct)
    {
        var live = options.CurrentValue.LiveTv;
        var now = clock.GetUtcNow().UtcDateTime;
        if (live?.Account is not { } account || string.IsNullOrEmpty(live.AdminApiKey) || now - status.DownloadedAt < DownloadsAtMost)
        {
            return;
        }

        status.Started();
        var problem = await guides.DownloadAsync(account.BaseUrl, live.AdminApiKey, ct);
        status.Downloaded(now, problem);
        if (problem != GuideDownloadProblem.None)
        {
            LogDownloadFailed(problem);
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
            if (stamp || db.Entry(channel).State == EntityState.Added)
            {
                channel.UpdatedAt = now;
            }

            kept.Add(channel);
        }

        // Channels the source no longer lists leave, their guide and favourites with them.
        changed |= existing.Count > 0;
        db.LiveChannels.RemoveRange(existing.Values);
        await db.SaveChangesAsync(ct);
        return (kept, changed);
    }

    private async Task<int> ReplaceGuideAsync(XtreamAccount account, List<LiveChannel> channels, DateTime now, CancellationToken ct)
    {
        // Several channels can share one guide entry (an HD and an SD copy).
        var byGuideId = channels.Where(c => c.GuideId is not null).GroupBy(c => c.GuideId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(c => c.Id).ToList(), StringComparer.Ordinal);
        var from = now - KeptBehind;
        var until = now + KeptAhead;

        var fresh = new List<LiveProgramme>();
        // What was read, and for which channels: the same again means the same guide, give or take what's slipped into
        // the past since (see below).
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        foreach (var (guideId, ids) in byGuideId.OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes($"{guideId}={string.Join(',', ids.Order())};"));
        }

        var cutAhead = false;
        var complete = false;
        await using (var guide = new HashingStream(await client.OpenGuideAsync(account, ct), hash))
        {
            await foreach (var p in XmltvParser.ProgrammesAsync(guide, ct))
            {
                cutAhead |= p.Start > until;
                if (p.End < from || p.Start > until || !byGuideId.TryGetValue(p.ChannelId, out var ids))
                {
                    continue;
                }

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

                if (fresh.Count >= MaxProgrammes)
                {
                    LogGuideCut(MaxProgrammes);
                    cutAhead = true;
                    break;
                }
            }

            if (!cutAhead)
            {
                // Read to the end, so the hash is of the whole guide.
                await guide.CopyToAsync(Stream.Null, ct);
                complete = true;
            }
        }

        // The same guide as last time, for the same channels, with nothing left out at the far end: what's written
        // already is what would be written again, but for programmes that have since slipped out of the past it keeps,
        // which go on their own. Rewriting it all would hold the database's one write lock for nothing.
        var key = db.Database.GetConnectionString() ?? "";
        var read = Convert.ToHexString(hash.GetHashAndReset());
        if (complete && Written.TryGetValue(key, out var last) && last.Hash == read && !last.CutAhead)
        {
            await db.LiveProgrammes.Where(p => p.End < from).ExecuteDeleteAsync(ct);
            return await db.LiveProgrammes.CountAsync(ct);
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
        if (complete)
        {
            Written[key] = (read, cutAhead);
        }

        return fresh.Count;
    }

    /// <summary>Passes what's read through <paramref name="hash"/> on its way.</summary>
    private sealed class HashingStream(Stream inner, System.Security.Cryptography.IncrementalHash hash) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            hash.AppendData(buffer, offset, read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            hash.AppendData(buffer.Span[..read]);
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            await base.DisposeAsync();
        }
    }

    private static string? Cut(string? text, int length) => text is null || text.Length <= length ? text : text[..length];

    [LoggerMessage(Level = LogLevel.Information, Message = "Live TV: {Channels} channels, {Programmes} programmes in the guide")]
    private partial void LogRefreshed(int channels, int programmes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Live TV refresh failed: {Error}")]
    private partial void LogFailed(string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Asking Dispatcharr to download its guides: {Problem}")]
    private partial void LogDownloadFailed(GuideDownloadProblem problem);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Live TV guide cut off at {Count} programmes")]
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
public sealed partial class LiveTvGuideWorker(IServiceScopeFactory scopes, LiveTvRefreshSignal signal, IOptionsMonitor<FlexarrOptions> options,
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
