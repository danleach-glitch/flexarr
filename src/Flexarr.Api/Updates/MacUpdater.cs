using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flexarr.Api.Configuration;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Updates;

public enum UpdateStage
{
    Idle,
    Downloading,
    Installing,
}

/// <summary>How the last update went, written by install-update.sh once it's done.</summary>
public sealed record UpdateResult(string From, string To, bool Ok, string Message, DateTimeOffset At);

/// <summary>
/// Updates a Mac install (scripts/deploy-macos.sh's layout) to a release: downloads its build for this processor,
/// checks it against the release's SHA256SUMS, unpacks it beside the app, and hands over to install-update.sh, which
/// runs as a LaunchAgent of its own because putting it in place means stopping this server.
/// </summary>
public sealed partial class MacUpdater(
    IInstallEnvironment install,
    IHttpClientFactory clients,
    IOptionsMonitor<FlexarrOptions> options,
    IServer server,
    ILogger<MacUpdater> logger)
{
    public const string UpdaterLabel = "io.github.flexarr.updater";
    public const string ResultFileName = "update-result.json";

    /// <summary>Earlier builds' web files are kept this long, so tabs opened before the update still load (as deploys do).</summary>
    private static readonly TimeSpan KeepWebFiles = TimeSpan.FromDays(7);

    private readonly Lock _lock = new();

    public UpdateStage Stage { get; private set; }

    /// <summary>The release being downloaded or installed.</summary>
    public ReleaseVersion? Installing { get; private set; }

    public long Received { get; private set; }

    public long? Total { get; private set; }

    /// <summary>Why the last attempt stopped before the restart; cleared by the next attempt.</summary>
    public string? Error { get; private set; }

    public bool CanInstall => install.Kind == InstallKind.MacLaunchAgent && !ReleaseVersion.Current.IsDevelopment;

    public UpdateResult? LastResult()
    {
        var path = Path.Combine(options.CurrentValue.DataDirectory, ResultFileName);
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<UpdateResult>(File.ReadAllText(path), JsonSerializerOptions.Web) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Starts installing <paramref name="release"/> in the background; false when it can't, or one is already going.</summary>
    public bool Start(LatestRelease release)
    {
        if (!CanInstall)
        {
            return false;
        }

        lock (_lock)
        {
            if (Stage != UpdateStage.Idle)
            {
                return false;
            }

            Stage = UpdateStage.Downloading;
            Installing = release.Version;
            Received = 0;
            Total = null;
            Error = null;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await InstallAsync(release, CancellationToken.None);
                Stage = UpdateStage.Installing;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Whatever went wrong, this server carries on as it was; the reason shows in Settings › Updates.
                LogFailed(release.Version.ToString(), ex);
                Error = ex is UpdateException ? ex.Message : $"The update didn't install: {ex.Message}";
                Stage = UpdateStage.Idle;
                TidyUp();
                return;
            }

            // install-update.sh stops this server within seconds. Still running long after, it didn't go ahead.
            await Task.Delay(TimeSpan.FromMinutes(5));
            Error = LastResult() is { Ok: false, To: var to } result && to == release.Version.ToString()
                ? result.Message
                : "The update didn't go ahead. Its log is ~/.flexarr/logs/update.log on the server.";
            Stage = UpdateStage.Idle;
        });
        return true;
    }

    private async Task InstallAsync(LatestRelease release, CancellationToken ct)
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new UpdateException("Only a Mac install updates itself.");
        }

        var root = install.Root ?? throw new UpdateException("This server wasn't installed with the Mac install script.");
        var build = release.MacBuild(RuntimeInformation.OSArchitecture)
            ?? throw new UpdateException($"Flexarr {release.Version} has no Mac build to install. Update with the install script instead.");
        var sums = release.Checksums ?? throw new UpdateException($"Flexarr {release.Version} has no checksums, so its Mac build can't be checked.");
        LogStarting(release.Version.ToString());

        var http = clients.CreateClient(UpdateChecker.DownloadClientName);
        var expected = ChecksumFor(await http.GetStringAsync(sums.Url, ct), build.Name)
            ?? throw new UpdateException($"Flexarr {release.Version}'s checksums don't include {build.Name}.");

        var stage = Path.Combine(root, "update");
        if (Directory.Exists(stage))
        {
            Directory.Delete(stage, recursive: true);
        }

        Directory.CreateDirectory(stage);
        var archive = Path.Combine(stage, build.Name);
        Total = build.Size;
        using (var response = await http.GetAsync(build.Url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            await using var file = File.Create(archive);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            int read;
            while ((read = await body.ReadAsync(buffer, ct)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
                Received += read;
            }

            if (!string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), expected, StringComparison.Ordinal))
            {
                throw new UpdateException($"The download of Flexarr {release.Version} doesn't match its checksum, so it wasn't installed. Try again later.");
            }
        }

        Stage = UpdateStage.Installing;
        var app = Path.Combine(stage, "app");
        Directory.CreateDirectory(app);
        await using (var compressed = File.OpenRead(archive))
        await using (var tar = new GZipStream(compressed, CompressionMode.Decompress))
        {
            // Refuses any entry that would land outside the folder.
            await TarFile.ExtractToDirectoryAsync(tar, app, overwriteFiles: false, ct);
        }

        File.Delete(archive);
        if (!File.Exists(Path.Combine(app, "Flexarr.dll")))
        {
            throw new UpdateException($"Flexarr {release.Version}'s Mac build isn't laid out as expected, so it wasn't installed.");
        }

        if (MissingRuntime(app, install.DotnetRoot) is { } missing)
        {
            throw new UpdateException($"Flexarr {release.Version} needs {missing}, which isn't installed. Update with the install script, which installs it.");
        }

        KeepRecentWebFiles(Path.Combine(root, "app", "wwwroot", "assets"), Path.Combine(app, "wwwroot", "assets"), DateTime.UtcNow);
        await HandOverAsync(root, stage, release.Version, ct);
    }

    /// <summary>Removes what a failed attempt downloaded or unpacked.</summary>
    private void TidyUp()
    {
        try
        {
            if (install.Root is { } root && Directory.Exists(Path.Combine(root, "update")))
            {
                Directory.Delete(Path.Combine(root, "update"), recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cleared at the start of the next attempt anyway.
        }
    }

    /// <summary>Writes install-update.sh and a LaunchAgent for it, and starts it.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private async Task HandOverAsync(string root, string stage, ReleaseVersion version, CancellationToken ct)
    {
        var script = Path.Combine(stage, "install-update.sh");
        await using (var resource = typeof(MacUpdater).Assembly.GetManifestResourceStream("install-update.sh")
            ?? throw new UpdateException("The update script is missing from this build."))
        await using (var file = File.Create(script))
        {
            await resource.CopyToAsync(file, ct);
        }

        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var data = Path.GetFullPath(options.CurrentValue.DataDirectory);
        var log = Path.Combine(root, "logs", "update.log");
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        string[] arguments = ["/bin/bash", script, root, data, Port().ToString(System.Globalization.CultureInfo.InvariantCulture),
            ReleaseVersion.Current.ToString(), version.ToString(), InstallEnvironment.LaunchAgentLabel,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", InstallEnvironment.LaunchAgentLabel + ".plist")];
        var plist = Path.Combine(stage, UpdaterLabel + ".plist");
        await File.WriteAllTextAsync(plist, $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key>
              <string>{UpdaterLabel}</string>
              <key>ProgramArguments</key>
              <array>
            {string.Join('\n', arguments.Select(a => $"    <string>{SecurityElement.Escape(a)}</string>"))}
              </array>
              <key>RunAtLoad</key>
              <true/>
              <key>StandardOutPath</key>
              <string>{SecurityElement.Escape(log)}</string>
              <key>StandardErrorPath</key>
              <string>{SecurityElement.Escape(log)}</string>
              <key>EnvironmentVariables</key>
              <dict>
                <key>PATH</key>
                <string>/usr/bin:/bin:/usr/sbin:/sbin</string>
              </dict>
            </dict>
            </plist>
            """, ct);

        var domain = $"gui/{await RunAsync("/usr/bin/id", ct, "-u")}";
        // The job from the last update has finished but stays loaded until it's taken out.
        await LaunchctlAsync(ct, "bootout", $"{domain}/{UpdaterLabel}");
        if (await LaunchctlAsync(ct, "bootstrap", domain, plist) is { } failure)
        {
            throw new UpdateException($"Couldn't start the update: launchctl said {failure}");
        }

        LogHandedOver(version.ToString(), log);
    }

    /// <summary>Runs launchctl; null when it succeeded, otherwise what it said.</summary>
    private static async Task<string?> LaunchctlAsync(CancellationToken ct, params string[] arguments)
    {
        try
        {
            await RunAsync("/bin/launchctl", ct, arguments);
            return null;
        }
        catch (UpdateException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Runs a program and returns what it printed; throws with what it said if it fails.</summary>
    private static async Task<string> RunAsync(string program, CancellationToken ct, params string[] arguments)
    {
        var start = new ProcessStartInfo(program) { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new UpdateException($"Couldn't run {program}.");
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return process.ExitCode == 0
            ? (await output).Trim()
            : throw new UpdateException(error.Trim() is { Length: > 0 } said ? said : $"{program} failed with exit code {process.ExitCode}");
    }

    /// <summary>The port this server answers on, for the script's health check.</summary>
    private int Port()
    {
        foreach (var address in server.Features.Get<IServerAddressesFeature>()?.Addresses ?? [])
        {
            // Kestrel's "http://*:8383" and "http://[::]:8383" aren't URIs System.Uri accepts; the port is after the last colon.
            if (address.LastIndexOf(':') is var colon and > 0 && int.TryParse(address[(colon + 1)..].TrimEnd('/'), out var port))
            {
                return port;
            }
        }

        return 8383;
    }

    /// <summary>The hash for <paramref name="name"/> in a SHA256SUMS file (shasum's "&lt;hash&gt;  &lt;name&gt;" lines).</summary>
    public static string? ChecksumFor(string sums, string name)
    {
        foreach (var line in sums.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && parts[1].TrimStart('*') == name && parts[0].Length == 64 && parts[0].All(Uri.IsHexDigit))
            {
                return parts[0].ToLowerInvariant();
            }
        }

        return null;
    }

    /// <summary>
    /// The shared framework the app at <paramref name="app"/> needs and <paramref name="dotnetRoot"/> doesn't have (as
    /// "ASP.NET Core 11.0"), or null. A newer patch of the same version counts: the app rolls forward to it.
    /// </summary>
    public static string? MissingRuntime(string app, string? dotnetRoot)
    {
        var config = Directory.EnumerateFiles(app, "*.runtimeconfig.json").FirstOrDefault();
        if (config is null || dotnetRoot is null)
        {
            return null;
        }

        var runtime = JsonNode.Parse(File.ReadAllText(config))?["runtimeOptions"];
        var frameworks = runtime?["frameworks"]?.AsArray().Select(f => f) ?? (runtime?["framework"] is { } single ? [single] : []);
        foreach (var framework in frameworks)
        {
            if (framework?["name"]?.GetValue<string>() is not { } name || !Version.TryParse(framework["version"]?.GetValue<string>(), out var needed))
            {
                continue;
            }

            var folder = Path.Combine(dotnetRoot, "shared", name);
            var installed = Directory.Exists(folder)
                ? Directory.EnumerateDirectories(folder).Select(d => Version.TryParse(Path.GetFileName(d), out var v) ? v : null)
                : [];
            if (!installed.Any(v => v is not null && v.Major == needed.Major && v >= needed))
            {
                var label = name == "Microsoft.AspNetCore.App" ? "ASP.NET Core" : name == "Microsoft.NETCore.App" ? ".NET" : name;
                return $"{label} {needed.Major}.{needed.Minor}";
            }
        }

        return null;
    }

    /// <summary>Copies the running build's recent web files into the new one, where it hasn't the same file.</summary>
    public static void KeepRecentWebFiles(string from, string to, DateTime utcNow)
    {
        if (!Directory.Exists(from))
        {
            return;
        }

        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
        {
            var target = Path.Combine(to, Path.GetFileName(file));
            if (!File.Exists(target) && utcNow - File.GetLastWriteTimeUtc(file) < KeepWebFiles)
            {
                File.Copy(file, target);
                File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(file));
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Downloading Flexarr {Version} to update")]
    private partial void LogStarting(string version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Restarting to update to Flexarr {Version}; the update's own log is {Log}")]
    private partial void LogHandedOver(string version, string log);

    [LoggerMessage(Level = LogLevel.Error, Message = "The update to Flexarr {Version} failed")]
    private partial void LogFailed(string version, Exception ex);
}

/// <summary>An update that can't go ahead, with the reason in words for Settings › Updates.</summary>
public sealed class UpdateException(string message) : Exception(message);
