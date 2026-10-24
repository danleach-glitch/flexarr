using System.Threading.Channels;

namespace Flexarr.Core.Scanning;

/// <summary>
/// A request to (re)scan part of a library. <see cref="Path"/> may be a file or a folder.
/// Metadata for new or stale items is refreshed afterwards; <see cref="ForceMetadata"/> refreshes everything.
/// <see cref="Deleted"/>: Sonarr, Radarr or Lidarr said they deleted what's at the path, so its files going is expected
/// even when that leaves the library folder empty (which otherwise looks like a drive that hasn't mounted).
/// </summary>
public sealed record LibraryScanRequest(string Path, string Reason, bool ForceMetadata = false, bool Deleted = false);

public interface ILibraryScanQueue
{
    ValueTask EnqueueAsync(LibraryScanRequest request, CancellationToken cancellationToken = default);

    bool TryDequeue(out LibraryScanRequest request);

    IAsyncEnumerable<LibraryScanRequest> ReadAllAsync(CancellationToken cancellationToken);
}

public sealed class LibraryScanQueue : ILibraryScanQueue
{
    private readonly Channel<LibraryScanRequest> _channel =
        Channel.CreateUnbounded<LibraryScanRequest>(new UnboundedChannelOptions { SingleReader = true });

    public ValueTask EnqueueAsync(LibraryScanRequest request, CancellationToken cancellationToken = default) =>
        _channel.Writer.WriteAsync(request, cancellationToken);

    public bool TryDequeue(out LibraryScanRequest request) => _channel.Reader.TryRead(out request!);

    public IAsyncEnumerable<LibraryScanRequest> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}
