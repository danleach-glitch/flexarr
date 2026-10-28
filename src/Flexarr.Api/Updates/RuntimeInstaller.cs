using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Flexarr.Api.Updates;

/// <summary>The ASP.NET Core runtime patch to have installed, from Microsoft's release metadata.</summary>
/// <param name="AspNetCore">ASP.NET Core's version, e.g. 10.0.14.</param>
/// <param name="NetCore">The .NET runtime it comes with, e.g. 10.0.13.</param>
public sealed record RuntimePatch(string AspNetCore, string NetCore, Uri Archive, string Sha512);

/// <summary>
/// Brings the .NET install a Mac server runs under (~/.dotnet) up to the latest patch of the version a Flexarr build
/// needs, as scripts/deploy-macos.sh does on every deploy: Flexarr is framework-dependent and runs on the newest patch
/// installed, so this is how it gets ASP.NET Core and Kestrel security fixes, and how a release that moves to a newer
/// .NET gets it. Each new version goes in beside the ones there, which the running server doesn't notice.
/// </summary>
public sealed partial class RuntimeInstaller(IHttpClientFactory clients, ILogger<RuntimeInstaller> logger)
{
    private const string MetadataBase = "https://builds.dotnet.microsoft.com/dotnet/release-metadata/";
    private const string ArchiveHost = "https://builds.dotnet.microsoft.com/";

    /// <summary>
    /// Installs the latest patch of the runtime <paramref name="app"/> needs into <paramref name="dotnetRoot"/>, unless
    /// it's there already. Returns the ASP.NET Core version it installed, or null when there was nothing to do.
    /// </summary>
    /// <param name="progress">Bytes received and the archive's size, while it downloads.</param>
    public async Task<string?> EnsureLatestAsync(string app, string dotnetRoot, string runtimeIdentifier, Action<long, long?> progress, CancellationToken ct)
    {
        if (ChannelFor(app) is not { } channel)
        {
            return null;
        }

        var http = clients.CreateClient(UpdateChecker.DownloadClientName);
        var patch = Latest(await http.GetStringAsync(new Uri($"{MetadataBase}{channel}/releases.json"), ct), runtimeIdentifier)
            ?? throw new UpdateException($"Microsoft's release information for .NET {channel} wasn't as expected.");
        if (IsInstalled(dotnetRoot, patch))
        {
            return null;
        }

        LogInstalling(patch.AspNetCore, dotnetRoot);
        var staging = Path.Combine(dotnetRoot, $".staging-{Guid.NewGuid():N}");
        try
        {
            // Left by an earlier interrupted install.
            Directory.CreateDirectory(dotnetRoot);
            foreach (var old in Directory.EnumerateDirectories(dotnetRoot, ".staging-*"))
            {
                Directory.Delete(old, recursive: true);
            }

            Directory.CreateDirectory(staging);
            var archive = Path.Combine(staging, "runtime.tar.gz");
            await DownloadAsync(http, patch, archive, progress, ct);
            var unpacked = Directory.CreateDirectory(Path.Combine(staging, "unpacked")).FullName;
            await using (var file = File.OpenRead(archive))
            await using (var tar = new GZipStream(file, CompressionMode.Decompress))
            {
                await TarFile.ExtractToDirectoryAsync(tar, unpacked, overwriteFiles: false, ct);
            }

            MoveIntoPlace(unpacked, dotnetRoot, staging);
            return IsInstalled(dotnetRoot, patch)
                ? patch.AspNetCore
                : throw new UpdateException($"The ASP.NET Core {patch.AspNetCore} runtime didn't install completely.");
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    /// <summary>The .NET version an app is built for, "10.0", from its runtimeconfig.json.</summary>
    public static string? ChannelFor(string app)
    {
        var config = Directory.EnumerateFiles(app, "*.runtimeconfig.json").FirstOrDefault();
        var runtime = config is null ? null : JsonNode.Parse(File.ReadAllText(config))?["runtimeOptions"];
        var frameworks = runtime?["frameworks"]?.AsArray().Select(f => f) ?? (runtime?["framework"] is { } single ? [single] : []);
        var aspNetCore = frameworks.FirstOrDefault(f => f?["name"]?.GetValue<string>() == "Microsoft.AspNetCore.App");
        return Version.TryParse(aspNetCore?["version"]?.GetValue<string>(), out var version) ? $"{version.Major}.{version.Minor}" : null;
    }

    /// <summary>The latest patch in a channel's releases.json, for one runtime identifier; null if anything looks wrong.</summary>
    public static RuntimePatch? Latest(string metadataJson, string runtimeIdentifier)
    {
        var metadata = JsonNode.Parse(metadataJson);
        var latest = metadata?["latest-release"]?.GetValue<string>();
        var release = metadata?["releases"]?.AsArray().FirstOrDefault(r => r?["release-version"]?.GetValue<string>() == latest);
        var aspNetCore = release?["aspnetcore-runtime"]?["version"]?.GetValue<string>();
        var netCore = release?["runtime"]?["version"]?.GetValue<string>();
        var file = release?["aspnetcore-runtime"]?["files"]?.AsArray()
            .FirstOrDefault(f => f?["name"]?.GetValue<string>() == $"aspnetcore-runtime-{runtimeIdentifier}.tar.gz");
        var url = file?["url"]?.GetValue<string>();
        var hash = file?["hash"]?.GetValue<string>();
        if (aspNetCore is null || netCore is null || url is null || hash is null || !PatchVersion().IsMatch(aspNetCore) || !PatchVersion().IsMatch(netCore)
            || !url.StartsWith(ArchiveHost, StringComparison.Ordinal) || !Sha512Hex().IsMatch(hash))
        {
            return null;
        }

        return new RuntimePatch(aspNetCore, netCore, new Uri(url), hash.ToLowerInvariant());
    }

    /// <summary>
    /// Every part of the patch is in place, down to the last folder the archive unpacks (host/fxr), so a half-finished
    /// install counts as missing and is repaired.
    /// </summary>
    public static bool IsInstalled(string dotnetRoot, RuntimePatch patch) =>
        File.Exists(Path.Combine(dotnetRoot, "shared", "Microsoft.AspNetCore.App", patch.AspNetCore, "Microsoft.AspNetCore.App.deps.json"))
        && File.Exists(Path.Combine(dotnetRoot, "shared", "Microsoft.NETCore.App", patch.NetCore, "Microsoft.NETCore.App.deps.json"))
        && File.Exists(Path.Combine(dotnetRoot, "shared", "Microsoft.NETCore.App", patch.NetCore, "libcoreclr.dylib"))
        && File.Exists(Path.Combine(dotnetRoot, "host", "fxr", patch.NetCore, "libhostfxr.dylib"));

    /// <summary>
    /// Renames each unpacked version folder into place, the .NET runtime before ASP.NET Core (which needs it), so the
    /// server only ever rolls forward to a patch that's all there. The same version already there is a leftover of an
    /// interrupted install: it's set aside and the complete copy put in its place. Other versions are never touched.
    /// </summary>
    public static void MoveIntoPlace(string unpacked, string dotnetRoot, string staging)
    {
        string[] patterns = [Path.Combine("host", "fxr"), Path.Combine("shared", "Microsoft.NETCore.App"), "shared"];
        foreach (var parent in patterns)
        {
            var from = Path.Combine(unpacked, parent);
            if (!Directory.Exists(from))
            {
                continue;
            }

            // "shared" holds the framework folders, each holding versions; the others hold versions directly.
            var versions = parent == "shared" ? Directory.EnumerateDirectories(from).SelectMany(Directory.EnumerateDirectories) : Directory.EnumerateDirectories(from);
            foreach (var version in versions.ToList())
            {
                var destination = Path.Combine(dotnetRoot, Path.GetRelativePath(unpacked, version));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (Directory.Exists(destination))
                {
                    Directory.Move(destination, Path.Combine(staging, "replaced-" + Path.GetRelativePath(dotnetRoot, destination).Replace(Path.DirectorySeparatorChar, '_')));
                }

                Directory.Move(version, destination);
            }
        }

        // The dotnet host only goes in once. macOS grants the external-drive access Flexarr needs to that very binary: a
        // newer host (each runtime patch ships one) is a different app to it, and every folder Flexarr opens on the drive
        // then waits on a permission prompt nobody sees. An older host runs newer runtimes fine.
        var host = Path.Combine(dotnetRoot, "dotnet");
        if (!File.Exists(host) && File.Exists(Path.Combine(unpacked, "dotnet")))
        {
            File.Move(Path.Combine(unpacked, "dotnet"), host);
        }

        foreach (var notice in new[] { "LICENSE.txt", "ThirdPartyNotices.txt" })
        {
            if (File.Exists(Path.Combine(unpacked, notice)))
            {
                File.Move(Path.Combine(unpacked, notice), Path.Combine(dotnetRoot, notice), overwrite: true);
            }
        }
    }

    /// <summary>Downloads the archive, checking it against the SHA-512 Microsoft publishes for it.</summary>
    private static async Task DownloadAsync(HttpClient http, RuntimePatch patch, string path, Action<long, long?> progress, CancellationToken ct)
    {
        using var response = await http.GetAsync(patch.Archive, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        await using var file = File.Create(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        var buffer = new byte[81920];
        long received = 0;
        int read;
        while ((read = await body.ReadAsync(buffer, ct)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            received += read;
            progress(received, total);
        }

        if (!string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), patch.Sha512, StringComparison.Ordinal))
        {
            throw new UpdateException($"The download of the ASP.NET Core {patch.AspNetCore} runtime doesn't match the checksum Microsoft publishes for it, so it wasn't installed.");
        }
    }

    [GeneratedRegex("^[0-9]+\\.[0-9]+\\.[0-9]+$")]
    private static partial Regex PatchVersion();

    [GeneratedRegex("^[0-9a-fA-F]{128}$")]
    private static partial Regex Sha512Hex();

    [LoggerMessage(Level = LogLevel.Information, Message = "Installing the ASP.NET Core {Version} runtime into {DotnetRoot}")]
    private partial void LogInstalling(string version, string dotnetRoot);
}
