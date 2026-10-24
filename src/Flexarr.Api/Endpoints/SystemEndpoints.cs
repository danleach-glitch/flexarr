using System.Reflection;
using System.Runtime.InteropServices;

namespace Flexarr.Api.Endpoints;

public sealed record SystemStatus(string AppName, string Version, string OsDescription, string RuntimeVersion, DateTimeOffset StartTime);

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

        group.MapGet("/status", () => new SystemStatus(
            "Flexarr",
            Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0",
            RuntimeInformation.OSDescription,
            RuntimeInformation.FrameworkDescription,
            StartTime));

        return api;
    }
}
