using System.Diagnostics;
using System.Globalization;
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

    /// <summary>What a server with neither a given name nor a machine name to use is called.</summary>
    public const string Fallback = "Flexarr";

    private static readonly Lazy<string?> Machine = new(ReadMachineName);

    /// <summary>The name in use.</summary>
    public static string Of(FlexarrOptions options) => Resolve(options.ServerName, () => Machine.Value);

    /// <summary>
    /// Whether an admin named the server (a name that passes <see cref="Problem"/>), rather than it going by the
    /// machine's name. Family guests are only shown a name someone chose.
    /// </summary>
    public static bool IsNamed(FlexarrOptions options) => Given(options.ServerName) is not null;

    /// <summary>What the server is called when no one has named it.</summary>
    public static string Default => Resolve(null, () => Machine.Value);

    /// <summary>The machine's own name ("Dans-Mac-Mini"), or null in a container or when it can't be found.</summary>
    public static string? MachineName => Machine.Value;

    /// <summary>Looks the machine's name up now (once), so no request waits for it later.</summary>
    public static void Warm() => _ = Machine.Value;

    internal static string Resolve(string? set, Func<string?> machine) =>
        Given(set) ?? (machine() is { Length: > 0 } name && Problem(name) is null ? name : Fallback);

    /// <summary>
    /// A name from config.json, an environment variable or a restored backup counts only if it would have been
    /// accepted by Settings: one that wouldn't goes back to the default rather than reaching the announcement.
    /// </summary>
    private static string? Given(string? set) => set?.Trim() is { Length: > 0 } name && Problem(name) is null ? name : null;

    /// <summary>Why <paramref name="name"/> can't be the server's name, or null when it can (empty means the default).</summary>
    public static string? Problem(string name)
    {
        name = name.Trim();
        if (name.Length > MaxLength)
        {
            return Loc.Mark("Keep the name to {max} characters or fewer.");
        }

        // Letters outside English take two to four bytes each, and Bonjour allows 63.
        if (Encoding.UTF8.GetByteCount(name) > MaxBytes)
        {
            return Loc.Mark("That name is too long.");
        }

        // Line breaks, tabs, and invisible characters that could reverse or hide the text around the name.
        if (name.Any(c => char.IsControl(c) || CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.Format or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator))
        {
            return Loc.Mark("The name can’t contain line breaks, tabs or invisible characters.");
        }

        // The network announcement reads "._" as the start of the service's type.
        return name.Contains("._", StringComparison.Ordinal) ? Loc.Mark("The name can’t contain “._”.") : null;
    }

    /// <summary>
    /// macOS's Bonjour name ("Dans-Mac-Mini"), else the host name without ".local". None in a container, where the
    /// name means nothing to anyone, or if it can't be found: the server is then "Flexarr".
    /// </summary>
    private static string? ReadMachineName()
    {
        try
        {
            if (Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true")
            {
                return null;
            }

            // The Mac app passes its Bonjour name in: its sandbox can't run scutil.
            if (Usable(Environment.GetEnvironmentVariable("Flexarr__MacApp__HostName")) is { } macApp)
            {
                return macApp;
            }

            if (OperatingSystem.IsMacOS() && Scutil() is { } bonjour)
            {
                return bonjour;
            }

            var name = System.Net.Dns.GetHostName();
            return Usable(name.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ? name[..^".local".Length] : name) ?? Usable(Environment.MachineName);
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or InvalidOperationException or System.ComponentModel.Win32Exception
                                       or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? Usable(string? name) =>
        name?.Trim() is { Length: > 0 } trimmed && !trimmed.Equals("localhost", StringComparison.OrdinalIgnoreCase) ? trimmed : null;

    /// <summary>`scutil --get LocalHostName`, given two seconds before it's given up on.</summary>
    private static string? Scutil()
    {
        var start = new ProcessStartInfo("/usr/sbin/scutil") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--get");
        start.ArgumentList.Add("LocalHostName");
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        if (!output.Wait(TimeSpan.FromSeconds(2)))
        {
            process.Kill();
            return null;
        }

        return Usable(output.Result);
    }
}
