using Flexarr.Core.Libraries;
using Flexarr.Core.LiveTv;
using Flexarr.Core.Media;
using Flexarr.Core.Notifications;
using Flexarr.Core.Podcasts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Flexarr.Core.Data;

public sealed class FlexarrDbContext(DbContextOptions<FlexarrDbContext> options) : DbContext(options)
{
    public DbSet<Library> Libraries => Set<Library>();
    public DbSet<LibraryFolder> LibraryFolders => Set<LibraryFolder>();
    public DbSet<MediaItem> MediaItems => Set<MediaItem>();
    public DbSet<MediaFile> MediaFiles => Set<MediaFile>();
    public DbSet<MediaStream> MediaStreams => Set<MediaStream>();
    public DbSet<Genre> Genres => Set<Genre>();
    public DbSet<Person> People => Set<Person>();
    public DbSet<Credit> Credits => Set<Credit>();
    public DbSet<PlaybackState> PlaybackStates => Set<PlaybackState>();
    public DbSet<ContinueWatchingDismissal> ContinueWatchingDismissals => Set<ContinueWatchingDismissal>();
    public DbSet<ShowPlaybackSpeed> ShowPlaybackSpeeds => Set<ShowPlaybackSpeed>();
    public DbSet<MediaMarker> MediaMarkers => Set<MediaMarker>();
    public DbSet<Profile> Profiles => Set<Profile>();
    public DbSet<Collection> Collections => Set<Collection>();
    public DbSet<CollectionItem> CollectionItems => Set<CollectionItem>();
    public DbSet<WatchTombstone> WatchTombstones => Set<WatchTombstone>();
    public DbSet<Download> Downloads => Set<Download>();
    public DbSet<Playlist> Playlists => Set<Playlist>();
    public DbSet<PlaylistItem> PlaylistItems => Set<PlaylistItem>();
    public DbSet<PodcastFeed> PodcastFeeds => Set<PodcastFeed>();
    public DbSet<PodcastEpisode> PodcastEpisodes => Set<PodcastEpisode>();
    public DbSet<PodcastSubscription> PodcastSubscriptions => Set<PodcastSubscription>();
    public DbSet<PodcastQueueEntry> PodcastQueue => Set<PodcastQueueEntry>();
    public DbSet<LiveChannel> LiveChannels => Set<LiveChannel>();
    public DbSet<LiveProgramme> LiveProgrammes => Set<LiveProgramme>();
    public DbSet<LiveFavourite> LiveFavourites => Set<LiveFavourite>();
    public DbSet<LiveLastChannel> LiveLastChannels => Set<LiveLastChannel>();
    public DbSet<RecordingRule> RecordingRules => Set<RecordingRule>();
    public DbSet<Recording> Recordings => Set<Recording>();
    public DbSet<NotificationDevice> NotificationDevices => Set<NotificationDevice>();
    public DbSet<MediaRequest> MediaRequests => Set<MediaRequest>();
    public DbSet<NotificationCursor> NotificationCursors => Set<NotificationCursor>();

    /// <summary>
    /// Libraries hidden from the profile this context serves. Libraries and their items in them are filtered out of
    /// every query (use <c>IgnoreQueryFilters</c> to see them). Empty for background work, which sees everything.
    /// Items and files whose files went missing from disk (<see cref="MediaItem.MissingSince"/>) are filtered out too.
    /// </summary>
    public IReadOnlyList<Guid> HiddenLibraryIds { get; set; } = [];

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);
        // Readable in the database and stable if enum members are reordered.
        configurationBuilder.Properties<Enum>().HaveConversion<string>().HaveMaxLength(32);
        // Every DateTime is stored as UTC, but SQLite hands them back without a kind, and they'd go out as local times
        // ("scanned 4 h ago" a minute after a scan, for anyone east of UTC).
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<UtcDateTimeConverter>();
    }

    private sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<Library>(e =>
        {
            e.Property(l => l.Name).HasMaxLength(200);
            e.HasMany(l => l.Folders).WithOne().HasForeignKey(f => f.LibraryId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany<MediaItem>().WithOne().HasForeignKey(i => i.LibraryId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(l => !HiddenLibraryIds.Contains(l.Id));
        });

        modelBuilder.Entity<LibraryFolder>().HasIndex(f => f.Path).IsUnique();

        modelBuilder.Entity<MediaItem>(e =>
        {
            // Every item (seasons and episodes too) carries its library, so hiding one hides all of it.
            e.HasQueryFilter(i => !HiddenLibraryIds.Contains(i.LibraryId) && i.MissingSince == null);
            e.Property(i => i.AlbumType).HasMaxLength(16);
            e.HasOne(i => i.Parent).WithMany(i => i.Children).HasForeignKey(i => i.ParentId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(i => i.Files).WithOne(f => f.MediaItem).HasForeignKey(f => f.MediaItemId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(i => new { i.LibraryId, i.ParentId, i.SortTitle });
            e.HasIndex(i => i.Path);
            e.HasIndex(i => i.AddedAt);
            // Coming soon, requests, notifications and watch history all find titles by these.
            e.HasIndex(i => i.TmdbId);
            e.HasIndex(i => i.TvdbId);
            e.HasIndex(i => i.ImdbId);
            e.HasMany(i => i.Genres).WithMany(g => g.Items).UsingEntity(j => j.ToTable("MediaItemGenres"));
            e.HasMany(i => i.Credits).WithOne(c => c.MediaItem).HasForeignKey(c => c.MediaItemId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Genre>().HasIndex(g => g.Name).IsUnique();

        modelBuilder.Entity<Person>(e =>
        {
            e.HasIndex(p => p.TmdbId);
            e.HasIndex(p => p.TvMazeId);
            e.HasIndex(p => p.Name);
            e.HasMany(p => p.Credits).WithOne(c => c.Person).HasForeignKey(c => c.PersonId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MediaFile>(e =>
        {
            e.HasIndex(f => f.Path).IsUnique();
            e.HasQueryFilter(f => f.MissingSince == null);
            e.HasMany(f => f.Streams).WithOne().HasForeignKey(s => s.MediaFileId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(f => f.Markers).WithOne().HasForeignKey(m => m.MediaFileId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Profile>(e =>
        {
            e.Property(p => p.Id).HasMaxLength(64);
            e.Property(p => p.Name).HasMaxLength(60);
            e.HasIndex(p => p.TailscaleLogin).IsUnique();
            e.HasData(new Profile { Id = PlaybackState.DefaultUser, Name = "Main profile", CreatedAt = new DateTime(2026, 10, 24, 0, 0, 0, DateTimeKind.Utc) });
        });

        modelBuilder.Entity<Collection>(e =>
        {
            e.Property(c => c.Name).HasMaxLength(120);
            e.Property(c => c.OwnerProfileId).HasMaxLength(64);
            // A profile's personal collections go with it.
            e.HasOne<Profile>().WithMany().HasForeignKey(c => c.OwnerProfileId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(c => c.Items).WithOne().HasForeignKey(i => i.CollectionId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(c => c.TmdbCollectionId);
        });

        modelBuilder.Entity<Download>(e =>
        {
            e.Property(d => d.ProfileId).HasMaxLength(64);
            e.Property(d => d.Error).HasMaxLength(500);
            // A profile's downloads go with it, and so do a title's when it leaves the library.
            e.HasOne<Profile>().WithMany().HasForeignKey(d => d.ProfileId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(d => d.MediaItem).WithMany().HasForeignKey(d => d.MediaItemId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(d => new { d.ProfileId, d.MediaItemId });
            e.HasIndex(d => d.Status);
        });

        modelBuilder.Entity<CollectionItem>(e =>
        {
            e.HasKey(i => new { i.CollectionId, i.MediaItemId });
            // A title that leaves the library leaves its collections.
            e.HasOne(i => i.MediaItem).WithMany().HasForeignKey(i => i.MediaItemId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Playlist>(e =>
        {
            e.Property(p => p.Name).HasMaxLength(120);
            e.Property(p => p.ProfileId).HasMaxLength(64);
            // A profile's playlists go with it.
            e.HasOne<Profile>().WithMany().HasForeignKey(p => p.ProfileId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(p => p.Items).WithOne().HasForeignKey(i => i.PlaylistId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(p => new { p.ProfileId, p.Name });
        });

        modelBuilder.Entity<PlaylistItem>(e =>
        {
            // A track that leaves the library leaves every playlist it was in.
            e.HasOne(i => i.MediaItem).WithMany().HasForeignKey(i => i.MediaItemId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(i => new { i.PlaylistId, i.Position });
        });

        modelBuilder.Entity<WatchTombstone>(e =>
        {
            // Read back whole and only by id, so stored as JSON rather than tables of their own.
            e.OwnsMany(t => t.Playback, b => b.ToJson());
            e.OwnsMany(t => t.Collections, b => b.ToJson());
            e.HasIndex(t => t.TmdbId);
            e.HasIndex(t => t.TvdbId);
            e.HasIndex(t => t.ImdbId);
        });

        modelBuilder.Entity<PodcastFeed>(e =>
        {
            e.HasKey(f => f.MediaItemId);
            // The podcast's item is the feed: removing one removes the other, episodes and subscriptions with it.
            e.HasOne(f => f.MediaItem).WithOne(i => i.Feed).HasForeignKey<PodcastFeed>(f => f.MediaItemId).OnDelete(DeleteBehavior.Cascade);
            e.Property(f => f.Url).HasMaxLength(2048);
            e.HasIndex(f => f.Url).IsUnique();
            e.HasIndex(f => f.ITunesId);
            e.HasIndex(f => f.NextCheckAt);
            e.Property(f => f.Error).HasMaxLength(500);
        });

        modelBuilder.Entity<PodcastEpisode>(e =>
        {
            e.HasKey(p => p.MediaItemId);
            e.HasOne(p => p.MediaItem).WithOne(i => i.Enclosure).HasForeignKey<PodcastEpisode>(p => p.MediaItemId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<PodcastFeed>().WithMany().HasForeignKey(p => p.FeedId).OnDelete(DeleteBehavior.Cascade);
            e.Property(p => p.AudioUrl).HasMaxLength(2048);
            e.HasIndex(p => new { p.FeedId, p.EpisodeGuid }).IsUnique();
            e.HasIndex(p => new { p.FeedId, p.PublishedAt });
        });

        modelBuilder.Entity<PodcastSubscription>(e =>
        {
            e.HasKey(s => new { s.ProfileId, s.FeedId });
            e.Property(s => s.ProfileId).HasMaxLength(64);
            // A profile's subscriptions go with it, and everyone's with a podcast that's removed.
            e.HasOne<Profile>().WithMany().HasForeignKey(s => s.ProfileId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(s => s.Feed).WithMany().HasForeignKey(s => s.FeedId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(s => s.FeedId);
        });

        modelBuilder.Entity<PodcastQueueEntry>(e =>
        {
            e.HasKey(q => new { q.ProfileId, q.EpisodeId });
            e.Property(q => q.ProfileId).HasMaxLength(64);
            // A profile's Up Next goes with it, and an episode that leaves the library leaves it.
            e.HasOne<Profile>().WithMany().HasForeignKey(q => q.ProfileId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(q => q.Episode).WithMany().HasForeignKey(q => q.EpisodeId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(q => new { q.ProfileId, q.Position });
        });

        modelBuilder.Entity<LiveChannel>(e =>
        {
            e.HasIndex(c => c.SourceId).IsUnique();
            e.Property(c => c.Name).HasMaxLength(200);
            e.Property(c => c.Group).HasMaxLength(200);
            e.Property(c => c.LogoUrl).HasMaxLength(2048);
            e.Property(c => c.GuideId).HasMaxLength(200);
        });

        modelBuilder.Entity<LiveProgramme>(e =>
        {
            // A channel's guide goes with it.
            e.HasOne<LiveChannel>().WithMany().HasForeignKey(p => p.ChannelId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(p => new { p.ChannelId, p.Start });
            e.HasIndex(p => p.End);
            e.Property(p => p.Title).HasMaxLength(500);
            e.Property(p => p.Subtitle).HasMaxLength(500);
            e.Property(p => p.Category).HasMaxLength(100);
            e.Property(p => p.ImageUrl).HasMaxLength(2048);
        });

        modelBuilder.Entity<LiveFavourite>(e =>
        {
            e.HasKey(f => new { f.ProfileId, f.ChannelId });
            e.Property(f => f.ProfileId).HasMaxLength(64);
            // A profile's favourites go with it, and everyone's with a channel the source drops.
            e.HasOne<Profile>().WithMany().HasForeignKey(f => f.ProfileId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<LiveChannel>().WithMany().HasForeignKey(f => f.ChannelId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(f => f.ChannelId);
        });

        modelBuilder.Entity<RecordingRule>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.Title).HasMaxLength(500);
            e.Property(r => r.CreatedBy).HasMaxLength(64);
            // A series for one channel goes with it; one for any channel stays.
            e.HasOne<LiveChannel>().WithMany().HasForeignKey(r => r.ChannelId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Recording>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.ChannelName).HasMaxLength(200);
            e.Property(r => r.Title).HasMaxLength(500);
            e.Property(r => r.Subtitle).HasMaxLength(500);
            e.Property(r => r.Description).HasMaxLength(4000);
            e.Property(r => r.Category).HasMaxLength(100);
            e.Property(r => r.Reason).HasMaxLength(500);
            e.Property(r => r.FilePath).HasMaxLength(2048);
            e.Property(r => r.CreatedBy).HasMaxLength(64);
            e.Property(r => r.State).HasConversion<string>().HasMaxLength(20);
            // Not tied to the channel or the series: a recording outlives both (the channel's name is kept).
            e.HasIndex(r => new { r.State, r.Start });
            e.HasIndex(r => new { r.ChannelId, r.Start });
            e.HasIndex(r => r.RuleId);
            e.Ignore(r => r.IsActive);
        });

        modelBuilder.Entity<LiveLastChannel>(e =>
        {
            e.HasKey(l => l.ProfileId);
            e.Property(l => l.ProfileId).HasMaxLength(64);
            e.HasOne<Profile>().WithMany().HasForeignKey(l => l.ProfileId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<LiveChannel>().WithMany().HasForeignKey(l => l.ChannelId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(l => l.ChannelId);
        });

        modelBuilder.Entity<NotificationDevice>(e =>
        {
            e.HasKey(d => d.Id);
            e.Property(d => d.Id).HasMaxLength(300);
            e.Property(d => d.SignInId).HasMaxLength(64);
            e.Property(d => d.ProfileId).HasMaxLength(64);
            e.Property(d => d.Token).HasMaxLength(256);
            e.Property(d => d.Environment).HasMaxLength(16);
            e.Property(d => d.Platform).HasMaxLength(16).HasDefaultValue(NotificationPlatforms.Apple);
            e.Property(d => d.TimeZone).HasMaxLength(64);
            // A profile's phones stop hearing about it when it goes.
            e.HasOne<Profile>().WithMany().HasForeignKey(d => d.ProfileId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(d => d.Token);
        });

        modelBuilder.Entity<MediaRequest>(e =>
        {
            e.Property(r => r.ProfileId).HasMaxLength(64);
            e.HasOne<Profile>().WithMany().HasForeignKey(r => r.ProfileId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(r => new { r.FulfilledAt, r.Kind, r.TmdbId });
        });

        modelBuilder.Entity<NotificationCursor>(e =>
        {
            e.HasKey(c => c.Kind);
            e.Property(c => c.Kind).HasMaxLength(32);
        });

        modelBuilder.Entity<PlaybackState>(e =>
        {
            e.HasKey(p => new { p.UserId, p.MediaItemId });
            e.Property(p => p.UserId).HasMaxLength(64);
            e.HasOne(p => p.MediaItem).WithMany(i => i.PlaybackStates).HasForeignKey(p => p.MediaItemId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Profile>().WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(p => new { p.UserId, p.LastPlayedAt });
        });

        modelBuilder.Entity<ShowPlaybackSpeed>(e =>
        {
            e.HasKey(s => new { s.ProfileId, s.SeriesId });
            e.Property(s => s.ProfileId).HasMaxLength(64);
            e.HasOne<MediaItem>().WithMany().HasForeignKey(s => s.SeriesId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Profile>().WithMany().HasForeignKey(s => s.ProfileId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ContinueWatchingDismissal>(e =>
        {
            e.HasKey(d => new { d.ProfileId, d.SeriesId });
            e.Property(d => d.ProfileId).HasMaxLength(64);
            e.HasOne<MediaItem>().WithMany().HasForeignKey(d => d.SeriesId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Profile>().WithMany().HasForeignKey(d => d.ProfileId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
