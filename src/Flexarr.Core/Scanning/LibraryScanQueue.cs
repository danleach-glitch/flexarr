using System.Threading.Channels;

namespace Flexarr.Core.Scanning;

/// <summary>A request to (re)scan part of a library. <see cref="Path"/> may be a file or a folder.</summary>
public sealed record LibraryScanRequest(string Path, string Reason);

public interface ILibraryScanQueue
{
    ValueTask EnqueueAsync(LibraryScanRequest request, CancellationToken cancellationToken = default);

    IAsyncEnumerable<LibraryScanRequest> ReadAllAsync(CancellationToken cancellationToken);
}

public sealed class LibraryScanQueue : ILibraryScanQueue
{
    private readonly Channel<LibraryScanRequest> _channel =
        Channel.CreateUnbounded<LibraryScanRequest>(new UnboundedChannelOptions { SingleReader = true });

    public ValueTask EnqueueAsync(LibraryScanRequest request, CancellationToken cancellationToken = default) =>
        _channel.Writer.WriteAsync(request, cancellationToken);

    public IAsyncEnumerable<LibraryScanRequest> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}
