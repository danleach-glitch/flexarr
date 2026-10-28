using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flexarr.Api.Configuration;
using Flexarr.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Logging;

/// <summary>
/// The zip to attach to a bug report: the server's logs, what devices sent, and a description of this server. Every
/// key, password and token is taken out.
/// </summary>
public sealed class Diagnostics(LogPaths paths, IOptionsMonitor<FlexarrOptions> options, FlexarrDbContext db, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private static readonly string[] SecretNames = ["key", "secret", "password", "token", "pin"];

    public async Task WriteAsync(Stream output, CancellationToken ct)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        await AddTextAsync(zip, "system.json", JsonSerializer.Serialize(await SystemAsync(ct), Json), ct);
        if (RedactedConfig() is { } config)
        {
            await AddTextAsync(zip, "config.json", config, ct);
        }

        foreach (var file in paths.LogFiles())
        {
            await AddFileAsync(zip, "logs/" + file.Name, file.FullName, ct);
        }

        var uploads = new DirectoryInfo(paths.UploadsDirectory);
        if (uploads.Exists)
        {
            foreach (var file in uploads.EnumerateFiles())
            {
                await AddFileAsync(zip, "devices/" + file.Name, file.FullName, ct);
            }
        }
    }

    private async Task<object> SystemAsync(CancellationToken ct)
    {
        var current = options.CurrentValue;
        var process = Process.GetCurrentProcess();
        return new
        {
            Version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0",
            CreatedAt = clock.GetUtcNow(),
            Os = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.OSArchitecture.ToString(),
            Runtime = RuntimeInformation.FrameworkDescription,
            InDocker = File.Exists("/.dockerenv"),
            Processors = Environment.ProcessorCount,
            MemoryMb = process.WorkingSet64 / (1024 * 1024),
            StartedAt = process.StartTime.ToUniversalTime(),
            FreeSpaceGb = new
            {
                Data = FreeGigabytes(current.DataDirectory),
                Cache = FreeGigabytes(current.ResolvedCacheDirectory),
            },
            Ffmpeg = await FirstLineAsync(current.FfmpegPath, ct),
            current.Transcoding.HardwareAcceleration,
            current.AuthenticationRequired,
            Libraries = await db.Libraries.AsNoTracking().Select(l => new { l.Name, l.Type }).ToListAsync(ct),
            Items = await db.MediaItems.CountAsync(ct),
            Files = await db.MediaFiles.CountAsync(ct),
            Profiles = await db.Profiles.CountAsync(ct),
            Integrations = current.Arr.Select(a => a.Kind.ToString()).Concat(current.Seerr is { BaseUrl.Length: > 0 } ? ["Seerr"] : [])
                .Concat(current.Bazarr is { BaseUrl.Length: > 0 } ? ["Bazarr"] : [])
                .Concat(current.LiveTv is { BaseUrl.Length: > 0 } ? ["Live TV"] : []),
            Logs = new { current.Logs.Level, current.Logs.Areas, current.Logs.RetentionDays, current.Logs.MaxSizeMb },
        };
    }

    /// <summary>config.json with every value whose name sounds secret blanked, then redacted like the log.</summary>
    private string? RedactedConfig()
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(ServerConfigStore.PathFor(options.CurrentValue.DataDirectory))) is not { } config)
            {
                return null;
            }

            Blank(config);
            return Redactor.Redact(config.ToJsonString(Json));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void Blank(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (name, value) in obj.ToList())
                {
                    if (value is JsonValue && SecretNames.Any(s => name.Contains(s, StringComparison.OrdinalIgnoreCase)))
                    {
                        obj[name] = "***";
                    }
                    else if (value is not null)
                    {
                        Blank(value);
                    }
                }

                break;
            case JsonArray array:
                foreach (var item in array.OfType<JsonNode>())
                {
                    Blank(item);
                }

                break;
        }
    }

    private static double? FreeGigabytes(string path)
    {
        try
        {
            return Math.Round(new DriveInfo(Path.GetFullPath(path)).AvailableFreeSpace / 1e9, 1);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task<string?> FirstLineAsync(string program, CancellationToken ct)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(program, "-version") { RedirectStandardOutput = true, RedirectStandardError = true })!;
            var line = await process.StandardOutput.ReadLineAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(timeout.Token);
            return line;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or OperationCanceledException)
        {
            return null;
        }
    }

    private static async Task AddTextAsync(ZipArchive zip, string name, string text, CancellationToken ct)
    {
        await using var entry = await zip.CreateEntry(name, CompressionLevel.Optimal).OpenAsync(ct);
        await using var writer = new StreamWriter(entry);
        await writer.WriteAsync(text.AsMemory(), ct);
    }

    private static async Task AddFileAsync(ZipArchive zip, string name, string path, CancellationToken ct)
    {
        try
        {
            await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            await using var entry = await zip.CreateEntry(name, CompressionLevel.Optimal).OpenAsync(ct);
            await source.CopyToAsync(entry, ct);
        }
        catch (IOException)
        {
            // Tidied away while the zip was being made.
        }
    }
}
