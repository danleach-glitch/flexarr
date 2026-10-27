using System.Collections.Concurrent;
using System.Threading.Channels;
using Serilog.Core;
using Serilog.Events;

namespace Flexarr.Api.Logging;

/// <summary>
/// Hands new entries to the Logs pages watching live. With nobody watching it does nothing; a watcher that falls
/// behind loses its oldest unsent entries rather than holding up the server.
/// </summary>
public sealed class LogFeed : ILogEventSink
{
    private readonly Serilog.Formatting.ITextFormatter _formatter = LogFormats.Json();
    private readonly ConcurrentDictionary<Channel<LogEntry>, byte> _watchers = new();
    private long _sequence;

    public void Emit(LogEvent logEvent)
    {
        if (_watchers.IsEmpty)
        {
            return;
        }

        using var line = new StringWriter();
        _formatter.Format(logEvent, line);
        if (LogEntry.Parse(line.ToString(), $"live-{Interlocked.Increment(ref _sequence)}") is not { } entry)
        {
            return;
        }

        foreach (var watcher in _watchers.Keys)
        {
            watcher.Writer.TryWrite(entry);
        }
    }

    public Watcher Watch() => new(this);

    public sealed class Watcher : IDisposable
    {
        private readonly LogFeed _feed;
        private readonly Channel<LogEntry> _channel = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

        internal Watcher(LogFeed feed)
        {
            _feed = feed;
            feed._watchers[_channel] = 0;
        }

        public ChannelReader<LogEntry> Entries => _channel.Reader;

        public void Dispose()
        {
            _feed._watchers.TryRemove(_channel, out _);
            _channel.Writer.TryComplete();
        }
    }
}
