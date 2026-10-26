using System.Collections.Concurrent;
using Flexarr.Streaming.Live;

namespace Flexarr.Api.LiveTv;

/// <summary>Someone watching a channel. <see cref="Id"/> is the random capability in their playlist's address.</summary>
public sealed record LiveViewer(string Id, Guid ChannelId, string ChannelName, string ProfileId, string? SignInId, LiveSession Session)
{
    public DateTime StartedAt { get; } = DateTime.UtcNow;
}

/// <summary>Who's watching which channel.</summary>
public sealed class LiveTvViewers
{
    private readonly ConcurrentDictionary<string, LiveViewer> _viewers = new(StringComparer.Ordinal);

    /// <summary>Why a viewer's channel was stopped for them (a recording needed the stream), for their player to say.</summary>
    private readonly ConcurrentDictionary<string, (string Reason, DateTime At)> _ended = new(StringComparer.Ordinal);

    public IReadOnlyCollection<LiveViewer> All => [.. _viewers.Values];

    /// <summary>Takes the viewer off, keeping why for a few minutes.</summary>
    public void End(string id, string reason)
    {
        _viewers.TryRemove(id, out _);
        _ended[id] = (reason, DateTime.UtcNow);
        foreach (var old in _ended.Where(e => DateTime.UtcNow - e.Value.At > TimeSpan.FromMinutes(10)).ToList())
        {
            _ended.TryRemove(old.Key, out _);
        }
    }

    public string? EndedBecause(string id) => _ended.TryGetValue(id, out var ended) ? ended.Reason : null;

    public void Add(LiveViewer viewer)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        _viewers[viewer.Id] = viewer;
    }

    public LiveViewer? Get(string id) => _viewers.TryGetValue(id, out var viewer) ? viewer : null;

    public LiveViewer? Remove(string id) => _viewers.TryRemove(id, out var viewer) ? viewer : null;
}

/// <summary>Lets go of viewers who stopped asking for their stream, and stops channels nobody is watching.</summary>
public sealed partial class LiveTvSweeper(LiveStreams streams, LiveTvViewers viewers, ILogger<LiveTvSweeper> logger) : BackgroundService
{
    /// <summary>A viewer not heard from for this long has gone (players fetch the playlist every few seconds).</summary>
    public static readonly TimeSpan ViewerIdle = TimeSpan.FromSeconds(45);

    /// <summary>A channel nobody watches keeps going this long, so going back to it keeps its rewind window.</summary>
    public static readonly TimeSpan Linger = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    foreach (var gone in await streams.SweepAsync(ViewerIdle, Linger))
                    {
                        viewers.Remove(gone);
                    }

                    // Viewers whose channel was stopped (it failed) go too.
                    foreach (var orphan in viewers.All.Where(v => streams.Find(v.Session.ChannelKey) != v.Session))
                    {
                        viewers.Remove(orphan.Id);
                    }
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    LogFailed(ex);
                }
            }
        }
        finally
        {
            await streams.DisposeAsync();
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Sweeping live channels failed")]
    private partial void LogFailed(Exception exception);
}
