using System.Text.Json;
using System.Text.Json.Serialization;

namespace Flexarr.Api.Trakt;

// Trakt's JSON, as far as Flexarr reads it (https://trakt.docs.apiary.io). Snake case throughout.

public sealed record TraktIds(
    int? Trakt = null,
    string? Slug = null,
    string? Imdb = null,
    int? Tmdb = null,
    int? Tvdb = null);

public sealed record TraktMovie(string? Title, int? Year, TraktIds Ids);

public sealed record TraktShow(string? Title, int? Year, TraktIds Ids);

public sealed record TraktEpisode(int Season, int Number, string? Title = null, TraktIds? Ids = null);

public sealed record TraktDeviceCode(string DeviceCode, string UserCode, string VerificationUrl, int ExpiresIn, int Interval);

public sealed record TraktToken(string AccessToken, string RefreshToken, int ExpiresIn, long CreatedAt);

public sealed record TraktUserSettings(TraktUser User);

public sealed record TraktUser(string Username, string? Name, bool? Vip, TraktIds Ids);

/// <summary>One play from <c>/sync/history</c>.</summary>
public sealed record TraktHistoryItem(long Id, DateTime WatchedAt, string Type, TraktMovie? Movie, TraktShow? Show, TraktEpisode? Episode);

/// <summary>A film from <c>/sync/watched/movies</c>: everything ever watched, with the last time.</summary>
public sealed record TraktWatchedMovie(int Plays, DateTime LastWatchedAt, TraktMovie Movie);

public sealed record TraktWatchedShow(int Plays, DateTime LastWatchedAt, TraktShow Show, IReadOnlyList<TraktWatchedSeason>? Seasons);

public sealed record TraktWatchedSeason(int Number, IReadOnlyList<TraktWatchedEpisode> Episodes);

public sealed record TraktWatchedEpisode(int Number, int Plays, DateTime LastWatchedAt);

/// <summary>A paused playback from <c>/sync/playback</c>; <see cref="Progress"/> is a percentage.</summary>
public sealed record TraktPlayback(long Id, double Progress, DateTime PausedAt, string Type, TraktMovie? Movie, TraktShow? Show, TraktEpisode? Episode);

public sealed record TraktWatchlistItem(long? Id, DateTime ListedAt, string Type, TraktMovie? Movie, TraktShow? Show);

public sealed record TraktList(string Name, string? Description, int ItemCount, string? Privacy, TraktIds Ids, TraktUser? User, DateTime? UpdatedAt);

public sealed record TraktLikedList(DateTime LikedAt, TraktList List);

public sealed record TraktListItem(int? Rank, DateTime ListedAt, string Type, TraktMovie? Movie, TraktShow? Show);

/// <summary>The parts of <c>/sync/last_activities</c> the sync compares.</summary>
public sealed record TraktLastActivities(
    DateTime All,
    TraktActivityTimes? Movies,
    TraktActivityTimes? Episodes,
    TraktActivityTimes? Shows,
    TraktActivityTimes? Watchlist,
    TraktActivityTimes? Lists);

public sealed record TraktActivityTimes(
    DateTime? WatchedAt = null,
    DateTime? PausedAt = null,
    DateTime? WatchlistedAt = null,
    DateTime? UpdatedAt = null,
    DateTime? LikedAt = null);

/// <summary>What <c>POST /sync/...</c> answers: how many changed, and what Trakt didn't know.</summary>
public sealed record TraktSyncResult(JsonElement? Added, JsonElement? Deleted, JsonElement? Existing, JsonElement? NotFound);

/// <summary>An item to send in a <c>/sync</c> body: a film, or a show with the episodes to add.</summary>
public sealed record TraktSyncMovie(TraktIds Ids, DateTime? WatchedAt = null);

public sealed record TraktSyncShow(TraktIds Ids, IReadOnlyList<TraktSyncSeason>? Seasons = null);

public sealed record TraktSyncSeason(int Number, IReadOnlyList<TraktSyncEpisode> Episodes);

public sealed record TraktSyncEpisode(int Number, DateTime? WatchedAt = null);

public sealed record TraktSyncBody(IReadOnlyList<TraktSyncMovie>? Movies = null, IReadOnlyList<TraktSyncShow>? Shows = null);

/// <summary>A scrobble: a film, or a show with the episode's season and number; progress is a percentage.</summary>
public sealed record TraktScrobbleBody(TraktSyncMovie? Movie, TraktSyncShow? Show, TraktEpisode? Episode, double Progress);

/// <summary>A film or show Trakt recommends.</summary>
public sealed record TraktRecommendation(string? Title, int? Year, TraktIds Ids);

internal static class TraktJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };
}
