namespace Flexarr.Core.Scanning;

/// <summary>What the scanner is doing right now, for the UI. Updated by one scan at a time.</summary>
public sealed class ScanProgress
{
    private readonly Lock _lock = new();
    private Snapshot? _current;

    public sealed record Snapshot(Guid LibraryId, string Path, int Total, int Processed);

    public Snapshot? Current
    {
        get
        {
            lock (_lock)
            {
                return _current;
            }
        }
    }

    public void Begin(Guid libraryId, string path, int total)
    {
        lock (_lock)
        {
            _current = new Snapshot(libraryId, path, total, 0);
        }
    }

    public void Advance(int count)
    {
        lock (_lock)
        {
            if (_current is not null)
            {
                _current = _current with { Processed = _current.Processed + count };
            }
        }
    }

    public void End()
    {
        lock (_lock)
        {
            _current = null;
        }
    }
}
