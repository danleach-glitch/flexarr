using System.Runtime.InteropServices;

namespace Flexarr.Api.Updates;

public enum InstallKind
{
    /// <summary>Installed on a Mac by scripts/deploy-macos.sh: a LaunchAgent running ~/.flexarr/app.</summary>
    MacLaunchAgent,

    /// <summary>The Docker image. A container can't replace itself, so updating is the person's to do.</summary>
    Docker,

    /// <summary>Anything else: run from source, or set up by hand.</summary>
    Other,
}

/// <summary>How this server was installed, which decides whether it can update itself.</summary>
public interface IInstallEnvironment
{
    InstallKind Kind { get; }

    /// <summary>For a Mac install, the folder holding app/, data/ and logs/ (~/.flexarr).</summary>
    string? Root { get; }

    /// <summary>The .NET install the server runs under (~/.dotnet on a Mac), to check a release's runtime is there.</summary>
    string? DotnetRoot { get; }
}

public sealed class InstallEnvironment : IInstallEnvironment
{
    /// <summary>The LaunchAgent's label (deploy/macos/io.github.flexarr.server.plist); launchd tells its job its own.</summary>
    public const string LaunchAgentLabel = "io.github.flexarr.server";

    public InstallEnvironment()
    {
        var app = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        // The .NET runtime's own folder is <dotnet root>/shared/Microsoft.NETCore.App/<version>/.
        DotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } root
            ? root
            : Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));

        if (Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true" || File.Exists("/.dockerenv"))
        {
            Kind = InstallKind.Docker;
        }
        else if (OperatingSystem.IsMacOS() && Environment.GetEnvironmentVariable("XPC_SERVICE_NAME") == LaunchAgentLabel
            && Path.GetFileName(app) == "app" && File.Exists(Path.Combine(app, "Flexarr.dll")))
        {
            Kind = InstallKind.MacLaunchAgent;
            Root = Path.GetDirectoryName(app);
        }
        else
        {
            Kind = InstallKind.Other;
        }
    }

    public InstallKind Kind { get; }

    public string? Root { get; }

    public string? DotnetRoot { get; }
}
