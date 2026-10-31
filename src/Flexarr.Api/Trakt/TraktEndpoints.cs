using Flexarr.Api.Profiles;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Api.Trakt;

/// <param name="Available">The server can offer Trakt (it has Flexarr's app through the relay, or its own).</param>
/// <param name="State">"disconnected", "connected" or "needsSignIn".</param>
/// <param name="Pending">A connect in progress (or just ended, saying how).</param>
/// <param name="Queued">Changes waiting to go to Trakt.</param>
/// <param name="Importing">The history import is running.</param>
public sealed record TraktStatusDto(
    bool Available,
    string State,
    string? Username,
    DateTime? ConnectedAt,
    DateTime? LastSyncAt,
    string? LastError,
    bool WatchlistFull,
    DateTime? ImportedAt,
    bool Importing,
    int Queued,
    TraktPendingConnect? Pending);

/// <summary>One profile's Trakt, for the admin's overview; never the tokens.</summary>
public sealed record TraktProfileDto(string ProfileId, string ProfileName, string State, string? Username, DateTime? LastSyncAt, string? LastError);

/// <summary>The current profile's Trakt account: connect by code, status, the import, disconnect.</summary>
public static class TraktEndpoints
{
    public static RouteGroupBuilder MapTraktEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/trakt").WithTags("Trakt").NotForKids();

        group.MapGet("/status", (CurrentProfile profile, FlexarrDbContext db, TraktClient trakt, TraktConnector connector, TraktImports imports, CancellationToken ct) =>
            StatusAsync(profile.Id, db, trakt, connector, imports, ct));

        // A code to enter at trakt.tv/activate; the server waits for it, so the app can close.
        group.MapPost("/connect", async (CurrentProfile profile, TraktClient trakt, TraktConnector connector, CancellationToken ct) =>
        {
            if (await trakt.ClientIdAsync(ct) is null)
            {
                return Results.Problem("Trakt isn't available on this server.", statusCode: 409);
            }

            try
            {
                return Results.Ok(await connector.StartAsync(profile.Id, ct));
            }
            catch (TraktException ex)
            {
                return Results.Problem(ex.Message, statusCode: 502);
            }
        });

        group.MapDelete("/connect", (CurrentProfile profile, TraktConnector connector) =>
        {
            connector.Cancel(profile.Id);
            return Results.NoContent();
        });

        // Disconnect: Trakt forgets the token, and so does Flexarr, with everything queued for it, plays held from it and
        // its lists. Watch history stays.
        group.MapDelete("/", async (CurrentProfile profile, FlexarrDbContext db, TraktClient trakt, TraktConnector connector, CancellationToken ct) =>
        {
            await TraktConnections.DisconnectAsync(db, trakt, connector, profile.Id, ct);
            return Results.NoContent();
        });

        // The history import: what it would do, then (confirm=true) doing it, in the background.
        group.MapPost("/import", async (bool? confirm, CurrentProfile profile, TraktSync sync, TraktImports imports, CancellationToken ct) =>
        {
            if (confirm == true)
            {
                return imports.Start(profile.Id) ? Results.Accepted() : Results.Problem("The import is already running.", statusCode: 409);
            }

            try
            {
                return await sync.ImportAsync(profile.Id, apply: false, ct) is { } preview ? Results.Ok(preview) : Results.Problem("Connect to Trakt first.", statusCode: 409);
            }
            catch (TraktException ex)
            {
                return Results.Problem(ex.Message, statusCode: 502);
            }
        });

        // The profile's Trakt lists (its own and liked ones), and which come across as collections.
        group.MapGet("/lists", async (CurrentProfile profile, TraktLists lists, CancellationToken ct) =>
        {
            try
            {
                return await lists.AvailableAsync(profile.Id, ct) is { } found ? Results.Ok(found) : Results.Problem("Connect to Trakt first.", statusCode: 409);
            }
            catch (TraktException ex)
            {
                return Results.Problem(ex.Message, statusCode: 502);
            }
        });

        group.MapPut("/lists", async (TraktListsUpdate update, CurrentProfile profile, TraktLists lists, CancellationToken ct) =>
            await lists.ChooseAsync(profile.Id, update.Ids, ct) ? Results.NoContent() : Results.Problem("Connect to Trakt first.", statusCode: 409));

        // Sync now, for this profile only: done before answering, so the app can show when it last synced. At most once
        // a minute; sooner just asks the background sync.
        group.MapPost("/sync", async (CurrentProfile profile, TraktSync sync, CancellationToken ct) =>
        {
            if (!TraktSyncWorker.TryRunNow(profile.Id))
            {
                TraktSyncWorker.Nudge(profile.Id);
                return Results.Accepted();
            }

            await sync.SyncAsync(profile.Id, ct);
            return Results.NoContent();
        });

        // The admin's overview: who's connected, and how it's going.
        group.MapGet("/profiles", async (CurrentProfile profile, FlexarrDbContext db, CancellationToken ct) =>
        {
            if (!profile.IsAdmin)
            {
                return Results.Forbid();
            }

            var rows = await db.Profiles.AsNoTracking().OrderBy(p => p.CreatedAt)
                .Select(p => new { p.Id, p.Name, Connection = db.TraktConnections.FirstOrDefault(c => c.ProfileId == p.Id) })
                .ToListAsync(ct);
            return Results.Ok(rows.Select(r => new TraktProfileDto(r.Id, r.Name, StateOf(r.Connection), r.Connection?.Username, r.Connection?.LastSyncAt, r.Connection?.LastError)));
        });

        return api;
    }

    private static string StateOf(TraktConnection? c) => c switch
    {
        null => "disconnected",
        { Status: TraktStatus.NeedsSignIn } => "needsSignIn",
        _ => "connected",
    };

    private static async Task<TraktStatusDto> StatusAsync(string profileId, FlexarrDbContext db, TraktClient trakt, TraktConnector connector, TraktImports imports,
        CancellationToken ct)
    {
        var c = await db.TraktConnections.AsNoTracking().FirstOrDefaultAsync(x => x.ProfileId == profileId, ct);
        var queued = c is null ? 0 : await db.TraktOutbox.CountAsync(o => o.ProfileId == profileId, ct);
        return new TraktStatusDto(await trakt.ClientIdAsync(ct) is not null, StateOf(c), c?.Username, c?.ConnectedAt, c?.LastSyncAt,
            c?.LastError ?? imports.Error(profileId), c?.WatchlistFull ?? false, c?.ImportedAt, imports.Running(profileId), queued, connector.Current(profileId));
    }
}
