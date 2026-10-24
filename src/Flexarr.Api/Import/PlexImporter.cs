using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Flexarr.Core.Paths;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Api.Import;

public sealed record PlexAccountDto(long Id, string Name, int Watched, int InProgress);

public sealed record PlexSourceDto(string? Path, bool Found, IReadOnlyList<PlexAccountDto> Accounts);

/// <param name="AccountId">The Plex account whose history to read.</param>
/// <param name="Path">Plex's database, if not in a standard place.</param>
/// <param name="ProfileId">The Flexarr profile to import into; the main profile when omitted.</param>
public sealed record PlexImportRequest(long AccountId, string? Path = null, string? ProfileId = null);

public sealed record PlexImportResult(int Matched, int Watched, int InProgress, int Unmatched, int KeptNewer);

/// <summary>
/// Brings watch history across from a Plex Media Server: watched flags, play counts and resume points,
/// matched to Flexarr's items by file path. Plex's database is copied first and only ever read.
/// </summary>
public sealed partial class PlexImporter(FlexarrDbContext db, IReadOnlyList<PathMapping> mappings, ILogger<PlexImporter> logger)
{
    private const string DatabaseName = "com.plexapp.plugins.library.db";

    /// <summary>Where Plex keeps its database on macOS, Linux packages, Docker images and Windows.</summary>
    public static IEnumerable<string> CandidatePaths()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var suffix = Path.Combine("Plex Media Server", "Plug-in Support", "Databases", DatabaseName);
        yield return Path.Combine(home, "Library", "Application Support", suffix);
        yield return Path.Combine("/var/lib/plexmediaserver/Library/Application Support", suffix);
        yield return Path.Combine("/config/Library/Application Support", suffix);
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), suffix);
    }

    public static string? Detect() => CandidatePaths().FirstOrDefault(File.Exists);

    public static async Task<PlexSourceDto> DescribeAsync(string? path, CancellationToken ct)
    {
        path ??= Detect();
        if (path is null || !File.Exists(path))
        {
            return new PlexSourceDto(path, false, []);
        }

        await using var copy = await PlexCopy.OpenAsync(path, ct);
        await using var command = copy.Connection.CreateCommand();
        command.CommandText = """
            SELECT a.id, COALESCE(NULLIF(a.name, ''), 'Plex user ' || a.id),
                   SUM(CASE WHEN s.view_count > 0 THEN 1 ELSE 0 END),
                   SUM(CASE WHEN s.view_offset > 0 THEN 1 ELSE 0 END)
            FROM accounts a JOIN metadata_item_settings s ON s.account_id = a.id
            GROUP BY a.id
            HAVING SUM(CASE WHEN s.view_count > 0 OR s.view_offset > 0 THEN 1 ELSE 0 END) > 0
            ORDER BY 3 DESC
            """;
        var accounts = new List<PlexAccountDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            accounts.Add(new PlexAccountDto(reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3)));
        }

        return new PlexSourceDto(path, true, accounts);
    }

    public async Task<PlexImportResult> ImportAsync(PlexImportRequest request, CancellationToken ct)
    {
        var path = request.Path ?? Detect() ?? throw new FileNotFoundException("Couldn't find a Plex database on this machine.");

        // file path → (watched count, offset ms, last viewed unix seconds), keeping the most recent per file.
        var history = new Dictionary<string, (int Views, long OffsetMs, long LastViewed)>(StringComparer.Ordinal);
        await using (var copy = await PlexCopy.OpenAsync(path, ct))
        {
            await using var command = copy.Connection.CreateCommand();
            command.CommandText = """
                SELECT p.file, s.view_count, s.view_offset, s.last_viewed_at
                FROM metadata_item_settings s
                JOIN metadata_items mi ON mi.guid = s.guid
                JOIN media_items m ON m.metadata_item_id = mi.id
                JOIN media_parts p ON p.media_item_id = m.id
                WHERE s.account_id = $account AND (s.view_count > 0 OR s.view_offset > 0) AND p.file IS NOT NULL
                """;
            command.Parameters.AddWithValue("$account", request.AccountId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var file = Normalize(PathMapper.Map(reader.GetString(0), mappings));
                var entry = (reader.IsDBNull(1) ? 0 : reader.GetInt32(1), reader.IsDBNull(2) ? 0 : reader.GetInt64(2), reader.IsDBNull(3) ? 0 : reader.GetInt64(3));
                if (!history.TryGetValue(file, out var existing) || entry.Item3 > existing.LastViewed)
                {
                    history[file] = entry;
                }
            }
        }

        var files = await db.MediaFiles.AsNoTracking()
            .Where(f => f.MediaItem!.Kind == MediaKind.Movie || f.MediaItem.Kind == MediaKind.Episode)
            .Select(f => new { f.Path, f.MediaItemId, f.DurationSeconds })
            .ToListAsync(ct);
        var byPath = new Dictionary<string, (Guid Item, double? Duration)>(StringComparer.Ordinal);
        foreach (var f in files)
        {
            byPath.TryAdd(Normalize(f.Path), (f.MediaItemId, f.DurationSeconds));
        }

        var profileId = request.ProfileId ?? PlaybackState.DefaultUser;
        if (!await db.Profiles.AnyAsync(p => p.Id == profileId, ct))
        {
            throw new FileNotFoundException("That Flexarr profile doesn't exist.");
        }

        var states = await db.PlaybackStates.Where(p => p.UserId == profileId).ToDictionaryAsync(p => p.MediaItemId, ct);
        int matched = 0, watched = 0, inProgress = 0, keptNewer = 0;

        // Several files (versions) can belong to one item; the latest viewing wins.
        foreach (var group in history.Where(h => byPath.ContainsKey(h.Key)).GroupBy(h => byPath[h.Key].Item))
        {
            matched++;
            var (file, (views, offsetMs, lastViewed)) = group.MaxBy(g => g.Value.LastViewed);
            var at = lastViewed > 0 ? DateTimeOffset.FromUnixTimeSeconds(lastViewed).UtcDateTime : DateTime.UtcNow.AddYears(-1);

            if (states.TryGetValue(group.Key, out var state) && state.LastPlayedAt > at)
            {
                keptNewer++;
                continue;
            }

            if (state is null)
            {
                state = new PlaybackState { UserId = profileId, MediaItemId = group.Key };
                db.PlaybackStates.Add(state);
                states[group.Key] = state;
            }

            state.LastPlayedAt = at;
            state.PlayCount = Math.Max(state.PlayCount, views);
            state.DurationSeconds = byPath[file].Duration;
            if (offsetMs > 0)
            {
                // Plex keeps an offset for a rewatch in progress; resume it.
                state.Watched = false;
                state.PositionSeconds = offsetMs / 1000.0;
                inProgress++;
            }
            else
            {
                state.Watched = views > 0;
                state.PositionSeconds = 0;
                watched++;
            }
        }

        await db.SaveChangesAsync(ct);
        var result = new PlexImportResult(matched, watched, inProgress, history.Count - history.Keys.Count(byPath.ContainsKey), keptNewer);
        LogImported(result.Matched, result.Watched, result.InProgress, result.Unmatched);
        return result;
    }

    private static string Normalize(string path) => path.Normalize(System.Text.NormalizationForm.FormC);

    [LoggerMessage(Level = LogLevel.Information, Message = "Imported Plex history: {Matched} items matched ({Watched} watched, {InProgress} in progress), {Unmatched} files not in Flexarr")]
    private partial void LogImported(int matched, int watched, int inProgress, int unmatched);

    /// <summary>A private copy of Plex's database (and its write-ahead log), deleted when disposed.</summary>
    private sealed class PlexCopy : IAsyncDisposable
    {
        private readonly string _directory;

        private PlexCopy(string directory, SqliteConnection connection)
        {
            _directory = directory;
            Connection = connection;
        }

        public SqliteConnection Connection { get; }

        public static async Task<PlexCopy> OpenAsync(string path, CancellationToken ct)
        {
            var directory = Directory.CreateTempSubdirectory("flexarr-plex-").FullName;
            var target = Path.Combine(directory, DatabaseName);
            File.Copy(path, target);
            if (File.Exists(path + "-wal"))
            {
                File.Copy(path + "-wal", target + "-wal");
            }

            var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = target, Pooling = false }.ToString());
            await connection.OpenAsync(ct);
            return new PlexCopy(directory, connection);
        }

        public async ValueTask DisposeAsync()
        {
            await Connection.DisposeAsync();
            Directory.Delete(_directory, recursive: true);
        }
    }
}
