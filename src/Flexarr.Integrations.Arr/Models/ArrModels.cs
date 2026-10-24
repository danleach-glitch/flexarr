using System.Text.Json.Serialization;

namespace Flexarr.Integrations.Arr.Models;

public sealed record ArrImage(string CoverType, string? RemoteUrl, string? Url);

public sealed record ArrRatingValue(double Value, long Votes);

/// <summary>Radarr reports a rating per source.</summary>
public sealed record RadarrRatings(ArrRatingValue? Imdb, ArrRatingValue? Tmdb, ArrRatingValue? Metacritic, ArrRatingValue? RottenTomatoes, ArrRatingValue? Trakt);

public sealed record ArrLanguage(int Id, string Name);

public sealed record RadarrMovie
{
    public int Id { get; init; }
    public required string Title { get; init; }

    /// <summary>The film's address in Radarr's web UI: /movie/{titleSlug}.</summary>
    public string? TitleSlug { get; init; }

    /// <summary>The one file Radarr tracks for the film, if it has one.</summary>
    public ArrFile? MovieFile { get; init; }
    public string? OriginalTitle { get; init; }
    public string? SortTitle { get; init; }
    public int Year { get; init; }
    public string? Overview { get; init; }
    public string? Path { get; init; }
    public bool HasFile { get; init; }
    public bool Monitored { get; init; }
    public int TmdbId { get; init; }
    public string? ImdbId { get; init; }
    public string? Certification { get; init; }
    public string? Studio { get; init; }
    public int Runtime { get; init; }
    public string? Status { get; init; }
    public double? Popularity { get; init; }
    public string? YouTubeTrailerId { get; init; }
    public DateTime? InCinemas { get; init; }
    public DateTime? DigitalRelease { get; init; }
    public DateTime? PhysicalRelease { get; init; }
    public DateTime? ReleaseDate { get; init; }
    public DateTime? Added { get; init; }
    public IReadOnlyList<string> Genres { get; init; } = [];
    public IReadOnlyList<ArrImage> Images { get; init; } = [];
    public RadarrRatings? Ratings { get; init; }
    public ArrLanguage? OriginalLanguage { get; init; }

    /// <summary>Present on /movie/lookup results.</summary>
    public string? RemotePoster { get; init; }
}

public sealed record RadarrCredit
{
    public required string PersonName { get; init; }
    public int PersonTmdbId { get; init; }
    public string? Character { get; init; }
    public string? Job { get; init; }
    public string? Department { get; init; }
    public string Type { get; init; } = "cast";
    public int Order { get; init; }
    public IReadOnlyList<ArrImage> Images { get; init; } = [];
}

public sealed record SonarrSeason(int SeasonNumber, bool Monitored, IReadOnlyList<ArrImage>? Images);

public sealed record SonarrSeries
{
    public int Id { get; init; }
    public required string Title { get; init; }

    /// <summary>The show's address in Sonarr's web UI: /series/{titleSlug}.</summary>
    public string? TitleSlug { get; init; }
    public string? SortTitle { get; init; }
    public int Year { get; init; }
    public string? Overview { get; init; }
    public string? Path { get; init; }
    public int TvdbId { get; init; }
    public int TvMazeId { get; init; }
    public int TmdbId { get; init; }
    public string? ImdbId { get; init; }
    public string? Certification { get; init; }
    public string? Network { get; init; }
    public int Runtime { get; init; }
    public string? Status { get; init; }
    public bool Ended { get; init; }
    public DateTime? FirstAired { get; init; }
    public DateTime? Added { get; init; }
    public IReadOnlyList<string> Genres { get; init; } = [];
    public IReadOnlyList<ArrImage> Images { get; init; } = [];
    public ArrRatingValue? Ratings { get; init; }
    public IReadOnlyList<SonarrSeason> Seasons { get; init; } = [];

    /// <summary>Present on /series/lookup results.</summary>
    public string? RemotePoster { get; init; }
}

public sealed record SonarrEpisode
{
    public int Id { get; init; }
    public int SeriesId { get; init; }
    public int SeasonNumber { get; init; }
    public int EpisodeNumber { get; init; }
    public string? Title { get; init; }
    public string? Overview { get; init; }
    public string? AirDate { get; init; }
    public DateTime? AirDateUtc { get; init; }
    public int Runtime { get; init; }
    public bool HasFile { get; init; }
    public int EpisodeFileId { get; init; }
    public bool Monitored { get; init; }
    public IReadOnlyList<ArrImage> Images { get; init; } = [];

    /// <summary>Set by /calendar when includeSeries=true.</summary>
    public SonarrSeries? Series { get; init; }
}

public sealed record ArrQueueItem
{
    public int Id { get; init; }
    public int? MovieId { get; init; }
    public int? SeriesId { get; init; }
    public int? EpisodeId { get; init; }
    public string? Title { get; init; }
    public string? Status { get; init; }
    public string? TrackedDownloadState { get; init; }
    public double Size { get; init; }
    public double Sizeleft { get; init; }
    public string? Timeleft { get; init; }
    public DateTime? EstimatedCompletionTime { get; init; }
    public RadarrMovie? Movie { get; init; }
    public SonarrSeries? Series { get; init; }
    public SonarrEpisode? Episode { get; init; }

    [JsonIgnore]
    public double Progress => Size > 0 ? 1 - Sizeleft / Size : 0;
}

/// <summary>A file Sonarr or Radarr tracks (a movie file, or an episode file), with its path as they see it.</summary>
public sealed record ArrFile(int Id, string? Path);

public sealed record ArrPage<T>(int Page, int PageSize, int TotalRecords, IReadOnlyList<T> Records);
