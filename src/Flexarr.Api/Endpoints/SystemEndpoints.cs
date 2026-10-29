using System.Reflection;
using System.Runtime.InteropServices;
using Flexarr.Api.Configuration;
using Flexarr.Api.Security;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Endpoints;

/// <param name="RemoteUrl">
/// The server's address for away from home (over Tailscale, once remote access is turned on), or null. The apps keep
/// it beside the address they were connected with and use it when that one can't be reached.
/// </param>
/// <param name="ErrorReports">Whether the apps send error reports (Settings › Logs), and this server's random id for them.</param>
/// <param name="ServerId">
/// This server's id, which GET /setup also gives without signing in: before an app sends its sign-in to
/// <paramref name="RemoteUrl"/>, it checks the server there answers with the same one.
/// </param>
public sealed record SystemStatus(string AppName, string Version, string OsDescription, string RuntimeVersion, DateTimeOffset StartTime, string? RemoteUrl,
    ErrorReportStatus ErrorReports, string? ServerId);

public sealed record ErrorReportStatus(bool Enabled, string? InstallId);

public static class SystemEndpoints
{
    private static readonly DateTimeOffset StartTime = DateTimeOffset.UtcNow;

    public static RouteGroupBuilder MapSystemEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/system").WithTags("System");

        // What's changed, as Markdown (Settings › About). Built into the server, so it matches the running version.
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

        group.MapGet("/status", async (HttpContext http, TailscaleService tailscale, IOptionsMonitor<FlexarrOptions> options, CancellationToken ct) => new SystemStatus(
            "Flexarr",
            Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0",
            RuntimeInformation.OSDescription,
            RuntimeInformation.FrameworkDescription,
            StartTime,
            await tailscale.RemoteUrlAsync(http.Connection.LocalPort > 0 ? http.Connection.LocalPort : 8383, ct),
            options.CurrentValue.Logs is { ErrorReports: true, ErrorReportsId: { } id } ? new ErrorReportStatus(true, id) : new ErrorReportStatus(false, null),
            options.CurrentValue.ServerId));

        return api;
    }
}
