using Flexarr.Api.Profiles;
using Flexarr.Core.Media;

namespace Flexarr.Api.Watchlist;

/// <summary>The current profile's watchlist. A title is named by its library id, or by "movie"/"tv" and its TMDB id.</summary>
public static class WatchlistEndpoints
{
    public static RouteGroupBuilder MapWatchlistEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/watchlist").WithTags("Watchlist").NotForKids();

        group.MapGet("/", (WatchlistService watchlist, CurrentProfile profile, CancellationToken ct) => watchlist.ListAsync(profile.Id, ct));

        // The library titles on the list that are ready to play, newest on it first, without asking Seerr: for widgets.
        group.MapGet("/ready", (int? take, WatchlistService watchlist, CurrentProfile profile, CancellationToken ct) =>
            watchlist.ReadyAsync(profile.Id, Math.Clamp(take ?? 20, 1, 50), ct));

        // Everything on the list, cheaply: for poster menus that offer Add or Remove.
        group.MapGet("/keys", (WatchlistService watchlist, CurrentProfile profile, CancellationToken ct) => watchlist.AllKeysAsync(profile.Id, ct));

        // For a title page's button: whether it's on the list.
        group.MapGet("/contains", (Guid? libraryItemId, string? mediaType, int? tmdbId, WatchlistService watchlist, CurrentProfile profile, CancellationToken ct) =>
            watchlist.MembershipAsync(profile.Id, new WatchlistAdd(libraryItemId, mediaType, tmdbId), ct));

        group.MapPost("/", async (WatchlistAdd add, WatchlistService watchlist, CurrentProfile profile, CancellationToken ct) =>
        {
            var (added, refusal) = await watchlist.AddAsync(profile.Id, add, WatchlistSource.Flexarr, ct);
            return added is not null ? Results.Ok(added) : Results.Problem(refusal!.Message, statusCode: refusal.StatusCode);
        });

        group.MapDelete("/{id:guid}", async (Guid id, WatchlistService watchlist, CurrentProfile profile, CancellationToken ct) =>
            await watchlist.RemoveAsync(profile.Id, id, ct) ? Results.NoContent() : Results.NotFound());

        // Taking a title off from its own page, where the entry's id isn't known.
        group.MapDelete("/", async (Guid? libraryItemId, string? mediaType, int? tmdbId, WatchlistService watchlist, CurrentProfile profile, CancellationToken ct) =>
            await watchlist.RemoveAsync(profile.Id, new WatchlistAdd(libraryItemId, mediaType, tmdbId), ct) ? Results.NoContent() : Results.NotFound());

        return api;
    }
}
