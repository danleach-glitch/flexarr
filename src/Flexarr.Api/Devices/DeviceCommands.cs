using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Threading.Channels;

namespace Flexarr.Api.Devices;

public enum DeviceCommandKind
{
    Play,
    Pause,
    Resume,
    Stop,

    /// <summary>Put on a Live TV channel (<see cref="DeviceCommand.ChannelId"/>).</summary>
    Watch,

    /// <summary>Change the device's own sound settings (<see cref="DeviceCommand.Sound"/>): dialogue boost, night mode, speakers.</summary>
    Sound,

    /// <summary>Go to <see cref="DeviceCommand.Seconds"/> into what's playing.</summary>
    Seek,

    /// <summary>Jump <see cref="DeviceCommand.Seconds"/> forward (or back, negative).</summary>
    Skip,

    /// <summary>On to the next episode.</summary>
    Next,

    /// <summary>Back to the start, or (near it already) the episode before.</summary>
    Previous,

    /// <summary>Skip the intro or recap playing, as the button over the picture would.</summary>
    SkipSegment,

    /// <summary>Change to the soundtrack <see cref="DeviceCommand.TrackId"/>.</summary>
    AudioTrack,

    /// <summary>Show the subtitles <see cref="DeviceCommand.TrackId"/>, or none when it's absent.</summary>
    SubtitleTrack,

    /// <summary>Play at <see cref="DeviceCommand.Rate"/> times normal speed.</summary>
    Rate,
}

public enum DialogueBoost
{
    Off,
    Low,
    Medium,
    High,
}

public enum NightMode
{
    Off,
    On,

    /// <summary>Between the hours set on the device.</summary>
    Scheduled,
}

public enum Speakers
{
    Auto,
    Stereo,
    Surround,
}

/// <summary>Sound settings for a device to take. Each one that's absent stays as the device has it.</summary>
public sealed record DeviceSound(DialogueBoost? DialogueBoost = null, NightMode? NightMode = null, Speakers? Speakers = null)
{
    public bool IsEmpty => DialogueBoost is null && NightMode is null && Speakers is null;
}

/// <summary>
/// Something for a device (an Apple TV, an iPhone) to do, asked for from another device, the web app or an assistant:
/// "play this on the living room TV".
/// </summary>
/// <param name="ItemId">With <see cref="DeviceCommandKind.Play"/>, the film, episode or track to start (already resolved: never a show), or with <paramref name="Shuffle"/> the show itself.</param>
/// <param name="FromBeginning">Start over rather than carry on from where it was left.</param>
/// <param name="SentBy">Who asked ("Dan's iPhone"), for the device to show.</param>
/// <param name="Shuffle">The show's episodes in a random order, one after another.</param>
/// <param name="ChannelId">With <see cref="DeviceCommandKind.Watch"/>, the Live TV channel to put on.</param>
/// <param name="ProfileId">With <see cref="DeviceCommandKind.Play"/>, whose to play it as: the device switches to that profile first. Absent: whoever is watching there.</param>
/// <param name="Sound">With <see cref="DeviceCommandKind.Sound"/>, the settings to change.</param>
/// <param name="Seconds">With <see cref="DeviceCommandKind.Seek"/>, where to go; with <see cref="DeviceCommandKind.Skip"/>, how far.</param>
/// <param name="TrackId">With <see cref="DeviceCommandKind.AudioTrack"/> or <see cref="DeviceCommandKind.SubtitleTrack"/>, the player's id for the track.</param>
/// <param name="Rate">With <see cref="DeviceCommandKind.Rate"/>, the speed.</param>
public sealed record DeviceCommand(string Id, DeviceCommandKind Kind, Guid? ItemId, bool FromBeginning, string? SentBy, DateTime SentAt, bool Shuffle = false,
    Guid? ChannelId = null, string? ProfileId = null, DeviceSound? Sound = null, double? Seconds = null, string? TrackId = null, double? Rate = null)
{
    /// <summary>What every device that listens has always taken; the rest only a device that says it does.</summary>
    public static readonly IReadOnlySet<DeviceCommandKind> Basic = new HashSet<DeviceCommandKind>
    {
        DeviceCommandKind.Play, DeviceCommandKind.Pause, DeviceCommandKind.Resume, DeviceCommandKind.Stop, DeviceCommandKind.Watch, DeviceCommandKind.Sound,
    };

    public static string NewId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
}

/// <summary>
/// Hands commands to devices. A device with Flexarr open keeps one request to <c>GET /devices/commands</c> going and
/// takes commands as they come; a device that isn't listening (the TV is asleep, the app is in the background) has
/// the latest command kept for it for a couple of minutes, so "play the next episode" said just before the app is
/// opened still plays.
/// </summary>
public sealed class DeviceCommands(TimeProvider clock)
{
    /// <summary>How long a command waits for a device that isn't listening to open Flexarr.</summary>
    public static readonly TimeSpan WaitLimit = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<string, Listener> _listeners = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DeviceCommand> _waiting = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTime> _lastSeen = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IReadOnlySet<DeviceCommandKind>> _accepts = new(StringComparer.Ordinal);

    /// <summary>One device's open connection. Disposing it (the request ended) takes the device offline.</summary>
    public sealed class Listener : IDisposable
    {
        private readonly DeviceCommands _owner;

        internal Listener(DeviceCommands owner, string deviceId)
        {
            _owner = owner;
            DeviceId = deviceId;
        }

        public string DeviceId { get; }

        internal Channel<DeviceCommand> Channel { get; } = System.Threading.Channels.Channel.CreateUnbounded<DeviceCommand>(new UnboundedChannelOptions { SingleReader = true });

        /// <summary>Completes when a newer connection from the same device takes over.</summary>
        public ChannelReader<DeviceCommand> Commands => Channel.Reader;

        public void Dispose() => _owner.Detach(this);
    }

    /// <summary>
    /// Starts listening for <paramref name="deviceId"/>. A connection the device already had is ended (its stream
    /// completes), and a command that was waiting for the device is handed over at once. <paramref name="accepts"/>:
    /// the commands beyond <see cref="DeviceCommand.Basic"/> it says it takes (null: an app from before it said).
    /// </summary>
    public Listener Listen(string deviceId, IEnumerable<DeviceCommandKind>? accepts = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        if (accepts is not null)
        {
            _accepts[deviceId] = accepts.ToHashSet();
        }
        else
        {
            _accepts.TryRemove(deviceId, out _);
        }

        var listener = new Listener(this, deviceId);
        var now = clock.GetUtcNow().UtcDateTime;
        var previous = _listeners.AddOrUpdate(deviceId, listener, (_, old) =>
        {
            old.Channel.Writer.TryComplete();
            return listener;
        });
        _ = previous;
        _lastSeen[deviceId] = now;
        if (_waiting.TryRemove(deviceId, out var waiting) && now - waiting.SentAt <= WaitLimit)
        {
            listener.Channel.Writer.TryWrite(waiting);
        }

        return listener;
    }

    private void Detach(Listener listener)
    {
        _listeners.TryRemove(new KeyValuePair<string, Listener>(listener.DeviceId, listener));
        _lastSeen[listener.DeviceId] = clock.GetUtcNow().UtcDateTime;
        listener.Channel.Writer.TryComplete();
    }

    /// <summary>Flexarr is open on the device and would take a command now.</summary>
    public bool IsOnline(string deviceId) => _listeners.ContainsKey(deviceId);

    /// <summary>
    /// The commands the device said it takes when it last listened, beyond the basic ones; null when it didn't say (an
    /// app from before 0.56, or it hasn't listened since the server started).
    /// </summary>
    public IReadOnlySet<DeviceCommandKind>? Accepts(string deviceId) => _accepts.TryGetValue(deviceId, out var kinds) ? kinds : null;

    /// <summary>The device would know what to do with <paramref name="kind"/>.</summary>
    public bool Takes(string deviceId, DeviceCommandKind kind) => DeviceCommand.Basic.Contains(kind) || Accepts(deviceId)?.Contains(kind) == true;

    /// <summary>When the device last connected or disconnected; null if it never has since the server started.</summary>
    public DateTime? LastSeenAt(string deviceId) => _lastSeen.TryGetValue(deviceId, out var at) ? at : null;

    /// <summary>
    /// Sends a command. True when the device is listening and took it; false when it's kept for the device to pick up
    /// when Flexarr is next opened on it (within <see cref="WaitLimit"/>). Only the latest waits: a Play after a Pause
    /// is what's meant. With <paramref name="keep"/> false a command for a device that isn't listening is dropped, so it
    /// can't push out a Play that's waiting.
    /// </summary>
    public bool Send(string deviceId, DeviceCommand command, bool keep = true)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        ArgumentNullException.ThrowIfNull(command);
        if (_listeners.TryGetValue(deviceId, out var listener) && listener.Channel.Writer.TryWrite(command))
        {
            return true;
        }

        if (keep)
        {
            _waiting[deviceId] = command;
        }

        return false;
    }

    /// <summary>A device signed out: nothing waits for it any more.</summary>
    public void Forget(string deviceId)
    {
        _waiting.TryRemove(deviceId, out _);
        _accepts.TryRemove(deviceId, out _);
        if (_listeners.TryRemove(deviceId, out var listener))
        {
            listener.Channel.Writer.TryComplete();
        }
    }
}
