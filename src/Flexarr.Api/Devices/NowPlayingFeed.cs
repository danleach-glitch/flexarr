using System.Collections.Concurrent;
using System.Threading.Channels;
using Flexarr.Api.Authentication;
using Flexarr.Api.Playback;
using Flexarr.Core.Data;

namespace Flexarr.Api.Devices;

/// <summary>What a device is playing now (null: nothing), sent as it changes.</summary>
/// <param name="Online">Flexarr is open on it and listening, as <see cref="DeviceDto.Online"/>.</param>
public sealed record DeviceNowPlayingDto(string DeviceId, NowPlayingDto? NowPlaying, bool? Online = null);

/// <summary>
/// Tells remotes when what a device is playing changes: it starts, pauses, seeks, moves on, changes track or stops, or
/// the device opens or closes Flexarr. A phone asks for these on its command stream (<c>nowPlaying=true</c>) and keeps
/// its Playing on TV bar and remote in step without asking again and again. Changes are noticed on the request path and
/// filled in off it, by <see cref="NowPlayingPublisher"/>.
/// </summary>
public sealed class NowPlayingFeed
{
    private readonly Channel<string> _changed = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<Listener, byte> _listeners = new();
    private readonly ConcurrentDictionary<string, int> _followed = new(StringComparer.Ordinal);

    public NowPlayingFeed(DeviceCommands commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        commands.OnlineChanged += Look;
    }

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

    internal ChannelReader<string> Changed => _changed.Reader;

    /// <summary>Something besides the streams wants to hear about <paramref name="deviceId"/> (a phone's Live Activity, see <see cref="LiveActivities"/>).</summary>
    public void Follow(string deviceId) => _followed.AddOrUpdate(deviceId, 1, (_, count) => count + 1);

    public void Unfollow(string deviceId)
    {
        if (_followed.AddOrUpdate(deviceId, 0, (_, count) => count - 1) <= 0)
        {
            _followed.TryRemove(new KeyValuePair<string, int>(deviceId, 0));
        }
    }

    /// <summary>A playback started on a device, reported progress, went quiet or ended.</summary>
    public void Notice(PlaybackSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.SignInId is { } device)
        {
            Look(device);
        }
    }

    /// <summary>What <paramref name="deviceId"/> is playing is to be looked at again (and sent, if anyone listens).</summary>
    public void Look(string deviceId)
    {
        if (HasListeners || _followed.ContainsKey(deviceId))
        {
            _changed.Writer.TryWrite(deviceId);
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

/// <summary>
/// Works out what a device that changed is playing now (as <c>GET /devices</c> would say, before hiding titles per
/// listener) and sends it out, to the streams and to the Live Activities following it. Only devices: a browser playing
/// isn't a TV to control.
/// </summary>
public sealed partial class NowPlayingPublisher(NowPlayingFeed feed, PlaybackSessions sessions, LiveActivities activities, DeviceCommands commands, SignIns signIns,
    IServiceScopeFactory scopes, ILogger<NowPlayingPublisher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (await feed.Changed.WaitToReadAsync(stoppingToken))
        {
            // Several reports at once (a seek, then the progress after it) come to one look per device.
            var changed = new HashSet<string>(StringComparer.Ordinal);
            while (feed.Changed.TryRead(out var device))
            {
                changed.Add(device);
            }

            var devices = signIns.List().Where(s => s.Kind == SignInKind.Device).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var device in changed.Where(devices.Contains))
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
                    var change = new DeviceNowPlayingDto(device, await DeviceEndpoints.NowPlayingAsync(device, sessions, db, stoppingToken, cached: true),
                        commands.IsOnline(device));
                    feed.Publish(change);
                    activities.Changed(change);
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
