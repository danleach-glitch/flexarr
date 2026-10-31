using System.Globalization;
using System.Text.Json;
using Flexarr.Api.Collections;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Flexarr.Core.Scanning;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Api.Trakt;

/// <param name="Id">"{user slug}/{list id}": what <c>PUT /trakt/lists</c> takes.</param>
/// <param name="Liked">Someone else's list this profile liked on Trakt.</param>
/// <param name="Selected">Shown as a collection here.</param>
public sealed record TraktListDto(string Id, string Name, string? Owner, int ItemCount, bool Liked, bool Selected);

public sealed record TraktListsUpdate(IReadOnlyList<string> Ids);

/// <summary>
/// A profile's Trakt lists, its own and those it liked, and the ones it chose kept in step as its personal collections:
/// the titles here in Trakt's order, and films that aren't here yet shown with Request, as franchises show theirs.
/// </summary>
public sealed class TraktLists(FlexarrDbContext db, TraktClient trakt, TraktAccounts accounts)
{
    /// <summary>Lists are looked at again after this long, as someone else's liked list changes without a word to us.</summary>
    internal static readonly TimeSpan Refresh = TimeSpan.FromHours(6);

    /// <summary>Most lists a profile can bring across.</summary>
    public const int MaxLists = 20;

    public async Task<List<TraktListDto>?> AvailableAsync(string profileId, CancellationToken ct)
    {
        var connection = await db.TraktConnections.AsNoTracking().FirstOrDefaultAsync(c => c.ProfileId == profileId, ct);
        if (connection is null)
        {
            return null;
        }

        var (ok, lists) = await accounts.WithTokenAsync(profileId, async token =>
        {
            var own = await trakt.ListsAsync(token, ct);
            var liked = await trakt.LikedListsAsync(token, ct);
            return own.Select(l => Dto(l, connection.UserSlug, liked: false, connection.ListIds))
                .Concat(liked.Select(l => Dto(l.List, l.List.User?.Ids.Slug ?? "", liked: true, connection.ListIds)))
                .Where(l => !l.Id.StartsWith('/'))
                .DistinctBy(l => l.Id)
                .ToList();
        }, ct);
        return ok ? lists : null;
    }

    private static TraktListDto Dto(TraktList l, string slug, bool liked, List<string> selected)
    {
        var id = $"{slug}/{l.Ids.Trakt?.ToString(CultureInfo.InvariantCulture) ?? l.Ids.Slug}";
        return new TraktListDto(id, l.Name, liked ? l.User?.Username : null, l.ItemCount, liked, selected.Contains(id));
    }

    /// <summary>Chooses the lists to bring across; those no longer chosen go. False if the profile isn't connected.</summary>
    public async Task<bool> ChooseAsync(string profileId, IReadOnlyList<string> ids, CancellationToken ct)
    {
        if (await db.TraktConnections.FirstOrDefaultAsync(c => c.ProfileId == profileId, ct) is not { } connection)
        {
            return false;
        }

        connection.ListIds = ids.Where(i => i.Contains('/', StringComparison.Ordinal) && i.Length <= 160).Distinct().Take(MaxLists).ToList();
        connection.ListsSyncedAt = null;
        await db.Collections.Where(c => c.OwnerProfileId == profileId && c.Kind == CollectionKind.TraktList && !connection.ListIds.Contains(c.TraktListId!))
            .ExecuteDeleteAsync(ct);
        await db.SaveChangesAsync(ct);
        TraktSyncWorker.Nudge();
        return true;
    }

    /// <summary>Whether the chosen lists are due a look: changed on Trakt, newly chosen, or a while since.</summary>
    internal static bool Due(TraktConnection connection, TraktLastActivities now, TraktLastActivities? before) =>
        connection.ListIds.Count > 0
        && (connection.ListsSyncedAt is null || DateTime.UtcNow - connection.ListsSyncedAt > Refresh
            || now.Lists?.UpdatedAt != before?.Lists?.UpdatedAt || now.Lists?.LikedAt != before?.Lists?.LikedAt);

    /// <summary>Brings each chosen list across as a personal collection. Saved by the caller.</summary>
    internal async Task SyncAsync(TraktConnection connection, string token, CancellationToken ct)
    {
        var profileId = connection.ProfileId;
        var existing = await db.Collections.Include(c => c.Items)
            .Where(c => c.OwnerProfileId == profileId && c.Kind == CollectionKind.TraktList).ToListAsync(ct);
        var titles = await LibraryTitles.LoadAsync(db, ct);
        var known = await AvailableAsync(profileId, ct) ?? [];
        foreach (var listId in connection.ListIds)
        {
            var slash = listId.IndexOf('/', StringComparison.Ordinal);
            List<TraktListItem> items;
            try
            {
                items = await trakt.ListItemsAsync(token, listId[..slash], listId[(slash + 1)..], ct);
            }
            catch (TraktException ex) when (ex.Status is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Forbidden)
            {
                // Deleted on Trakt, or made private: it goes here too.
                existing.Where(c => c.TraktListId == listId).ToList().ForEach(c => db.Collections.Remove(c));
                continue;
            }

            var collection = existing.FirstOrDefault(c => c.TraktListId == listId);
            if (collection is null)
            {
                collection = new Collection { Name = "Trakt list", Kind = CollectionKind.TraktList, OwnerProfileId = profileId, TraktListId = listId };
                db.Collections.Add(collection);
            }

            if (known.FirstOrDefault(k => k.Id == listId) is { } info)
            {
                collection.Name = info.Name.Length > 120 ? info.Name[..120] : info.Name;
            }

            var ordered = items.Where(i => i.Type is "movie" or "show").OrderBy(i => i.Rank ?? int.MaxValue).ThenBy(i => i.ListedAt).ToList();
            var wanted = new List<Guid>();
            var missing = new List<MissingFilm>();
            foreach (var i in ordered)
            {
                var (kind, ids, title, year) = i.Movie is { } m ? (MediaKind.Movie, m.Ids, m.Title, m.Year) : (MediaKind.Series, i.Show!.Ids, i.Show.Title, i.Show.Year);
                var key = new TitleKey(kind, ids.Tmdb?.ToString(CultureInfo.InvariantCulture), ids.Tvdb?.ToString(CultureInfo.InvariantCulture), ids.Imdb);
                if (titles.Title(key) is { } item)
                {
                    if (!wanted.Contains(item))
                    {
                        wanted.Add(item);
                    }
                }
                else if (kind == MediaKind.Movie && ids.Tmdb is { } tmdb && title is not null)
                {
                    // Films only: a missing title is requested as a film (shows not here are left out for now).
                    missing.Add(new MissingFilm(tmdb, title, year, null, null));
                }
            }

            collection.MissingJson = missing.Count > 0 ? JsonSerializer.Serialize(missing, FranchiseSync.Json) : null;
            collection.Items.RemoveAll(c => !wanted.Contains(c.MediaItemId));
            for (var p = 0; p < wanted.Count; p++)
            {
                if (collection.Items.FirstOrDefault(c => c.MediaItemId == wanted[p]) is { } kept)
                {
                    kept.Position = p;
                }
                else
                {
                    collection.Items.Add(new CollectionItem { MediaItemId = wanted[p], Position = p });
                }
            }

            collection.UpdatedAt = DateTime.UtcNow;
        }

        connection.ListsSyncedAt = DateTime.UtcNow;
    }
}
