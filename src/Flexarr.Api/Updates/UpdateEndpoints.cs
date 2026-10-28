using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Flexarr.Api.Configuration;
using Flexarr.Api.Security;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Updates;

/// <param name="Version">The running version, without the commit.</param>
/// <param name="IsDevelopment">A build between releases (the household test server's): shown, never updated from here.</param>
/// <param name="CanInstall">This server can install <paramref name="Latest"/> itself (a Mac install, and the release has a build for it).</param>
/// <param name="InUse">What installing now would interrupt.</param>
public sealed record UpdatesDto(
    string Version,
    bool IsDevelopment,
    InstallKind Install,
    bool CanInstall,
    ReleaseDto? Latest,
    bool Available,
    DateTimeOffset? LastChecked,
    string? CheckError,
    bool Automatic,
    UpdateProgressDto Progress,
    UpdateResult? LastResult,
    IReadOnlyList<string> InUse);

/// <param name="Notes">The release's changes, as CHANGELOG.md's Markdown for that version.</param>
public sealed record ReleaseDto(string Version, string Notes, Uri Url, DateTimeOffset? PublishedAt);

/// <param name="Downloading">What's downloading now: "Flexarr 0.51.0", then "the latest .NET runtime" if one's needed.</param>
public sealed record UpdateProgressDto(UpdateStage Stage, string? Version, long Received, long? Total, string? Error, string? Downloading = null);

/// <param name="Automatic">Install new releases by themselves overnight (only where Flexarr can update itself).</param>
public sealed record UpdateSettingsUpdate(bool? Automatic);

public static class UpdateEndpoints
{
    public static RouteGroupBuilder MapUpdateEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/updates").WithTags("Updates").RequireAuthorization(FlexarrClaims.AdminPolicy);

        group.MapGet("/", async (UpdateChecker checker, MacUpdater updater, ServerActivity activity, IInstallEnvironment install,
            IOptionsMonitor<FlexarrOptions> options, CancellationToken ct) =>
        {
            // A server that's only just started hasn't made its daily check yet.
            if (checker.LastChecked is null)
            {
                await checker.CheckAsync(ct);
            }

            return await ToDtoAsync(checker, updater, activity, install, options.CurrentValue, ct);
        });

        group.MapPost("/check", async (UpdateChecker checker, MacUpdater updater, ServerActivity activity, IInstallEnvironment install,
            IOptionsMonitor<FlexarrOptions> options, CancellationToken ct) =>
        {
            await checker.CheckAsync(ct);
            return await ToDtoAsync(checker, updater, activity, install, options.CurrentValue, ct);
        });

        group.MapPut("/settings", async (UpdateSettingsUpdate update, UpdateChecker checker, MacUpdater updater, ServerActivity activity,
            IInstallEnvironment install, IOptionsMonitor<FlexarrOptions> options, CancellationToken ct) =>
        {
            var automatic = update.Automatic ?? options.CurrentValue.Updates.Automatic;
            ServerConfigStore.Update(options.CurrentValue.DataDirectory, config =>
            {
                var updates = config["updates"] as JsonObject ?? [];
                updates["automatic"] = automatic;
                config["updates"] = updates;
            });

            // The file watcher reloads options a moment after the write.
            for (var i = 0; i < 50 && options.CurrentValue.Updates.Automatic != automatic; i++)
            {
                await Task.Delay(100, ct);
            }

            return await ToDtoAsync(checker, updater, activity, install, options.CurrentValue, ct);
        });

        group.MapPost("/install", async (UpdateChecker checker, MacUpdater updater, ServerActivity activity, IInstallEnvironment install,
            IOptionsMonitor<FlexarrOptions> options, CancellationToken ct) =>
        {
            if (!CanInstall(checker, updater))
            {
                return Results.Problem(
                    checker.Available(ReleaseVersion.Current) ? "This server can't install updates itself." : "There's no newer release to install.",
                    statusCode: StatusCodes.Status409Conflict);
            }

            if (!updater.Start(checker.Latest!))
            {
                return Results.Problem("An update is already being installed.", statusCode: StatusCodes.Status409Conflict);
            }

            return Results.Accepted(value: await ToDtoAsync(checker, updater, activity, install, options.CurrentValue, ct));
        });

        return api;
    }

    private static bool CanInstall(UpdateChecker checker, MacUpdater updater) =>
        updater.CanInstall && checker.Available(ReleaseVersion.Current) && checker.Latest?.MacBuild(RuntimeInformation.OSArchitecture) is not null;

    private static async Task<UpdatesDto> ToDtoAsync(UpdateChecker checker, MacUpdater updater, ServerActivity activity, IInstallEnvironment install,
        FlexarrOptions options, CancellationToken ct)
    {
        var current = ReleaseVersion.Current;
        var available = checker.Available(current);
        return new UpdatesDto(
            current.ToString(),
            current.IsDevelopment,
            install.Kind,
            CanInstall(checker, updater),
            checker.Latest is { } latest ? new ReleaseDto(latest.Version.ToString(), latest.Notes, latest.Page, latest.PublishedAt) : null,
            available,
            checker.LastChecked,
            checker.CheckError,
            options.Updates.Automatic,
            new UpdateProgressDto(updater.Stage, updater.Installing?.ToString(), updater.Received, updater.Total, updater.Error, updater.Downloading),
            updater.LastResult(),
            available ? await activity.InUseAsync(ct) : []);
    }
}
