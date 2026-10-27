using System.Text.Json;
using System.Text.Json.Nodes;
using Flexarr.Api.Configuration;
using Flexarr.Api.Endpoints;
using Flexarr.Api.Profiles;
using Flexarr.Api.Security;
using Flexarr.Core.Data;
using Flexarr.Core.Scanning;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Backups;

/// <param name="Folder">Where backups are kept now.</param>
/// <param name="FolderSetting">The folder chosen in Settings; null for the default (in the data folder).</param>
/// <param name="FolderProblem">Why the folder can't be used now (a share that isn't mounted), if it can't.</param>
public sealed record BackupsDto(string Folder, string? FolderSetting, bool Nightly, int KeepNightly, IReadOnlyList<BackupInfo> Backups, string? FolderProblem = null);

/// <param name="Folder">Empty for the default, in the data folder.</param>
public sealed record BackupSettingsUpdate(string? Folder, bool? Nightly);

/// <param name="Passphrase">Protects the file with this passphrase; without one it's saved as it is.</param>
public sealed record DownloadRequest(string? Passphrase);

public sealed record BackupContentsDto(DateTime CreatedAt, BackupKind Kind, string FlexarrVersion, int Profiles, int Libraries, int Titles, string? Problem);

/// <param name="Kind">"backup" or "settings" once opened; null while it waits for its passphrase.</param>
/// <param name="FromAnotherServer">A backup made by a different Flexarr server: restoring it brings that server's keys and sign-ins.</param>
public sealed record UploadDto(string Id, bool Encrypted, string? Kind, BackupContentsDto? Backup, SettingsFileDto? Settings, bool FromAnotherServer = false);

public sealed record OpenUploadRequest(string? Passphrase);

public static class BackupEndpoints
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static RouteGroupBuilder MapBackupEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/backups").WithTags("Backups").RequireAuthorization(FlexarrClaims.AdminPolicy);

        group.MapGet("/", (BackupStore store, IOptionsMonitor<FlexarrOptions> options) => ToDto(store, options.CurrentValue));


        group.MapPut("/settings", async (BackupSettingsUpdate update, BackupStore store, IOptionsMonitor<FlexarrOptions> options, CancellationToken ct) =>
        {
            var current = options.CurrentValue;
            var folder = update.Folder is null ? current.Backups.Folder : update.Folder.Trim() is { Length: > 0 } chosen ? chosen : null;
            if (folder is not null && CheckFolder(folder) is { } problem)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["folder"] = [problem] });
            }

            var nightly = update.Nightly ?? current.Backups.Nightly;
            ServerConfigStore.Update(current.DataDirectory, config => config["backups"] = new JsonObject
            {
                ["folder"] = folder is null ? null : Path.GetFullPath(folder),
                ["nightly"] = nightly,
                ["keepNightly"] = current.Backups.KeepNightly,
            });

            // The file watcher reloads options a moment after the write.
            for (var i = 0; i < 50 && (options.CurrentValue.Backups.Folder != (folder is null ? null : Path.GetFullPath(folder))
                || options.CurrentValue.Backups.Nightly != nightly); i++)
            {
                await Task.Delay(100, ct);
            }

            return Results.Ok(ToDto(store, options.CurrentValue));
        });

        group.MapPost("/", async (BackupStore store, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await store.CreateAsync(BackupKind.Manual, ct));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
                or Microsoft.Data.Sqlite.SqliteException)
            {
                return Results.Problem($"The backup couldn’t be made: {ex.Message}", statusCode: 500);
            }
        });

        // Saved as it is, straight to the browser's downloads (a link, so a big backup isn't held in the page's memory).
        group.MapGet("/{name}/download", (string name, BackupStore store) =>
            store.PathFor(name) is { } path ? Results.File(path, "application/zip", name) : Results.NotFound());

        // A POST, as it may carry a passphrase.
        group.MapPost("/{name}/download", (string name, DownloadRequest? request, BackupStore store) =>
        {
            if (store.PathFor(name) is not { } path)
            {
                return Results.NotFound();
            }

            if (string.IsNullOrEmpty(request?.Passphrase))
            {
                return Results.File(path, "application/zip", name);
            }

            if (request.Passphrase.Length < BackupCrypto.MinPassphraseLength)
            {
                return ShortPassphrase();
            }

            return Results.Stream(async output =>
            {
                await using var input = File.OpenRead(path);
                await BackupCrypto.EncryptAsync(input, output, request.Passphrase, CancellationToken.None);
            }, "application/octet-stream", Path.ChangeExtension(name, ".flexarr-backup"));
        });

        group.MapDelete("/{name}", (string name, BackupStore store) => store.Delete(name) ? Results.NoContent() : Results.NotFound());

        group.MapPost("/{name}/restore", async (string name, KeyRequest? request, HttpContext http, BackupStore store, IServerRestart restart, FlexarrDbContext db,
            CurrentProfile current, PinGuard guard, IOptionsMonitor<FlexarrOptions> options, CancellationToken ct) =>
        {
            if (store.PathFor(name) is not { } path)
            {
                return Results.NotFound();
            }

            if (await SettingsEndpoints.ConfirmKeyAsync(request, http, db, current, guard, ct, "restore a backup") is { } refused)
            {
                return refused;
            }

            var unpacked = Path.Combine(options.CurrentValue.DataDirectory, "backup-uploads", $"restore-{Guid.NewGuid():N}");
            try
            {
                var contents = await BackupArchive.ExtractAsync(path, unpacked, [.. db.Database.GetMigrations()], ct);
                return await RestoreAsync(unpacked, contents, store, restart, options.CurrentValue, ct);
            }
            catch (InvalidDataException ex)
            {
                return Results.Problem(ex.Message, statusCode: 400);
            }
            finally
            {
                if (Directory.Exists(unpacked))
                {
                    Directory.Delete(unpacked, recursive: true);
                }
            }
        });

        group.MapPost("/settings-export", async (DownloadRequest? request, FlexarrDbContext db, IOptionsMonitor<FlexarrOptions> options, TimeProvider time,
            CancellationToken ct) =>
        {
            var now = time.GetUtcNow().UtcDateTime;
            var document = await SettingsTransfer.ExportAsync(options.CurrentValue.DataDirectory, db, now, ct);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(document, Indented);
            var name = $"flexarr-settings-{now:yyyyMMdd}";
            if (string.IsNullOrEmpty(request?.Passphrase))
            {
                return Results.File(bytes, "application/json", name + ".json");
            }

            if (request.Passphrase.Length < BackupCrypto.MinPassphraseLength)
            {
                return ShortPassphrase();
            }

            using var output = new MemoryStream();
            await BackupCrypto.EncryptAsync(new MemoryStream(bytes), output, request.Passphrase, ct);
            return Results.File(output.ToArray(), "application/octet-stream", name + ".flexarr-settings");
        });

        // A backup or settings file from the browser, as it is (not a form): it's looked at before anything is done with it.
        group.MapPost("/uploads", async (HttpRequest http, BackupUploads uploads, FlexarrDbContext db, IOptionsMonitor<FlexarrOptions> options, CancellationToken ct) =>
        {
            // A backup can be bigger than requests are usually allowed to be.
            if (http.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            {
                limit.MaxRequestBodySize = null;
            }

            BackupUpload upload;
            try
            {
                upload = await uploads.SaveAsync(http.Body, http.ContentLength, ct);
            }
            catch (InvalidDataException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status413PayloadTooLarge);
            }

            if (!upload.Encrypted && await OpenAsync(upload, null, uploads, db, ct) is { } problem)
            {
                uploads.Remove(upload);
                return problem;
            }

            return Results.Ok(await ToDtoAsync(upload, db, options.CurrentValue, ct));
        });

        group.MapPost("/uploads/{id}/open", async (string id, OpenUploadRequest request, BackupUploads uploads, FlexarrDbContext db,
            IOptionsMonitor<FlexarrOptions> options, CancellationToken ct) =>
        {
            if (uploads.Find(id) is not { } upload)
            {
                return Gone();
            }

            return await OpenAsync(upload, request.Passphrase, uploads, db, ct) ?? Results.Ok(await ToDtoAsync(upload, db, options.CurrentValue, ct));
        });

        group.MapPost("/uploads/{id}/restore", async (string id, KeyRequest? request, HttpContext http, BackupUploads uploads, BackupStore store,
            IServerRestart restart, FlexarrDbContext db, CurrentProfile current, PinGuard guard, IOptionsMonitor<FlexarrOptions> options, CancellationToken ct) =>
        {
            if (uploads.Find(id) is not { } upload)
            {
                return Gone();
            }

            if (await SettingsEndpoints.ConfirmKeyAsync(request, http, db, current, guard, ct, "restore a backup") is { } refused)
            {
                return refused;
            }

            await upload.Gate.WaitAsync(ct);
            try
            {
                if (upload is not { Backup: { } contents, Unpacked: { } unpacked } || !Directory.Exists(unpacked))
                {
                    return upload.Backup?.Problem is { } problem ? Results.Problem(problem, statusCode: 409) : Gone();
                }

                var result = await RestoreAsync(unpacked, contents, store, restart, options.CurrentValue, ct);
                uploads.Remove(upload);
                return result;
            }
            finally
            {
                upload.Gate.Release();
            }
        });

        group.MapPost("/uploads/{id}/import", async (string id, SettingsImportRequest request, HttpContext http, BackupUploads uploads, FlexarrDbContext db,
            ILibraryScanQueue queue, CurrentProfile current, PinGuard guard, IOptionsMonitor<FlexarrOptions> options, CancellationToken ct) =>
        {
            if (uploads.Find(id) is not { Settings: { } document })
            {
                return Gone();
            }

            // The server's keys decide who can get in, and its paths what it runs, so taking them from a file asks again,
            // as showing the key does.
            if (SettingsTransfer.NeedsConfirmation(request.Sections)
                && await SettingsEndpoints.ConfirmKeyAsync(new KeyRequest(request.Pin, request.Confirm), http, db, current, guard, ct,
                    "use the server keys or paths from this file") is { } refused)
            {
                return refused;
            }

            try
            {
                return Results.Ok(await SettingsTransfer.ApplyAsync(document, request, options.CurrentValue.DataDirectory, db, queue, ct));
            }
            catch (InvalidDataException ex)
            {
                return Results.Problem(ex.Message, statusCode: 400);
            }
        });

        group.MapDelete("/uploads/{id}", async (string id, BackupUploads uploads, CancellationToken ct) =>
        {
            if (uploads.Find(id) is { } upload)
            {
                await uploads.RemoveAsync(upload, ct);
            }

            return Results.NoContent();
        });

        return api;
    }

    /// <summary>
    /// Keeps a copy of the server as it is now, stages the backup to be put in place, and restarts to do it. Nothing
    /// changes unless the copy is made first.
    /// </summary>
    private static async Task<IResult> RestoreAsync(string unpacked, BackupContents contents, BackupStore store, IServerRestart restart, FlexarrOptions options,
        CancellationToken ct)
    {
        if (contents.Problem is { } problem)
        {
            return Results.Problem(problem, statusCode: 409);
        }

        // The settings as they'll be after the restart: the backup's, with this machine's paths. Checked once more as a
        // whole, as the server mustn't restart into settings it can't read.
        var config = Path.Combine(unpacked, ServerConfigStore.FileName);
        SettingsTransfer.KeepMachinePaths(config, ServerConfigStore.PathFor(options.DataDirectory));
        if (File.Exists(config) && SettingsTransfer.BindProblem(JsonNode.Parse(await File.ReadAllTextAsync(config, ct)) as JsonObject ?? []) is { } settings)
        {
            return Results.Problem($"The settings in this backup can’t be used: {settings}", statusCode: 409);
        }

        // One restore at a time: a second can't swap its backup in under the first's copy.
        if (!await PendingRestore.Gate.WaitAsync(TimeSpan.Zero, ct))
        {
            return Results.Problem("A restore is already under way.", statusCode: 409);
        }

        try
        {
            try
            {
                await store.CreateAsync(BackupKind.PreRestore, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Results.Problem($"Flexarr couldn’t keep a copy of the server as it is now, so nothing was restored: {ex.Message}", statusCode: 500);
            }

            PendingRestore.Stage(options.DataDirectory, unpacked, DateTime.UtcNow);
            restart.Request();
            return Results.Accepted();
        }
        finally
        {
            PendingRestore.Gate.Release();
        }
    }

    private static async Task<IResult?> OpenAsync(BackupUpload upload, string? passphrase, BackupUploads uploads, FlexarrDbContext db, CancellationToken ct)
    {
        try
        {
            await uploads.OpenAsync(upload, passphrase, [.. db.Database.GetMigrations()], ct);
            return null;
        }
        catch (PassphraseNeededException ex)
        {
            return Results.Problem(ex.Message, statusCode: 400);
        }
        catch (WrongPassphraseException ex)
        {
            return Results.Problem(ex.Message, statusCode: 403);
        }
        catch (InvalidDataException ex)
        {
            return Results.Problem(ex.Message, statusCode: 400);
        }
    }

    private static async Task<UploadDto> ToDtoAsync(BackupUpload upload, FlexarrDbContext db, FlexarrOptions options, CancellationToken ct) => new(
        upload.Id,
        upload.Encrypted,
        upload.Backup is not null ? "backup" : upload.Settings is not null ? "settings" : null,
        upload.Backup is { } b
            ? new BackupContentsDto(b.Manifest.CreatedAt, b.Manifest.Kind, b.Manifest.FlexarrVersion, b.Profiles, b.Libraries, b.Titles, b.Problem)
            : null,
        upload.Settings is { } settings ? await SettingsTransfer.DescribeAsync(settings, db, ct) : null,
        upload.Unpacked is { } unpacked && ImageSecretIn(unpacked) is { } secret && secret != options.ImageSecret);

    /// <summary>
    /// The image secret in an unpacked backup's settings: made once by each server and never changed, so it tells
    /// whether the backup is this server's.
    /// </summary>
    private static string? ImageSecretIn(string unpacked)
    {
        try
        {
            var path = Path.Combine(unpacked, ServerConfigStore.FileName);
            return File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject config
                ? config.FirstOrDefault(p => p.Key.Equals("imageSecret", StringComparison.OrdinalIgnoreCase)).Value?.GetValue<string>()
                : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    private static BackupsDto ToDto(BackupStore store, FlexarrOptions options) =>
        new(SafeFolder(store), options.Backups.Folder, options.Backups.Nightly, options.Backups.KeepNightly, store.List(), store.FolderProblem());

    private static string SafeFolder(BackupStore store)
    {
        try
        {
            return store.Folder;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return "";
        }
    }

    /// <summary>Why backups can't go in this folder, or null when they can (it's made if it isn't there).</summary>
    private static string? CheckFolder(string folder)
    {
        if (!Path.IsPathFullyQualified(folder) || folder.Contains('\0', StringComparison.Ordinal))
        {
            return "Enter the folder’s full path.";
        }

        try
        {
            PrivateFiles.CreateFolder(folder);
            var probe = Path.Combine(folder, $".flexarr-write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return $"Flexarr can’t write to {folder}.";
        }
    }

    private static IResult ShortPassphrase() =>
        Results.ValidationProblem(new Dictionary<string, string[]> { ["passphrase"] = [$"Use a passphrase of at least {BackupCrypto.MinPassphraseLength} characters."] });

    private static IResult Gone() => Results.Problem("That file is no longer here. Choose it again.", statusCode: 404);
}
