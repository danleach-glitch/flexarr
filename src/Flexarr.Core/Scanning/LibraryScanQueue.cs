using System.Threading.Channels;

namespace Flexarr.Core.Scanning;

/// <summary>
/// A request to (re)scan part of a library. <see cref="Path"/> may be a file or a folder.
/// Metadata for new or stale items is refreshed afterwards; <see cref="ForceMetadata"/> reads what's at the path afresh
/// (for a whole library folder, every title in it).
/// <see cref="Deleted"/>: Sonarr, Radarr or Lidarr said they deleted what's at the path, so its files going is expected
/// even when that leaves the library folder empty (which otherwise looks like a drive that hasn't mounted).
/// <see cref="TmdbId"/>, <see cref="TvdbId"/>: the film's or show's id as the webhook gave it, so a title new to Flexarr
/// can have its details fetched alone rather than with the whole Radarr or Sonarr library.
/// <see cref="LastChange"/>: when the folder watcher last heard of a change here; a scan of it that started after that has seen it.
/// <see cref="OnlySubtitles"/>: only subtitle files changed, so the title's details needn't be looked up.
/// <see cref="Retries"/>: how many times this scan has failed and been queued again.
/// </summary>
public sealed record LibraryScanRequest(string Path, string Reason, bool ForceMetadata = false, bool Deleted = false, int? TmdbId = null, int? TvdbId = null, DateTimeOffset? LastChange = null, bool OnlySubtitles = false, int Retries = 0);

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
