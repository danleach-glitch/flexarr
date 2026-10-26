using System.Xml;
using Flexarr.Api.Configuration;
using Flexarr.Core.Data;
using Flexarr.Core.LiveTv;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.LiveTv;

/// <summary>How the last refresh of the channels and guide went, for Settings › Live TV.</summary>
public sealed class LiveTvStatus
{
    private readonly Lock _lock = new();

    public bool Refreshing { get; private set; }
    public DateTime? RefreshedAt { get; private set; }
    public string? Error { get; private set; }

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
public sealed partial class LiveTvGuide(FlexarrDbContext db, XtreamClient client, IOptionsMonitor<FlexarrOptions> options, LiveTvStatus status,
    TimeProvider clock, ILogger<LiveTvGuide> logger)
{
    /// <summary>Programmes kept after they end, so the guide shows what's just been on.</summary>
    public static readonly TimeSpan KeptBehind = TimeSpan.FromHours(3);

    /// <summary>How far ahead the guide is kept; sources rarely list more than a week.</summary>
    public static readonly TimeSpan KeptAhead = TimeSpan.FromDays(8);

    /// <summary>A guide bigger than this is cut off rather than filling the database.</summary>
    private const int MaxProgrammes = 500_000;

    private const int Batch = 5_000;

    public async Task RefreshAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        if (options.CurrentValue.LiveTv?.Account is not { } account)
        {
            // No source (or it was removed): its channels and guide go with it.
            await db.LiveChannels.ExecuteDeleteAsync(ct);
            return;
        }

        status.Started();
        string? error = null;
        try
        {
            var channels = await SyncChannelsAsync(account, now, ct);
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
            status.Finished(now, error);
        }
    }

    private async Task<List<LiveChannel>> SyncChannelsAsync(XtreamAccount account, DateTime now, CancellationToken ct)
    {
        var listed = await client.GetChannelsAsync(account, ct);
        var existing = await db.LiveChannels.ToDictionaryAsync(c => c.SourceId, ct);
        var kept = new List<LiveChannel>();
        foreach (var c in listed.DistinctBy(c => c.StreamId))
        {
            if (!existing.Remove(c.StreamId, out var channel))
            {
                channel = new LiveChannel { SourceId = c.StreamId, Name = c.Name };
                db.LiveChannels.Add(channel);
            }

            channel.Name = c.Name;
            channel.Number = c.Number;
            channel.Group = c.Group;
            channel.LogoUrl = c.LogoUrl;
            channel.GuideId = c.GuideId;
            channel.UpdatedAt = now;
            kept.Add(channel);
        }

        // Channels the source no longer lists leave, their guide with them.
        db.LiveChannels.RemoveRange(existing.Values);
        await db.SaveChangesAsync(ct);
        return kept;
    }

    private async Task<int> ReplaceGuideAsync(XtreamAccount account, List<LiveChannel> channels, DateTime now, CancellationToken ct)
    {
        // Several channels can share one guide entry (an HD and an SD copy).
        var byGuideId = channels.Where(c => c.GuideId is not null).GroupBy(c => c.GuideId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(c => c.Id).ToList(), StringComparer.Ordinal);
        var from = now - KeptBehind;
        var until = now + KeptAhead;

        var fresh = new List<LiveProgramme>();
        await using (var guide = await client.OpenGuideAsync(account, ct))
        {
            await foreach (var p in XmltvParser.ProgrammesAsync(guide, ct))
            {
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
                    break;
                }
            }
        }

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
        return fresh.Count;
    }

    private static string? Cut(string? text, int length) => text is null || text.Length <= length ? text : text[..length];

    [LoggerMessage(Level = LogLevel.Information, Message = "Live TV: {Channels} channels, {Programmes} programmes in the guide")]
    private partial void LogRefreshed(int channels, int programmes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Live TV refresh failed: {Error}")]
    private partial void LogFailed(string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Live TV guide cut off at {Count} programmes")]
    private partial void LogGuideCut(int count);
}

/// <summary>Asks for the channels and guide to be refreshed now (Settings saved, or Refresh pressed).</summary>
public sealed class LiveTvRefreshSignal
{
    // One waiting request is enough: asking again while one is pending changes nothing.
    private readonly System.Threading.Channels.Channel<bool> _asked = System.Threading.Channels.Channel.CreateBounded<bool>(
        new System.Threading.Channels.BoundedChannelOptions(1) { FullMode = System.Threading.Channels.BoundedChannelFullMode.DropWrite });

    public void Request() => _asked.Writer.TryWrite(true);

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
    public static readonly TimeSpan Every = TimeSpan.FromHours(6);
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var asked = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (asked || await DueAsync(stoppingToken))
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<LiveTvGuide>().RefreshAsync(stoppingToken);
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                LogFailed(ex);
            }

            asked = await signal.WaitAsync(Tick, stoppingToken);
        }
    }

    private async Task<bool> DueAsync(CancellationToken ct)
    {
        if (options.CurrentValue.LiveTv?.Account is null)
        {
            return false;
        }

        var last = status.RefreshedAt;
        if (last is null)
        {
            // After a restart: the channels' own stamp says when the last refresh was.
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            last = await db.LiveChannels.MaxAsync(c => (DateTime?)c.UpdatedAt, ct);
        }

        return last is null || clock.GetUtcNow().UtcDateTime - last > Every;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Refreshing Live TV failed")]
    private partial void LogFailed(Exception exception);
}
