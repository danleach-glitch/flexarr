using System.Diagnostics;
using System.Text;
using Flexarr.Core.Localization;

namespace Flexarr.Api.Configuration;

/// <summary>
/// What the server is called: the name an admin gave it (<see cref="FlexarrOptions.ServerName"/>), or else the
/// machine's name ("Dans-Mac-Mini"), or "Flexarr" in a container, whose machine name is a random id. It's announced
/// on the network, so the apps list it, and shown wherever the apps say which server they're on.
/// </summary>
public static class ServerName
{
    /// <summary>Longest name an admin may give, in characters.</summary>
    public const int MaxLength = 40;

    /// <summary>Bonjour's limit on a service's name, in UTF-8 bytes.</summary>
    public const int MaxBytes = 63;

    private const string Fallback = "Flexarr";

    private static readonly Lazy<string?> Machine = new(ReadMachineName);

    /// <summary>The name in use.</summary>
    public static string Of(FlexarrOptions options) => Resolve(options.ServerName, Default);

    /// <summary>What the server is called when no one has named it.</summary>
    public static string Default => Resolve(null, Machine.Value);

    internal static string Resolve(string? set, string? machine) =>
        set?.Trim() is { Length: > 0 } name ? name
        : machine is { Length: > 0 } && Encoding.UTF8.GetByteCount(machine) <= MaxBytes ? machine
        : Fallback;

    /// <summary>Why <paramref name="name"/> can't be the server's name, or null when it can (empty means the default).</summary>
    public static string? Problem(string name)
    {
        name = name.Trim();
        if (name.Length > MaxLength || Encoding.UTF8.GetByteCount(name) > MaxBytes)
        {
            return Loc.Mark("Keep the name to {max} characters or fewer.");
        }

        return name.Any(char.IsControl) ? Loc.Mark("The name can’t contain line breaks or tabs.") : null;
    }

    /// <summary>
    /// macOS's Bonjour name ("Dans-Mac-Mini"), else the host name without ".local". None in a container, where the
    /// name means nothing to anyone.
    /// </summary>
    private static string? ReadMachineName()
    {
        if (Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true")
        {
            return null;
        }

        // The Mac app passes its Bonjour name in: its sandbox can't run scutil.
        if (Environment.GetEnvironmentVariable("Flexarr__MacApp__HostName") is { Length: > 0 } macApp)
        {
            return macApp;
        }

        if (OperatingSystem.IsMacOS())
        {
            try
            {
                var start = new ProcessStartInfo("/usr/sbin/scutil") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                start.ArgumentList.Add("--get");
                start.ArgumentList.Add("LocalHostName");
                using var process = Process.Start(start)!;
                var read = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit(2000);
                if (read.Length > 0)
                {
                    return read;
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // The Mac app's sandbox runs nothing but its own FFmpeg: use the host name.
            }
        }

        var name = System.Net.Dns.GetHostName();
        name = name.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ? name[..^".local".Length] : name;
        return name.Length > 0 && !name.Equals("localhost", StringComparison.OrdinalIgnoreCase) ? name : Environment.MachineName;
    }
}
