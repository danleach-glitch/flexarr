using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flexarr.Api.Configuration;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Backups;

/// <summary>Opening a protected file without its passphrase.</summary>
public sealed class PassphraseNeededException() : Exception("This file is protected with a passphrase.");

/// <summary>
/// A file brought in from the browser: a backup to restore, or settings to import. It's kept for an hour in
/// &lt;DataDirectory&gt;/backup-uploads while the person looks it over, so the passphrase and choices come separately
/// and nothing secret goes back to the browser.
/// </summary>
public sealed class BackupUpload(string id, string raw, DateTime receivedAt)
{
    public string Id { get; } = id;
    public string RawPath { get; } = raw;
    public DateTime ReceivedAt { get; } = receivedAt;
    public bool Encrypted { get; set; }

    /// <summary>A backup, unpacked and checked, once opened.</summary>
    public BackupContents? Backup { get; set; }

    public string? Unpacked { get; set; }

    /// <summary>A settings file, once opened: only what Flexarr will use of it (see <see cref="SettingsTransfer.Clean"/>).</summary>
    public JsonObject? Settings { get; set; }

    /// <summary>One open or restore at a time, so two clicks can't both write the same files.</summary>
    public SemaphoreSlim Gate { get; } = new(1, 1);
}

public sealed class BackupUploads(IOptionsMonitor<FlexarrOptions> options, TimeProvider time)
{
    private static readonly TimeSpan KeptFor = TimeSpan.FromHours(1);
    private readonly ConcurrentDictionary<string, BackupUpload> _uploads = new();

    private string Folder => Path.Combine(options.CurrentValue.DataDirectory, "backup-uploads");

    /// <summary>The biggest file taken: far more than any server's backup.</summary>
    public const long MaxBytes = 16L << 30;

    /// <exception cref="InvalidDataException">It's bigger than <see cref="MaxBytes"/>, or than there's room for.</exception>
    public async Task<BackupUpload> SaveAsync(Stream body, long? length, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        Sweep();
        PrivateFiles.CreateFolder(Folder);
        // Room for it, unpacking it (a few times its size), and a gigabyte to spare.
        if (length > MaxBytes || (length ?? 0) * 6 + (1L << 30) > new DriveInfo(Folder).AvailableFreeSpace)
        {
            throw new InvalidDataException("There isn’t room on the server for this file.");
        }

        var id = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12));
        var path = Path.Combine(Folder, id);
        try
        {
            await using var file = PrivateFiles.Create(path);
            var buffer = new byte[81920];
            long received = 0;
            int read;
            while ((read = await body.ReadAsync(buffer, ct)) > 0)
            {
                received += read;
                if (received > MaxBytes)
                {
                    throw new InvalidDataException("This file is too big to be a Flexarr backup.");
                }

                // Checked as it comes (a browser needn't say how big it is up front), every 64 MB.
                if (received % (64L << 20) < read && new DriveInfo(Folder).AvailableFreeSpace < 1L << 30)
                {
                    throw new InvalidDataException("There isn’t room on the server for this file.");
                }

                await file.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }
        catch
        {
            File.Delete(path);
            throw;
        }

        var upload = new BackupUpload(id, path, time.GetUtcNow().UtcDateTime) { Encrypted = BackupCrypto.IsEncrypted(path) };
        _uploads[id] = upload;
        return upload;
    }

    public BackupUpload? Find(string id) => _uploads.TryGetValue(id, out var upload) ? upload : null;

    /// <summary>Decrypts (with the passphrase, when it's protected) and works out what the file is.</summary>
    /// <exception cref="PassphraseNeededException">It's protected and no passphrase was given.</exception>
    /// <exception cref="WrongPassphraseException">The passphrase doesn't open it.</exception>
    /// <exception cref="InvalidDataException">It isn't a Flexarr backup or settings file.</exception>
    public async Task OpenAsync(BackupUpload upload, string? passphrase, IReadOnlyCollection<string> knownMigrations, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(upload);
        await upload.Gate.WaitAsync(ct);
        try
        {
            if (upload.Backup is null && upload.Settings is null)
            {
                await OpenOnceAsync(upload, passphrase, knownMigrations, ct);
            }
        }
        finally
        {
            upload.Gate.Release();
        }
    }

    private async Task OpenOnceAsync(BackupUpload upload, string? passphrase, IReadOnlyCollection<string> knownMigrations, CancellationToken ct)
    {
        var plain = upload.RawPath;
        if (upload.Encrypted)
        {
            if (string.IsNullOrEmpty(passphrase))
            {
                throw new PassphraseNeededException();
            }

            plain = upload.RawPath + ".open";
            try
            {
                await using var input = File.OpenRead(upload.RawPath);
                await using var output = PrivateFiles.Create(plain);
                await BackupCrypto.DecryptAsync(input, output, passphrase, ct);
            }
            catch
            {
                File.Delete(plain);
                throw;
            }
        }

        if (IsZip(plain))
        {
            var unpacked = Path.Combine(Folder, upload.Id + "-unpacked");
            if (Directory.Exists(unpacked))
            {
                Directory.Delete(unpacked, recursive: true);
            }

            try
            {
                upload.Backup = await BackupArchive.ExtractAsync(plain, unpacked, knownMigrations, ct);
            }
            finally
            {
                // Only a backup that can be restored is kept unpacked: one that can't goes now, not in an hour.
                if (upload.Backup is not { Problem: null } && Directory.Exists(unpacked))
                {
                    Directory.Delete(unpacked, recursive: true);
                }
            }

            upload.Unpacked = upload.Backup.Problem is null ? unpacked : null;
            return;
        }

        if (new FileInfo(plain).Length <= SettingsTransfer.MaxFileBytes)
        {
            try
            {
                await using var stream = File.OpenRead(plain);
                if (SettingsTransfer.Clean(await JsonNode.ParseAsync(stream, cancellationToken: ct)) is { } settings)
                {
                    upload.Settings = settings;
                    return;
                }
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or FormatException)
            {
                // Not JSON, or JSON that isn't a settings file (a property given twice, a number where text should be).
            }
        }

        throw new InvalidDataException("This isn’t a Flexarr backup or settings file.");
    }

    /// <summary>
    /// Done with: its files go (an unpacked backup that was staged for restore has already moved away). The caller holds
    /// its gate, or knows nothing else is using it.
    /// </summary>
    public void Remove(BackupUpload upload)
    {
        ArgumentNullException.ThrowIfNull(upload);
        _uploads.TryRemove(upload.Id, out _);
        DeleteFiles(upload.Id);
    }

    /// <summary>Done with, once anything opening or restoring it has finished.</summary>
    public async Task RemoveAsync(BackupUpload upload, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(upload);
        await upload.Gate.WaitAsync(ct);
        try
        {
            Remove(upload);
        }
        finally
        {
            upload.Gate.Release();
        }
    }

    private void Sweep()
    {
        var now = time.GetUtcNow().UtcDateTime;
        // Not one that's being opened or restored right now.
        foreach (var upload in _uploads.Values.Where(u => now - u.ReceivedAt > KeptFor && u.Gate.CurrentCount > 0))
        {
            Remove(upload);
        }

        // Left by an earlier run of the server, or by a restore that stopped part-way (restore-…).
        if (Directory.Exists(Folder))
        {
            foreach (var entry in new DirectoryInfo(Folder).EnumerateFileSystemInfos()
                .Where(e => !_uploads.ContainsKey(e.Name.Split('.')[0].Replace("-unpacked", "", StringComparison.Ordinal)) && now - e.LastWriteTimeUtc > KeptFor))
            {
                try
                {
                    if (entry is DirectoryInfo folder)
                    {
                        folder.Delete(recursive: true);
                    }
                    else
                    {
                        entry.Delete();
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Tried again with the next upload.
                }
            }
        }
    }

    private void DeleteFiles(string id)
    {
        var path = Path.Combine(Folder, id);
        File.Delete(path);
        File.Delete(path + ".open");
        if (Directory.Exists(path + "-unpacked"))
        {
            Directory.Delete(path + "-unpacked", recursive: true);
        }
    }

    private static bool IsZip(string path)
    {
        using var file = File.OpenRead(path);
        Span<byte> head = stackalloc byte[4];
        return file.ReadAtLeast(head, 4, throwOnEndOfStream: false) == 4 && head.SequenceEqual("PK\u0003\u0004"u8);
    }
}
