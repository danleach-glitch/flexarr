using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flexarr.Api.Authentication;
using Flexarr.Api.Configuration;
using Flexarr.Core.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Api.Backups;

public enum BackupKind
{
    Manual,
    Nightly,
    PreUpgrade,
    PreRestore,
}

/// <summary>What a backup says about itself (manifest.json in the zip).</summary>
public sealed record BackupManifest(int Format, BackupKind Kind, DateTime CreatedAt, string FlexarrVersion, string? Migration);

/// <summary>A backup unpacked and checked, ready to restore: what's in it, or why it can't be restored.</summary>
public sealed record BackupContents(BackupManifest Manifest, int Profiles, int Libraries, int Titles, string? Problem);

/// <summary>
/// A backup is a zip of everything in the data folder that can't be made again: a snapshot of the database, config.json
/// (settings and keys), devices.json (signed-in browsers and devices) and the profile pictures. The cache (artwork,
/// subtitles, previews) is left out: it fills itself again.
/// </summary>
public static class BackupArchive
{
    public const int Format = 1;
    public const string ManifestName = "manifest.json";
    public const string DatabaseName = "flexarr.db";
    public const string ImagesFolder = "profile-images";

    /// <summary>The files beside the database a backup carries, all from the top of the data folder.</summary>
    /// <remarks>Family invites' guests.json (who is a guest) goes too; invites.json doesn't: its keys work once or have run out.</remarks>
    public static readonly string[] Files = [ServerConfigStore.FileName, SignIns.FileName, Security.GuestInvites.GuestsFileName, Security.GuestInvites.AddressesFileName];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public static async Task<BackupManifest> CreateAsync(string dataDirectory, string destination, BackupKind kind, DateTime now, CancellationToken ct)
    {
        var partial = destination + ".partial";
        // The copy is made beside the database, on this machine's own drive, and only the zip goes to the backups
        // folder: on a network share the database then crosses the network once, not three times.
        var snapshot = Path.Combine(dataDirectory, $".backup-snapshot-{Guid.NewGuid():N}.db");
        try
        {
            // VACUUM INTO copies the database as one consistent moment, while the server goes on using it. It writes
            // into an empty file that already exists, so the copy is private from the start (it holds the PIN hashes).
            await PrivateFiles.Create(snapshot).DisposeAsync();
            await using (var source = Open(Path.Combine(dataDirectory, DatabaseName)))
            {
                await source.OpenAsync(ct);
                await using var vacuum = source.CreateCommand();
                vacuum.CommandText = "VACUUM INTO $path";
                vacuum.Parameters.AddWithValue("$path", snapshot);
                await vacuum.ExecuteNonQueryAsync(ct);
            }

            var manifest = new BackupManifest(Format, kind, now, AppVersion.Current, await LastMigrationAsync(snapshot, ct));
            await using (var file = PrivateFiles.Create(partial))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                await using (var entry = await zip.CreateEntry(ManifestName).OpenAsync(ct))
                {
                    await JsonSerializer.SerializeAsync(entry, manifest, Json, ct);
                }

                await zip.CreateEntryFromFileAsync(snapshot, DatabaseName, CompressionLevel.Optimal, ct);
                foreach (var name in Files.Where(n => File.Exists(Path.Combine(dataDirectory, n))))
                {
                    await zip.CreateEntryFromFileAsync(Path.Combine(dataDirectory, name), name, CompressionLevel.Optimal, ct);
                }

                var images = Path.Combine(dataDirectory, ImagesFolder);
                if (Directory.Exists(images))
                {
                    foreach (var image in Directory.EnumerateFiles(images))
                    {
                        // Pictures are compressed already.
                        await zip.CreateEntryFromFileAsync(image, $"{ImagesFolder}/{Path.GetFileName(image)}", CompressionLevel.NoCompression, ct);
                    }
                }
            }

            File.Move(partial, destination);
            return manifest;
        }
        finally
        {
            File.Delete(partial);
            File.Delete(snapshot);
        }
    }

    /// <summary>The manifest of a backup zip, or null when the file isn't one.</summary>
    public static BackupManifest? ReadManifest(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            return zip.GetEntry(ManifestName) is { Length: <= MaxManifestBytes } entry ? ReadManifest(entry) : null;
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    private const long MaxManifestBytes = 64 * 1024;
    private const long MaxSmallFileBytes = 16 * 1024 * 1024;
    private const int MaxEntries = 10_000;

    /// <summary>
    /// How far one entry may expand. A database snapshot compresses about five-fold; a zip bomb (a file of zeros) a
    /// thousand-fold.
    /// </summary>
    private const long MaxExpansion = 100;

    /// <summary>Left free on the drive after unpacking, so a big backup can't fill it.</summary>
    private const long SpareBytes = 1L << 30;

    /// <summary>
    /// Unpacks a backup into <paramref name="folder"/> and checks it all before anything is restored: the database is
    /// whole, from a version of Flexarr this one knows, brought up to this version's design and checked against it
    /// table by table, with nothing in it Flexarr didn't put there; the settings can be read. Only the names a backup
    /// has are unpacked, each no bigger than it says it is.
    /// </summary>
    /// <param name="knownMigrations">This version's database migrations.</param>
    /// <exception cref="InvalidDataException">The file isn't a Flexarr backup, or is too big to unpack here.</exception>
    public static async Task<BackupContents> ExtractAsync(string zipPath, string folder, IReadOnlyCollection<string> knownMigrations, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(knownMigrations);
        PrivateFiles.CreateFolder(folder);
        BackupManifest? manifest = null;
        try
        {
            // Counted from the zip's own directory before it's read: reading a directory of millions of entries is
            // itself enough to run out of memory.
            if (EntryCount(zipPath) is not (> 0 and <= MaxEntries))
            {
                throw new InvalidDataException("This isn’t a Flexarr backup.");
            }

            using var zip = ZipFile.OpenRead(zipPath);

            var wanted = zip.Entries.Select(e => (Entry: e, Target: TargetFor(e.FullName))).Where(e => e.Target is not null).ToList();
            var total = wanted.Sum(e => e.Entry.Length);
            if (wanted.Any(e => e.Entry.Length > MaxSmallFileBytes && e.Entry.Length > e.Entry.CompressedLength * MaxExpansion)
                || wanted.Any(e => e.Target != DatabaseName && e.Entry.Length > MaxSmallFileBytes))
            {
                throw new InvalidDataException("This file unpacks to far more than a backup would. It isn’t one Flexarr made.");
            }

            if (new DriveInfo(Path.GetFullPath(folder)).AvailableFreeSpace - total < SpareBytes)
            {
                throw new InvalidDataException($"There isn’t room to unpack this backup: it needs {total / (1024 * 1024):N0} MB and 1 GB to spare.");
            }

            foreach (var (entry, target) in wanted)
            {
                if (target == ManifestName)
                {
                    manifest = entry.Length <= MaxManifestBytes ? ReadManifest(entry) : null;
                    continue;
                }

                var path = Path.Combine(folder, target!);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await using var input = await entry.OpenAsync(ct);
                await using var output = PrivateFiles.Create(path);
                // No more than the entry says it holds, whatever its compressed data turns out to be.
                await CopyAtMostAsync(input, output, entry.Length, ct);
            }
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or IOException and not FileNotFoundException and not DirectoryNotFoundException)
        {
            throw new InvalidDataException("This isn’t a Flexarr backup, or it’s damaged.", ex);
        }

        var database = Path.Combine(folder, DatabaseName);
        if (manifest is null || !File.Exists(database))
        {
            throw new InvalidDataException("This isn’t a Flexarr backup.");
        }

        if (SettingsProblem(Path.Combine(folder, ServerConfigStore.FileName)) is { } settingsProblem)
        {
            return new BackupContents(manifest, 0, 0, 0, settingsProblem);
        }

        try
        {
            await using var db = Open(database);
            await db.OpenAsync(ct);
            if (await ScalarAsync<string>(db, "PRAGMA quick_check", ct) != "ok")
            {
                return new BackupContents(manifest, 0, 0, 0, "The database in this backup is damaged.");
            }

            // Flexarr's database has only tables and indexes. A trigger or view would be something else's doing, and
            // would run inside the server once restored.
            if (await ScalarAsync<long>(db, "SELECT COUNT(*) FROM sqlite_master WHERE type NOT IN ('table', 'index')", ct) > 0)
            {
                return new BackupContents(manifest, 0, 0, 0, "The database in this backup has things in it Flexarr didn’t make, so it won’t be restored.");
            }

            var migration = await ScalarAsync<string>(db, "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId DESC LIMIT 1", ct);
            if (migration is null || !knownMigrations.Contains(migration))
            {
                return new BackupContents(manifest, 0, 0, 0, migration is null
                    ? "The database in this backup can’t be read."
                    : $"This backup is from a newer Flexarr ({manifest.FlexarrVersion}). Update this server, then restore it.");
            }
        }
        catch (SqliteException)
        {
            return new BackupContents(manifest, 0, 0, 0, "The database in this backup can’t be read.");
        }

        // The unpacked copy is brought up to this version now (so the restart doesn't), and then every table and
        // column this version uses is looked for: a database that only claims to be Flexarr's fails here, not after
        // the restart.
        try
        {
            var options = new DbContextOptionsBuilder<FlexarrDbContext>().UseSqlite($"Data Source={database};Pooling=False").Options;
            await using (var context = new FlexarrDbContext(options))
            {
                await context.Database.MigrateAsync(ct);
                await using var db = Open(database);
                await db.OpenAsync(ct);
                foreach (var check in SchemaChecks(context.Model))
                {
                    await using var command = db.CreateCommand();
                    command.CommandText = check;
                    await using var reader = await command.ExecuteReaderAsync(ct);
                }

                return new BackupContents(manifest,
                    (int)await ScalarAsync<long>(db, "SELECT COUNT(*) FROM Profiles", ct),
                    (int)await ScalarAsync<long>(db, "SELECT COUNT(*) FROM Libraries", ct),
                    (int)await ScalarAsync<long>(db, "SELECT COUNT(*) FROM MediaItems WHERE ParentId IS NULL", ct),
                    null);
            }
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException or DbUpdateException)
        {
            return new BackupContents(manifest, 0, 0, 0, "The database in this backup doesn’t match what Flexarr expects, so it won’t be restored.");
        }
    }

    /// <summary>
    /// How many entries a zip says it has, from its end-of-central-directory record (and the zip64 one, for a zip
    /// that big); null when there's no such record.
    /// </summary>
    public static long? EntryCount(string zipPath)
    {
        using var file = File.OpenRead(zipPath);
        // The record is 22 bytes plus a comment of up to 65,535, at the very end.
        var tail = new byte[(int)Math.Min(file.Length, 22 + 65_535 + 20)];
        file.Seek(-tail.Length, SeekOrigin.End);
        file.ReadExactly(tail);
        for (var i = tail.Length - 22; i >= 0; i--)
        {
            if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) != 0x06054b50)
            {
                continue;
            }

            long count = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i + 10));
            if (count != 0xFFFF)
            {
                return count;
            }

            // Zip64: the locator just before this record says where the real count is.
            if (i < 20 || System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i - 20)) != 0x07064b50)
            {
                return null;
            }

            var offset = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(tail.AsSpan(i - 20 + 8));
            if (offset < 0 || offset + 56 > file.Length)
            {
                return null;
            }

            var record = new byte[56];
            file.Seek(offset, SeekOrigin.Begin);
            file.ReadExactly(record);
            return System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(record) == 0x06064b50
                ? System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(32))
                : null;
        }

        return null;
    }

    /// <summary>Where an entry goes in the unpacked folder: one of a backup's own names, or null to leave it.</summary>
    private static string? TargetFor(string name)
    {
        if (name == ManifestName || name == DatabaseName || Files.Contains(name))
        {
            return name;
        }

        // A picture straight inside profile-images, named as ProfileImages names them (hex digits and .jpg).
        var prefix = ImagesFolder + "/";
        return name.StartsWith(prefix, StringComparison.Ordinal) && name[prefix.Length..] is { Length: > 4 and <= 300 } image
            && image.EndsWith(".jpg", StringComparison.Ordinal) && image[..^4].All(char.IsAsciiHexDigitLower)
            ? Path.Combine(ImagesFolder, image)
            : null;
    }

    private static BackupManifest? ReadManifest(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var limited = new MemoryStream();
        CopyAtMostAsync(stream, limited, MaxManifestBytes, CancellationToken.None).GetAwaiter().GetResult();
        limited.Position = 0;
        var manifest = JsonSerializer.Deserialize<BackupManifest>(limited, Json);
        return manifest is { Format: > 0 and <= Format, FlexarrVersion: { Length: > 0 and <= 64 } } && Enum.IsDefined(manifest.Kind) ? manifest : null;
    }

    /// <summary>Why the settings in a backup couldn't be used, or null when they can (or there are none).</summary>
    private static string? SettingsProblem(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject config)
            {
                return "The settings in this backup can’t be read.";
            }

            return SettingsTransfer.BindProblem(config) is { } problem ? $"The settings in this backup can’t be used: {problem}" : null;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            return "The settings in this backup can’t be read.";
        }
    }

    /// <summary>A query per table that names every column this version maps there; each fails if anything is missing.</summary>
    private static IEnumerable<string> SchemaChecks(Microsoft.EntityFrameworkCore.Metadata.IModel model)
    {
        foreach (var table in model.GetEntityTypes().Where(e => e.GetTableName() is not null).GroupBy(e => e.GetTableName()!))
        {
            var store = Microsoft.EntityFrameworkCore.Metadata.StoreObjectIdentifier.Table(table.Key);
            var columns = table.SelectMany(e => e.GetProperties()).Select(p => p.GetColumnName(store)).OfType<string>().Distinct().ToList();
            if (columns.Count > 0)
            {
                yield return $"SELECT {string.Join(", ", columns.Select(Quote))} FROM {Quote(table.Key)} LIMIT 0";
            }
        }

        static string Quote(string name) => $"\"{name.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    /// <exception cref="InvalidDataException">There's more than <paramref name="limit"/> bytes.</exception>
    private static async Task CopyAtMostAsync(Stream input, Stream output, long limit, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long copied = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            copied += read;
            if (copied > limit)
            {
                throw new InvalidDataException("This file unpacks to more than it says. It isn’t one Flexarr made.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }

    private static async Task<string?> LastMigrationAsync(string database, CancellationToken ct)
    {
        await using var db = Open(database);
        await db.OpenAsync(ct);
        try
        {
            return await ScalarAsync<string>(db, "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId DESC LIMIT 1", ct);
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    private static async Task<T?> ScalarAsync<T>(SqliteConnection db, string sql, CancellationToken ct)
    {
        await using var command = db.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(ct) is T value ? value : default;
    }

    /// <summary>Unpooled, so the file isn't held open once it's done with (it may be moved or deleted next).</summary>
    private static SqliteConnection Open(string path) => new($"Data Source={path};Pooling=False");
}

/// <summary>Files and folders only this user can read: backups hold the server's keys.</summary>
public static class PrivateFiles
{
    public static FileStream Create(string path)
    {
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return new FileStream(path, options);
    }

    public static void CreateFolder(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}

public static class AppVersion
{
    public static string Current { get; } =
        typeof(AppVersion).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "0.0.0";
}
