using System.Globalization;
using System.Text.RegularExpressions;
using Flexarr.Api.Configuration;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Backups;

public sealed record BackupInfo(string Name, BackupKind Kind, DateTime CreatedAt, long Size, string? FlexarrVersion);

/// <summary>
/// The backups folder: making backups (one at a time), listing them, and keeping only so many of each automatic kind.
/// Backups are named flexarr-backup-yyyyMMdd-HHmmss-kind.zip, so the list needn't open every zip to sort or prune.
/// </summary>
public sealed partial class BackupStore(IOptionsMonitor<FlexarrOptions> options, TimeProvider time, ILogger<BackupStore> logger) : IDisposable
{
    /// <summary>How many of the safety copies taken before an upgrade or a restore are kept.</summary>
    public const int KeepSafetyCopies = 3;

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Each backup's Flexarr version, read once: the folder may be a slow network share.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Path, long Size), string?> _versions = new();

    public string Folder => options.CurrentValue.Backups.ResolvedFolder(options.CurrentValue.DataDirectory);

    /// <summary>The backups in the folder, newest first; none when the folder can't be read (see <see cref="FolderProblem"/>).</summary>
    public IReadOnlyList<BackupInfo> List()
    {
        try
        {
            return ListOrThrow();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return [];
        }
    }

    /// <summary>Why the backups folder can't be used now (a share that isn't mounted, say), or null when it can.</summary>
    public string? FolderProblem()
    {
        try
        {
            var folder = Folder;
            if (options.CurrentValue.Backups.Folder is not null && !Directory.Exists(folder))
            {
                return $"The backups folder {folder} isn’t there. Is its drive connected?";
            }

            if (Directory.Exists(folder))
            {
                _ = Directory.EnumerateFiles(folder).FirstOrDefault();
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return $"Flexarr can’t read the backups folder: {ex.Message}";
        }
    }

    private List<BackupInfo> ListOrThrow()
    {
        var folder = Folder;
        if (!Directory.Exists(folder))
        {
            return [];
        }

        return [.. Directory.EnumerateFiles(folder, "flexarr-backup-*.zip")
            .Select(path => Parse(Path.GetFileName(path)) is { } parsed
                ? Info(path, parsed.Kind, parsed.CreatedAt)
                : null)
            .OfType<BackupInfo>()
            .OrderByDescending(b => b.CreatedAt)];
    }

    private BackupInfo Info(string path, BackupKind kind, DateTime createdAt)
    {
        var size = new FileInfo(path).Length;
        var version = _versions.GetOrAdd((path, size), key => BackupArchive.ReadManifest(key.Path)?.FlexarrVersion);
        return new BackupInfo(Path.GetFileName(path), kind, createdAt, size, version);
    }

    public async Task<BackupInfo> CreateAsync(BackupKind kind, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var folder = Folder;
            var data = options.CurrentValue.DataDirectory;
            if (options.CurrentValue.Backups.Folder is not null && !Directory.Exists(folder))
            {
                // A chosen folder that isn't there is usually a drive or share that isn't mounted. Making it would put
                // the backups on this drive under the share's name (in /Volumes, say), out of sight, until it's full.
                throw new IOException($"The backups folder {folder} isn’t there. Is its drive connected?");
            }

            PrivateFiles.CreateFolder(folder);
            // Room for the copy of the database (which a backup is mostly made of) twice over, while it's zipped.
            var database = new FileInfo(Path.Combine(data, BackupArchive.DatabaseName));
            var needed = (database.Exists ? database.Length : 0) * 2 + (64L << 20);
            if (new DriveInfo(folder).AvailableFreeSpace < needed)
            {
                throw new IOException($"There isn’t room for a backup in {folder}: it needs about {needed / (1024 * 1024):N0} MB free.");
            }

            var now = time.GetUtcNow().UtcDateTime;
            var name = NameFor(kind, now);
            // Two in the same second (a restore straight after "Back up now"): the second waits its turn for a name.
            while (File.Exists(Path.Combine(folder, name)))
            {
                now = now.AddSeconds(1);
                name = NameFor(kind, now);
            }

            var path = Path.Combine(folder, name);
            var manifest = await BackupArchive.CreateAsync(data, path, kind, now, ct);
            LogCreated(kind, path);
            Prune(folder);
            return new BackupInfo(name, kind, now, new FileInfo(path).Length, manifest.FlexarrVersion);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The full path of a backup in the folder, or null when there's no such backup.</summary>
    public string? PathFor(string name)
    {
        if (Parse(name) is null)
        {
            return null;
        }

        var path = Path.Combine(Folder, name);
        return File.Exists(path) ? path : null;
    }

    public bool Delete(string name)
    {
        if (PathFor(name) is not { } path)
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    /// <summary>The copy kept before an upgrade changes the database.</summary>
    public async Task BeforeUpgradeAsync(CancellationToken ct)
    {
        try
        {
            await CreateAsync(BackupKind.PreUpgrade, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Not being able to keep a copy (a backup folder on a share that isn't mounted) mustn't keep the server down.
            LogUpgradeBackupFailed(ex, Folder);
        }
    }

    public void Dispose() => _gate.Dispose();

    /// <summary>When the newest nightly backup was made, if there is one.</summary>
    public DateTime? LastNightly() => List().FirstOrDefault(b => b.Kind == BackupKind.Nightly)?.CreatedAt;

    /// <summary>Keeps the newest nightlies (as many as the setting says) and the newest few safety copies; manual ones stay.</summary>
    private void Prune(string folder)
    {
        var keepNightly = Math.Max(1, options.CurrentValue.Backups.KeepNightly);
        var old = List()
            .GroupBy(b => b.Kind)
            .SelectMany(g => g.Key switch
            {
                BackupKind.Nightly => g.Skip(keepNightly),
                BackupKind.PreUpgrade or BackupKind.PreRestore => g.Skip(KeepSafetyCopies),
                _ => [],
            });
        foreach (var backup in old)
        {
            try
            {
                File.Delete(Path.Combine(folder, backup.Name));
            }
            catch (IOException ex)
            {
                LogPruneFailed(ex, backup.Name);
            }
        }
    }

    public static string NameFor(BackupKind kind, DateTime createdAt) =>
        $"flexarr-backup-{createdAt.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{Slug(kind)}.zip";

    public static (BackupKind Kind, DateTime CreatedAt)? Parse(string name)
    {
        if (BackupName().Match(name) is not { Success: true } match
            || !DateTime.TryParseExact(match.Groups["at"].Value, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at))
        {
            return null;
        }

        var kind = Enum.GetValues<BackupKind>().FirstOrDefault(k => Slug(k) == match.Groups["kind"].Value, (BackupKind)(-1));
        return Enum.IsDefined(kind) ? (kind, at) : null;
    }

    private static string Slug(BackupKind kind) => kind switch
    {
        BackupKind.Nightly => "nightly",
        BackupKind.PreUpgrade => "pre-upgrade",
        BackupKind.PreRestore => "pre-restore",
        _ => "manual",
    };

    [GeneratedRegex(@"^flexarr-backup-(?<at>\d{8}-\d{6})-(?<kind>[a-z-]+)\.zip$")]
    private static partial Regex BackupName();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Restored a backup")]
    public partial void LogRestored();

    [LoggerMessage(Level = LogLevel.Error, Message = "Couldn't back up the database to {Folder} before upgrading it")]
    private partial void LogUpgradeBackupFailed(Exception ex, string folder);

    [LoggerMessage(Level = LogLevel.Information, Message = "Backed up ({Kind}) to {Path}")]
    private partial void LogCreated(BackupKind kind, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't delete the old backup {Name}")]
    private partial void LogPruneFailed(Exception ex, string name);
}

/// <summary>
/// A restore is staged in &lt;DataDirectory&gt;/restore-pending while the server runs, then put in place by the next
/// start, before anything opens the database. Putting it in place is all or nothing: what's there now is moved aside
/// first, and moved back if anything goes wrong.
/// </summary>
public static class PendingRestore
{
    public const string FolderName = "restore-pending";
    private const string ReadyMarker = ".ready";
    private const string PreviousFolder = "restore-previous";

    /// <summary>
    /// A staged restore is for the restart that follows it. One found much later (the server wasn't started again by
    /// anything, and someone started it by hand days on) would quietly undo everything since, so it's dropped.
    /// </summary>
    public static readonly TimeSpan GoodFor = TimeSpan.FromHours(1);

    /// <summary>One restore at a time.</summary>
    public static SemaphoreSlim Gate { get; } = new(1, 1);

    public static string FolderFor(string dataDirectory) => Path.Combine(dataDirectory, FolderName);

    /// <summary>Moves an unpacked, checked backup into place to be restored on the next start.</summary>
    public static void Stage(string dataDirectory, string unpacked, DateTime now)
    {
        var staging = FolderFor(dataDirectory);
        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, recursive: true);
        }

        Directory.Move(unpacked, staging);
        File.WriteAllText(Path.Combine(staging, ReadyMarker), now.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Puts a staged backup in place of the current data. Returns whether it did. If it can't, what was there is put
    /// back, the staged backup is set aside as restore-failed-…, and the server starts as it was: one that couldn't
    /// start would be started again and again by launchd.
    /// </summary>
    public static bool Apply(string dataDirectory, DateTime now)
    {
        var staging = FolderFor(dataDirectory);
        try
        {
            if (!Directory.Exists(staging))
            {
                return false;
            }

            var marker = Path.Combine(staging, ReadyMarker);
            if (!File.Exists(marker) || !long.TryParse(File.ReadAllText(marker), out var ticks) || now - new DateTime(ticks, DateTimeKind.Utc) > GoodFor
                || !File.Exists(Path.Combine(staging, BackupArchive.DatabaseName)))
            {
                // Half-staged (the server stopped mid-way), or left from long ago.
                Console.Error.WriteLine("Flexarr found a backup staged for restore that was unfinished or too old, and didn't restore it.");
                Directory.Delete(staging, recursive: true);
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Flexarr couldn't read the backup staged for restore ({ex.Message}), and didn't restore it.");
            return false;
        }

        var previous = Path.Combine(dataDirectory, PreviousFolder);
        var moved = new List<(string From, string To)>();
        try
        {
            if (Directory.Exists(previous))
            {
                Directory.Delete(previous, recursive: true);
            }

            Directory.CreateDirectory(previous);
            // What's there now goes aside first, the database with its write-ahead log (which would otherwise be played
            // into the restored database).
            string[] database = [BackupArchive.DatabaseName, BackupArchive.DatabaseName + "-wal", BackupArchive.DatabaseName + "-shm"];
            foreach (var name in database.Concat(BackupArchive.Files).Append(BackupArchive.ImagesFolder))
            {
                Move(Path.Combine(dataDirectory, name), Path.Combine(previous, name), moved);
            }

            foreach (var name in BackupArchive.Files.Prepend(BackupArchive.DatabaseName).Append(BackupArchive.ImagesFolder))
            {
                Move(Path.Combine(staging, name), Path.Combine(dataDirectory, name), moved);
            }

            // A file the backup doesn't have (config.json, say) keeps this server's.
            foreach (var name in BackupArchive.Files)
            {
                if (!File.Exists(Path.Combine(dataDirectory, name)) && File.Exists(Path.Combine(previous, name)))
                {
                    File.Copy(Path.Combine(previous, name), Path.Combine(dataDirectory, name));
                }

                // Keys and sign-in tokens: this user's alone.
                if (!OperatingSystem.IsWindows() && File.Exists(Path.Combine(dataDirectory, name)))
                {
                    File.SetUnixFileMode(Path.Combine(dataDirectory, name), UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }

            Directory.Delete(staging, recursive: true);
            Directory.Delete(previous, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Back as it was, newest move first.
            var undone = true;
            foreach (var (from, to) in Enumerable.Reverse(moved))
            {
                try
                {
                    Move(to, from, null);
                }
                catch (Exception undo) when (undo is IOException or UnauthorizedAccessException)
                {
                    undone = false;
                }
            }

            try
            {
                Directory.Move(staging, Path.Combine(dataDirectory, $"restore-failed-{now:yyyyMMdd-HHmmss}"));
            }
            catch (Exception aside) when (aside is IOException or UnauthorizedAccessException)
            {
                // Left in place; it's dropped as too old on a later start.
            }

            Console.Error.WriteLine(undone
                ? $"Flexarr couldn't restore the backup ({ex.Message}). The server is as it was before."
                : $"Flexarr couldn't restore the backup ({ex.Message}), nor put everything back: what was there is in {previous}.");
            return false;
        }
    }

    private static void Move(string from, string to, List<(string, string)>? moved)
    {
        if (File.Exists(from))
        {
            File.Move(from, to, overwrite: true);
        }
        else if (Directory.Exists(from))
        {
            Directory.Move(from, to);
        }
        else
        {
            return;
        }

        moved?.Add((from, to));
    }
}

/// <summary>Stops the server so whatever runs it (launchd, Docker, systemd) starts it again, and a staged restore applies.</summary>
public interface IServerRestart
{
    void Request();
}

public sealed partial class ServerRestart(IHostApplicationLifetime lifetime, ILogger<ServerRestart> logger) : IServerRestart
{
    public void Request()
    {
        LogRestarting();
        _ = Task.Run(async () =>
        {
            // Long enough for the answer to reach the browser.
            await Task.Delay(TimeSpan.FromSeconds(1));
            // Not zero, so a supervisor that only restarts on failure (systemd's Restart=on-failure) starts it again too.
            Environment.ExitCode = 75;
            lifetime.StopApplication();
        });
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Restarting to restore a backup")]
    private partial void LogRestarting();
}

/// <summary>Takes the nightly backup at half past three, and catches up after a night the server was off.</summary>
public sealed partial class BackupWorker(BackupStore store, IOptionsMonitor<FlexarrOptions> options, TimeProvider time, ILogger<BackupWorker> logger)
    : BackgroundService
{
    private static readonly TimeSpan At = new(3, 30, 0);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Out of the way of startup's own work.
            await Task.Delay(TimeSpan.FromMinutes(5), time, stoppingToken);
            if (store.LastNightly() is not { } last || time.GetUtcNow().UtcDateTime - last > TimeSpan.FromHours(24))
            {
                await RunAsync(stoppingToken);
            }


            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(UntilNext(time.GetLocalNow()), time, stoppingToken);
                await RunAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public static TimeSpan UntilNext(DateTimeOffset now)
    {
        var today = new DateTimeOffset(now.Date + At, now.Offset);
        return (today > now ? today : today.AddDays(1)) - now;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        if (!options.CurrentValue.Backups.Nightly)
        {
            return;
        }

        try
        {
            await store.CreateAsync(BackupKind.Nightly, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Whatever went wrong, the server carries on: a background service that throws stops the whole app.
            LogFailed(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The nightly backup failed")]
    private partial void LogFailed(Exception ex);
}
