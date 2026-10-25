using System.Reflection;
using System.Runtime.InteropServices;
using Flexarr.Api.Security;

namespace Flexarr.Api.Endpoints;

/// <param name="RemoteUrl">
/// The server's address for away from home (over Tailscale, once remote access is turned on), or null. The apps keep
/// it beside the address they were connected with and use it when that one can't be reached.
/// </param>
public sealed record SystemStatus(string AppName, string Version, string OsDescription, string RuntimeVersion, DateTimeOffset StartTime, string? RemoteUrl);

public static class SystemEndpoints
{
    private static readonly DateTimeOffset StartTime = DateTimeOffset.UtcNow;

    public static RouteGroupBuilder MapSystemEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/system").WithTags("System");

        // What's changed, as Markdown (Settings › Changelog). Built into the server, so it matches the running version.
        group.MapGet("/changelog", () =>
        {
            using var stream = typeof(SystemEndpoints).Assembly.GetManifestResourceStream("CHANGELOG.md");
            if (stream is null)
            {
                return Results.NotFound();
            }

            using var reader = new StreamReader(stream);
            return Results.Text(reader.ReadToEnd(), "text/markdown; charset=utf-8");
        });

        group.MapGet("/status", async (HttpContext http, TailscaleService tailscale, CancellationToken ct) => new SystemStatus(
            "Flexarr",
            Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0",
            RuntimeInformation.OSDescription,
            RuntimeInformation.FrameworkDescription,
            StartTime,
            await tailscale.RemoteUrlAsync(http.Connection.LocalPort > 0 ? http.Connection.LocalPort : 8383, ct)));

        return api;
    }
}
