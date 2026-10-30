using System.Collections.Concurrent;
using System.Threading.Channels;
using Flexarr.Api.Playback;
using Flexarr.Core.Data;

namespace Flexarr.Api.Devices;

/// <summary>What a device is playing now (null: nothing), sent as it changes.</summary>
public sealed record DeviceNowPlayingDto(string DeviceId, NowPlayingDto? NowPlaying);

/// <summary>
/// Tells remotes when what a device is playing changes: it starts, pauses, seeks, moves on, changes track or stops.
/// A phone asks for these on its command stream (<c>nowPlaying=true</c>) and keeps its Playing on TV bar and remote
/// in step without asking again and again. Changes are noticed on the request path and filled in off it, by
/// <see cref="NowPlayingPublisher"/>.
/// </summary>
public sealed class NowPlayingFeed
{
    private readonly Channel<string> _changed = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<Listener, byte> _listeners = new();
    private readonly ConcurrentDictionary<string, int> _followed = new(StringComparer.Ordinal);

    /// <summary>One stream asking for changes. Disposing it (the request ended) stops them.</summary>
    public sealed class Listener : IDisposable
    {
        private readonly NowPlayingFeed _owner;

        internal Listener(NowPlayingFeed owner) => _owner = owner;

        internal Channel<DeviceNowPlayingDto> Channel { get; } =
            System.Threading.Channels.Channel.CreateBounded<DeviceNowPlayingDto>(new BoundedChannelOptions(64) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });

        public ChannelReader<DeviceNowPlayingDto> Changes => Channel.Reader;

        public void Dispose()
        {
            _owner._listeners.TryRemove(this, out _);
            Channel.Writer.TryComplete();
        }
    }

    public Listener Listen()
    {
        var listener = new Listener(this);
        _listeners[listener] = 0;
        return listener;
    }

    internal bool HasListeners => !_listeners.IsEmpty;

    /// <summary>Something besides the streams wants to hear about <paramref name="deviceId"/> (a phone's Live Activity, see <see cref="LiveActivities"/>).</summary>
    public void Follow(string deviceId) => _followed.AddOrUpdate(deviceId, 1, (_, count) => count + 1);

    public void Unfollow(string deviceId)
    {
        if (_followed.AddOrUpdate(deviceId, 0, (_, count) => count - 1) <= 0)
        {
            _followed.TryRemove(new KeyValuePair<string, int>(deviceId, 0));
        }
    }

    internal ChannelReader<string> Changed => _changed.Reader;

    /// <summary>A playback started on a device, reported progress, or ended.</summary>
    public void Notice(PlaybackSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.SignInId is { } device && (HasListeners || _followed.ContainsKey(device)))
        {
            _changed.Writer.TryWrite(device);
        }
    }

    internal void Publish(DeviceNowPlayingDto change)
    {
        foreach (var listener in _listeners.Keys)
        {
            listener.Channel.Writer.TryWrite(change);
        }
    }
}

/// <summary>Works out what a device that changed is playing now (as <c>GET /devices</c> would say) and sends it out.</summary>
public sealed partial class NowPlayingPublisher(NowPlayingFeed feed, PlaybackSessions sessions, LiveActivities activities, IServiceScopeFactory scopes,
    ILogger<NowPlayingPublisher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (await feed.Changed.WaitToReadAsync(stoppingToken))
        {
            // Several reports at once (a seek, then the progress after it) come to one look per device.
            var devices = new HashSet<string>(StringComparer.Ordinal);
            while (feed.Changed.TryRead(out var device))
            {
                devices.Add(device);
            }

            foreach (var device in devices)
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
                    var change = new DeviceNowPlayingDto(device, await DeviceEndpoints.NowPlayingAsync(device, sessions, db, stoppingToken));
                    feed.Publish(change);
                    await activities.ChangedAsync(change, scope.ServiceProvider.GetRequiredService<Notifications.PushRelay>(), stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogPublishFailed(ex, device);
                }
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't send what device {Device} is playing")]
    private partial void LogPublishFailed(Exception ex, string device);
}
