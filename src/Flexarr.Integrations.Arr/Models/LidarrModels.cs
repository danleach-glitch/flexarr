namespace Flexarr.Integrations.Arr.Models;

/// <summary>
/// An artist as Lidarr knows it. Lidarr's artist images only have local URLs (<c>/MediaCover/{id}/poster.jpg</c>, and a
/// <c>remoteUrl</c> that is a path inside its own container), so they're fetched from Lidarr itself, with its API key.
/// </summary>
public sealed record LidarrArtist
{
    public int Id { get; init; }
    public required string ArtistName { get; init; }

    /// <summary>The MusicBrainz artist id.</summary>
    public string? ForeignArtistId { get; init; }
    public string? SortName { get; init; }
    public string? Path { get; init; }
    public string? Overview { get; init; }
    public string? Disambiguation { get; init; }
    public string? ArtistType { get; init; }
    public string? Status { get; init; }
    public bool Monitored { get; init; }
    public DateTime? Added { get; init; }
    public IReadOnlyList<string> Genres { get; init; } = [];
    public IReadOnlyList<ArrImage> Images { get; init; } = [];
    public ArrRatingValue? Ratings { get; init; }

    /// <summary>Which of the artist's future albums Lidarr monitors: <c>all</c>, <c>none</c> or <c>new</c>.</summary>
    public string? MonitorNewItems { get; init; }
    public int QualityProfileId { get; init; }
    public int MetadataProfileId { get; init; }

    /// <summary>A lookup's photo, on Lidarr's image cache (images.lidarr.audio), for an artist Lidarr doesn't have.</summary>
    public string? RemotePoster { get; init; }
}

/// <summary>A release group (an album, EP, single…) as Lidarr knows it, owned or not.</summary>
public sealed record LidarrAlbum
{
    public int Id { get; init; }
    public int ArtistId { get; init; }
    public required string Title { get; init; }

    /// <summary>The MusicBrainz release group id. Tags usually carry a release id instead: see <see cref="Releases"/>.</summary>
    public string? ForeignAlbumId { get; init; }
    public string? Disambiguation { get; init; }
    public string? Overview { get; init; }
    /// <summary>MusicBrainz's primary type: Album, EP, Single, Broadcast or Other.</summary>
    public string? AlbumType { get; init; }

    /// <summary>MusicBrainz's secondary types, such as Live, Compilation, Soundtrack, Remix or Demo; usually none.</summary>
    public IReadOnlyList<string> SecondaryTypes { get; init; } = [];
    public bool Monitored { get; init; }
    public DateTime? ReleaseDate { get; init; }

    /// <summary>When Lidarr last searched for it; null if it never has.</summary>
    public DateTime? LastSearchTime { get; init; }

    /// <summary>Running time in milliseconds.</summary>
    public long Duration { get; init; }
    public IReadOnlyList<string> Genres { get; init; } = [];
    public IReadOnlyList<ArrImage> Images { get; init; } = [];
    public ArrRatingValue? Ratings { get; init; }
    public IReadOnlyList<LidarrRelease> Releases { get; init; } = [];
    public LidarrAlbumStatistics? Statistics { get; init; }

    /// <summary>The album's artist, in a lookup's answer (where <see cref="ArtistId"/> is 0 for one Lidarr doesn't have).</summary>
    public LidarrArtist? Artist { get; init; }

    /// <summary>A lookup's cover, on Lidarr's image cache, for an album Lidarr doesn't have.</summary>
    public string? RemoteCover { get; init; }
}

/// <summary>A Lidarr root folder, with what Lidarr gives artists added there unless told otherwise.</summary>
public sealed record LidarrRootFolder(int Id, string Path, int DefaultQualityProfileId, int DefaultMetadataProfileId);

/// <summary>Which release types Lidarr lists for an artist (e.g. albums only, or EPs and singles too).</summary>
public sealed record LidarrMetadataProfile(int Id, string Name);

/// <summary>Where and how an artist added for a request is kept: its root folder and Lidarr profiles.</summary>
public sealed record LidarrPlacement(string RootFolderPath, int QualityProfileId, int MetadataProfileId);

/// <summary>One pressing of an album; its MusicBrainz release id is what a tagger writes as MUSICBRAINZ_ALBUMID.</summary>
public sealed record LidarrRelease(int Id, int AlbumId, string? ForeignReleaseId, string? Title, int TrackCount);

public sealed record LidarrAlbumStatistics(int TrackFileCount, int TrackCount, int TotalTrackCount, long SizeOnDisk);

/// <summary>A music file Lidarr tracks, with the album it belongs to and its path as Lidarr sees it.</summary>
public sealed record LidarrTrackFile(int Id, int ArtistId, int AlbumId, string? Path);
