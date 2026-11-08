namespace Flexarr.Api.Configuration;

/// <summary>
/// Keeps what the server stores to the account it runs as. Left as they're made, the database, the logs and the cache
/// can be read by every account on the machine: the database holds the PINs' hashes, Trakt sign-ins and the phones'
/// notification keys, a new server's log holds its setup code, and the cache shows what's being played.
/// config.json, devices.json and invites.json are written this way already. The data folder itself is closed too
/// (Docker's /config when that's it), so nothing in it is reachable whatever it was made as.
/// </summary>
public static partial class OwnerOnly
{
    private const UnixFileMode Others = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    /// <summary>
    /// The data folder, the database (and its write-ahead files, which SQLite then makes like it), and the log and
    /// cache folders where they're Flexarr's own: inside the data folder, or made by the server. A log or cache folder
    /// set to one somewhere else that was there already is left alone (it may be shared: a scratch drive), with a line
    /// in the log saying so; so is a data folder that's the account's home or a drive's root.
    /// <paramref name="installRoot"/> is a Mac install's ~/.flexarr, which is all Flexarr's and closed whole.
    /// </summary>
    public static void Apply(string dataDirectory, string logDirectory, string cacheDirectory, string? installRoot, ILogger? logger = null)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            Close(Path.Combine(dataDirectory, "flexarr.db" + suffix));
        }

        var dataFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory));
        var data = dataFolder + Path.DirectorySeparatorChar;
        if (string.Equals(dataFolder, Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)), StringComparison.Ordinal)
            || string.Equals(dataFolder, Path.TrimEndingDirectorySeparator(Path.GetPathRoot(dataFolder) ?? ""), StringComparison.Ordinal))
        {
            if (logger is not null)
            {
                LogDataFolderLeftOpen(logger, dataFolder);
            }
        }
        else
        {
            Close(dataFolder);
        }

        // A folder set to nothing ("CacheDirectory": "") is the one the server runs from, and one set to the data folder
        // itself (with a slash on the end, say) isn't inside it: neither is Flexarr's own to close, nor a reason not to start.
        foreach (var folder in new[] { logDirectory, cacheDirectory }.Where(f => !string.IsNullOrWhiteSpace(f)
            && !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(f)), dataFolder, StringComparison.Ordinal)))
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            // A Mac install's logs sit beside its data, in ~/.flexarr: Flexarr's own too.
            var inside = full.StartsWith(data, StringComparison.Ordinal)
                || installRoot is not null && full.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot)) + Path.DirectorySeparatorChar, StringComparison.Ordinal);
            if (!inside && Directory.Exists(folder))
            {
                // Set to a folder that was there already: perhaps shared, so not this server's to close.
                if (logger is not null && IsOpen(folder))
                {
                    LogFolderLeftOpen(logger, folder);
                }

                continue;
            }

            if (!inside && !Directory.Exists(Path.GetDirectoryName(full)))
            {
                continue; // somewhere that isn't there yet (a drive not mounted): made when it's first used, as before
            }

            try
            {
                // Made now rather than at the first log line or stream, so it's never there open to everyone.
                Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
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

    /// <summary>Whether anyone but the owner may get at <paramref name="path"/>.</summary>
    private static bool IsOpen(string path)
    {
        try
        {
            return !OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & Others) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
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

    [LoggerMessage(Level = LogLevel.Information, Message = "The data folder {Folder} is left open to the machine's other accounts as it is: it's this account's home or a drive's root")]
    private static partial void LogDataFolderLeftOpen(ILogger logger, string folder);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Folder} is left open to the machine's other accounts as it is: it was there before Flexarr used it")]
    private static partial void LogFolderLeftOpen(ILogger logger, string folder);
}
