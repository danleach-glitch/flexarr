using System.Text.Json;
using System.Text.Json.Nodes;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Flexarr.Core.Paths;
using Flexarr.Integrations.Arr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Configuration;

public enum ArrFolderState
{
    /// <summary>The folder is here at the same path.</summary>
    Found,

    /// <summary>A saved mapping leads to a folder that's here.</summary>
    Mapped,

    /// <summary>Not here, but the titles in it say where it is (<see cref="ArrFolderDto.Suggested"/>).</summary>
    Suggested,

    /// <summary>Not here, and nothing says where it is.</summary>
    Missing,
}

/// <summary>
/// One of an app's root folders, and where Flexarr finds it.
/// </summary>
/// <param name="Local">Where it is on this server, when found or mapped.</param>
/// <param name="Agreeing">For a suggestion: how many titles it explains, of <paramref name="Compared"/>.</param>
/// <param name="Certain">Enough titles agree to have saved it unasked.</param>
public sealed record ArrFolderDto(string App, ArrKind Kind, string Path, ArrFolderState State, string? Local, PathMapping? Suggested,
    int Agreeing, int Compared, bool Certain);

/// <summary>
/// Finds out where Sonarr, Radarr and Lidarr's root folders are on this server: there already, through a saved mapping,
/// or (when they run in Docker and see the media elsewhere) worked out from titles both sides have.
/// </summary>
public sealed partial class PathMappingFinder(FlexarrDbContext db, ArrClient arr, IOptionsMonitor<FlexarrOptions> options, ILogger<PathMappingFinder> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Every connected app's root folders. An app that can't be reached is left out: its connection says why.</summary>
    public async Task<IReadOnlyList<ArrFolderDto>> CheckAsync(CancellationToken ct)
    {
        var mappings = options.CurrentValue.PathMappings;
        var found = new List<ArrFolderDto>();
        foreach (var instance in options.CurrentValue.Arr)
        {
            IReadOnlyList<string> roots;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                roots = (await arr.GetRootFoldersAsync(instance, timeout.Token)).Select(r => r.Path).Where(p => p.Length > 0).ToList();
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
            {
                continue;
            }

            // Both sides' title folders, fetched only if a folder isn't found as it is.
            (IReadOnlyList<string> Theirs, IReadOnlyList<string> Ours)? titles = null;
            foreach (var root in roots)
            {
                var mapped = PathMapper.Map(root, mappings);
                if (Directory.Exists(mapped))
                {
                    var same = PathComparison.Key(mapped) == PathComparison.Key(root);
                    found.Add(new(instance.Name, instance.Kind, root, same ? ArrFolderState.Found : ArrFolderState.Mapped, mapped, null, 0, 0, false));
                    continue;
                }

                titles ??= await TitlesAsync(instance, ct);
                var detected = titles is { } t ? PathMappingDetector.Infer(root, t.Theirs, t.Ours) : null;
                if (detected is not null && Directory.Exists(detected.To))
                {
                    found.Add(new(instance.Name, instance.Kind, root, ArrFolderState.Suggested, null, new PathMapping { From = detected.From, To = detected.To },
                        detected.Agreeing, detected.Compared, detected.Certain));
                }
                else if (await SameNamedFolderAsync(root, ct) is { } folder)
                {
                    found.Add(new(instance.Name, instance.Kind, root, ArrFolderState.Suggested, null, new PathMapping { From = root, To = folder }, 0, 0, false));
                }
                else
                {
                    found.Add(new(instance.Name, instance.Kind, root, ArrFolderState.Missing, null, null, 0, 0, false));
                }
            }
        }

        return found;
    }

    /// <summary>
    /// Saves the suggestions enough titles agree on, each only once, so one that's taken out stays out. Returns the
    /// mappings added; the caller re-matches the titles they bring in.
    /// </summary>
    public async Task<IReadOnlyList<PathMapping>> ApplyCertainAsync(CancellationToken ct)
    {
        if (options.CurrentValue.Arr.Count == 0)
        {
            return [];
        }

        var current = options.CurrentValue;
        var added = (await CheckAsync(ct))
            .Where(f => f is { State: ArrFolderState.Suggested, Certain: true })
            .Select(f => f.Suggested!)
            .Where(m => !current.PathMappingsDetected.Contains(m.From) && !current.PathMappings.Any(p => p.From == m.From))
            .DistinctBy(m => m.From)
            .ToList();
        if (added.Count == 0)
        {
            return [];
        }

        await SaveAsync(current.DataDirectory, config =>
        {
            config["pathMappings"] = JsonSerializer.SerializeToNode(current.PathMappings.Concat(added), Json);
            config["pathMappingsDetected"] = new JsonArray([.. current.PathMappingsDetected.Concat(added.Select(m => m.From)).Distinct().Select(f => (JsonNode)f)]);
        }, ct);
        foreach (var mapping in added)
        {
            LogDetected(PathComparison.Printable(mapping.From), PathComparison.Printable(mapping.To));
        }

        await RematchAsync(db, ct);
        return added;
    }

    /// <summary>
    /// Films, shows and artists that aren't linked to Sonarr, Radarr or Lidarr are refreshed on their next scan, when a
    /// changed mapping may link them.
    /// </summary>
    public static Task RematchAsync(FlexarrDbContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        // Long ago rather than never, so they still count as having details meanwhile.
        return db.MediaItems
            .Where(i => i.ArrId == null && !i.MatchLocked && i.MetadataUpdatedAt != null
                && (i.Kind == MediaKind.Movie || i.Kind == MediaKind.Series || i.Kind == MediaKind.Artist))
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.MetadataUpdatedAt, DateTime.UnixEpoch), ct);
    }

    /// <summary>Writes config.json, then waits for the options to reload, so what follows sees the change.</summary>
    private async Task SaveAsync(string dataDirectory, Action<JsonObject> change, CancellationToken ct)
    {
        var reloaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watch = options.OnChange(_ => reloaded.TrySetResult());
        ServerConfigStore.Update(dataDirectory, change);
        try
        {
            await reloaded.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        }
        catch (TimeoutException)
        {
            // Applied when the watcher catches up.
        }
    }

    /// <summary>The app's title folders and Flexarr's for the same kind of title, or null when the app can't say.</summary>
    private async Task<(IReadOnlyList<string> Theirs, IReadOnlyList<string> Ours)?> TitlesAsync(ArrInstance instance, CancellationToken ct)
    {
        try
        {
            IReadOnlyList<string?> theirs = instance.Kind switch
            {
                ArrKind.Sonarr => (await arr.GetSeriesAsync(instance, ct)).Select(s => s.Path).ToList(),
                ArrKind.Radarr => (await arr.GetMoviesAsync(instance, ct)).Select(m => m.Path).ToList(),
                _ => (await arr.GetArtistsAsync(instance, ct)).Select(a => a.Path).ToList(),
            };

            // An artist's folder is the one its albums are in.
            List<string?> ours = instance.Kind switch
            {
                ArrKind.Sonarr => await db.MediaItems.Where(i => i.Kind == MediaKind.Series).Select(i => i.Path).ToListAsync(ct),
                ArrKind.Radarr => await db.MediaItems.Where(i => i.Kind == MediaKind.Movie).Select(i => i.Path).ToListAsync(ct),
                _ => (await db.MediaItems.Where(i => i.Kind == MediaKind.Album).Select(i => i.Path).ToListAsync(ct))
                    .Select(p => p is null ? null : System.IO.Path.GetDirectoryName(p)).Distinct().ToList(),
            };

            return ([.. theirs.OfType<string>()], [.. ours.OfType<string>()]);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>The one library folder with the same name as the app's (/tv and /Volumes/Media/TV), for an empty root.</summary>
    private async Task<string?> SameNamedFolderAsync(string root, CancellationToken ct)
    {
        var name = System.IO.Path.GetFileName(PathComparison.Key(root));
        if (name.Length == 0)
        {
            return null;
        }

        var matches = (await db.LibraryFolders.Select(f => f.Path).ToListAsync(ct))
            .Where(p => string.Equals(System.IO.Path.GetFileName(PathComparison.Key(p)), name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Added a path mapping from {From} to {To}, worked out from titles both sides have")]
    private partial void LogDetected(string from, string to);
}
