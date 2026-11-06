namespace Flexarr.Api.Configuration;

/// <summary>
/// Keeps what the server stores to the account it runs as. Left as they're made, the database, the logs and the cache
/// can be read by every account on the machine: the database holds the PINs' hashes, Trakt sign-ins and the phones'
/// notification keys, a new server's log holds its setup code, and the cache shows what's being played.
/// config.json, devices.json and invites.json are written this way already.
/// </summary>
public static class OwnerOnly
{
    private const UnixFileMode Others = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    /// <summary>
    /// The database (and its write-ahead files, which SQLite then makes like it), and the log and cache folders where
    /// they're Flexarr's own, inside the data folder. A log or cache folder set to somewhere else is left alone, as
    /// is the data folder itself: either may be shared for other reasons (a scratch drive, Docker's /config).
    /// <paramref name="installRoot"/> is a Mac install's ~/.flexarr, which is all Flexarr's and closed whole.
    /// </summary>
    public static void Apply(string dataDirectory, string logDirectory, string cacheDirectory, string? installRoot)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            Close(Path.Combine(dataDirectory, "flexarr.db" + suffix));
        }

        var data = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory)) + Path.DirectorySeparatorChar;
        // A folder set to nothing ("CacheDirectory": "") is the one the server runs from, and one set to the data folder
        // itself (with a slash on the end, say) isn't inside it: neither is Flexarr's own to close, nor a reason not to start.
        foreach (var folder in new[] { logDirectory, cacheDirectory }.Where(f => !string.IsNullOrWhiteSpace(f)
            && Path.TrimEndingDirectorySeparator(Path.GetFullPath(f)).StartsWith(data, StringComparison.Ordinal)))
        {
            try
            {
                // Made now rather than at the first log line or stream, so it's never there open to everyone.
                Directory.CreateDirectory(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue; // a drive that isn't there yet, say
            }

            Close(folder);
        }

        if (installRoot is not null)
        {
            Close(installRoot);
        }
    }

    /// <summary>Takes a file or folder away from everyone but its owner, whose own access stays as it was.</summary>
    private static void Close(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows() || !Path.Exists(path))
            {
                return;
            }

            var mode = File.GetUnixFileMode(path);
            if ((mode & Others) != 0)
            {
                File.SetUnixFileMode(path, mode & ~Others);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not ours to change (another account's folder, a read-only mount, a file system without permissions): carry on.
        }
    }
}
